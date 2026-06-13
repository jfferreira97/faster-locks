using System.Collections.Concurrent;
using System.Text.Json;
using FasterAlerts.Data;
using FasterAlerts.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace FasterAlerts.Services;

public class AutoTradeService(
    JupiterService jupiter,
    PumpFunMonitorService monitor,
    TradingEventLog eventLog,
    IServiceScopeFactory scopeFactory,
    IHttpClientFactory httpFactory,
    ILogger<AutoTradeService> logger)
{
    private readonly ConcurrentDictionary<string, byte> _tradedMints = new();

    // SOL/USD cached for 5 min — small price moves don't matter for MC tracking
    private double _solPriceUsd;
    private DateTimeOffset _solPriceFetchedAt = DateTimeOffset.MinValue;
    private readonly SemaphoreSlim _solPriceLock = new(1, 1);

    private async Task<double> GetSolPriceAsync()
    {
        if (_solPriceUsd > 0 && (DateTimeOffset.UtcNow - _solPriceFetchedAt).TotalMinutes < 5)
            return _solPriceUsd;

        await _solPriceLock.WaitAsync();
        try
        {
            if (_solPriceUsd > 0 && (DateTimeOffset.UtcNow - _solPriceFetchedAt).TotalMinutes < 5)
                return _solPriceUsd;

            using var http = httpFactory.CreateClient();
            var json = await http.GetStringAsync(
                "https://api.dexscreener.com/tokens/v1/solana/So11111111111111111111111111111111111111112");
            using var doc = JsonDocument.Parse(json);
            var arr = doc.RootElement;
            if (arr.ValueKind == JsonValueKind.Array && arr.GetArrayLength() > 0)
            {
                var pair = arr[0];
                if (pair.TryGetProperty("priceUsd", out var el) &&
                    double.TryParse(el.GetString(), System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out var p) && p > 0)
                {
                    _solPriceUsd = p;
                    _solPriceFetchedAt = DateTimeOffset.UtcNow;
                    eventLog.Info($"SOL price refreshed: ${p:F2}");
                }
            }
        }
        catch (Exception ex) { logger.LogWarning("SOL price fetch failed: {Err}", ex.Message); }
        finally { _solPriceLock.Release(); }

        return _solPriceUsd > 0 ? _solPriceUsd : 150.0;
    }

    // Compute MC from actual fill price rather than the pre-buy alert snapshot.
    // Total supply is derived from DexScreener's alert data (supply is fixed, price moves).
    private static double ComputeEntryMc(StreamAlert alert, double entryPriceSol, double solUsd)
    {
        double totalSupply = alert.PriceUsd > 0 && alert.MarketCapUsd > 0
            ? (double)(alert.MarketCapUsd / alert.PriceUsd)
            : 1_000_000_000.0; // pump.fun default: 1B tokens
        return entryPriceSol * solUsd * totalSupply;
    }

    public async Task RecoverActiveTradesAsync()
    {
        try
        {
            using var scope = scopeFactory.CreateScope();
            var db  = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var cfg = await db.TradingSettings.FindAsync(1) ?? new TradingSettings();
            var active = await db.Trades.Where(t => t.Status == "Active").ToListAsync();
            if (active.Count == 0) return;

            var firedByTrade = await db.TpOrders
                .Where(o => active.Select(t => t.Id).Contains(o.TradeId))
                .GroupBy(o => o.TradeId)
                .ToDictionaryAsync(g => g.Key, g => g.Select(o => o.Threshold).ToList());

            foreach (var trade in active)
            {
                _tradedMints.TryAdd(trade.TokenMint, 0);
                firedByTrade.TryGetValue(trade.Id, out var firedThresholds);
                monitor.StartMonitor(
                    trade.Id, trade.TokenMint, trade.TokenSymbol,
                    trade.TokenAmount, trade.EntryPriceSol, trade.SolSpent,
                    cfg.TrailingStopPercent, trade.EntryMarketCapUsd,
                    cfg.WalletAddress, cfg.WalletPrivateKeyBase58,
                    trade.Source, cfg.TakeProfitLevels, firedThresholds);

                var msg = $"Recovered | ${trade.TokenSymbol} | entry MC=${trade.EntryMarketCapUsd:N0} | stop reset to entry×{1.0-cfg.TrailingStopPercent/100.0:F2}";
                logger.LogInformation("♻️ {Msg}", msg);
                eventLog.Info(msg);
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to recover active trades");
            eventLog.Error($"Recovery failed: {ex.Message}");
        }
    }

    public async Task TryTradeAsync(StreamAlert alert)
    {
        var cfg = await GetSettingsAsync();
        if (!cfg.Enabled) return;

        var filterFail = FilterFailReason(alert, cfg);
        if (filterFail is not null)
        {
            logger.LogInformation("⏭  AutoTrade skip {Mint} — {Reason}", alert.TokenMint[..8], filterFail);
            WriteDecisionLog(alert, cfg, $"SKIPPED — {filterFail}");
            return;
        }

        if (await IsAlreadyActiveAsync(alert.TokenMint))
        {
            logger.LogInformation("⏭  AutoTrade skip {Mint} — already active in DB", alert.TokenMint[..8]);
            WriteDecisionLog(alert, cfg, "SKIPPED — already in active trade");
            return;
        }

        if (!_tradedMints.TryAdd(alert.TokenMint, 0))
        {
            logger.LogInformation("⏭  AutoTrade skip {Mint} — concurrent request", alert.TokenMint[..8]);
            WriteDecisionLog(alert, cfg, "SKIPPED — concurrent request");
            return;
        }

        var entryMsg = $"AutoTrade ENTER | ${alert.TokenSymbol} | MC=${alert.MarketCapUsd:N0} | locked={alert.PercentSupply:F2}%";
        logger.LogInformation("🤖 {Msg}", entryMsg);
        eventLog.Info(entryMsg);

        var (sig, outAmount) = await jupiter.BuyAsync(alert.TokenMint, cfg.BuySolLamports, cfg.WalletPrivateKeyBase58, cfg.WalletAddress);
        if (sig is null || outAmount <= 0)
        {
            var err = $"Buy FAILED | ${alert.TokenSymbol}";
            logger.LogError("❌ {Msg}", err);
            eventLog.Error(err);
            _tradedMints.TryRemove(alert.TokenMint, out _);
            WriteDecisionLog(alert, cfg, "BUY FAILED — Jupiter returned no sig");
            return;
        }

        double solSpent   = cfg.BuySolLamports / 1e9;
        double tokenUnits = outAmount / 1e6;
        double entryPrice = tokenUnits > 0 ? solSpent / tokenUnits : 0;
        double solUsd     = await GetSolPriceAsync();
        double entryMc    = ComputeEntryMc(alert, entryPrice, solUsd);

        logger.LogInformation("✅ Bought {Amount:N0} ${Symbol} | entry={Price:E3} SOL | MC=${Mc:N0} | sig={Sig}",
            tokenUnits, alert.TokenSymbol, entryPrice, entryMc, sig[..8]);
        eventLog.Info($"Entry MC (from fill): ${entryMc:N0} | SOL=${solUsd:F2}");
        WriteDecisionLog(alert, cfg, $"BOUGHT | entry MC=${entryMc:N0} | {solSpent} SOL | sig={sig[..8]}");

        int tradeId = await SaveTradeAsync(alert, sig, outAmount, entryPrice, solSpent, entryMc: entryMc);
        monitor.StartMonitor(tradeId, alert.TokenMint, alert.TokenSymbol, outAmount,
            entryPrice, solSpent, cfg.TrailingStopPercent, entryMc,
            cfg.WalletAddress, cfg.WalletPrivateKeyBase58, takeProfitLevels: cfg.TakeProfitLevels);
    }

    public async Task<(bool Ok, string Msg)> ManualBuyAsync(StreamAlert alert)
    {
        var cfg = await GetSettingsAsync();

        if (await IsAlreadyActiveAsync(alert.TokenMint))
        {
            logger.LogInformation("⏭  ManualTrade skip {Mint} — already active in DB", alert.TokenMint[..8]);
            return (false, $"${alert.TokenSymbol} is already an active position");
        }

        if (!_tradedMints.TryAdd(alert.TokenMint, 0))
        {
            logger.LogInformation("⏭  ManualTrade skip {Mint} — concurrent request", alert.TokenMint[..8]);
            return (false, "Concurrent buy request in progress — try again");
        }

        var entryMsg = $"ManualTrade ENTER | ${alert.TokenSymbol} | MC=${alert.MarketCapUsd:N0}";
        logger.LogInformation("🤖 {Msg}", entryMsg);
        eventLog.Info(entryMsg);

        var (sig, outAmount) = await jupiter.BuyAsync(alert.TokenMint, cfg.BuySolLamports, cfg.WalletPrivateKeyBase58, cfg.WalletAddress);
        if (sig is null || outAmount <= 0)
        {
            var err = $"Manual Buy FAILED | ${alert.TokenSymbol}";
            logger.LogError("❌ {Msg}", err);
            eventLog.Error(err);
            _tradedMints.TryRemove(alert.TokenMint, out _);
            return (false, $"Jupiter buy failed for ${alert.TokenSymbol} — check the Log tab");
        }

        double solSpent   = cfg.BuySolLamports / 1e9;
        double tokenUnits = outAmount / 1e6;
        double entryPrice = tokenUnits > 0 ? solSpent / tokenUnits : 0;
        double solUsd     = await GetSolPriceAsync();
        double entryMc    = ComputeEntryMc(alert, entryPrice, solUsd);

        logger.LogInformation("✅ ManualBuy {Amount:N0} ${Symbol} | entry={Price:E3} SOL | MC=${Mc:N0} | sig={Sig}",
            tokenUnits, alert.TokenSymbol, entryPrice, entryMc, sig[..8]);
        eventLog.Info($"Entry MC (from fill): ${entryMc:N0} | SOL=${solUsd:F2}");

        int tradeId = await SaveTradeAsync(alert, sig, outAmount, entryPrice, solSpent, source: "Manual", entryMc: entryMc);
        monitor.StartMonitor(tradeId, alert.TokenMint, alert.TokenSymbol, outAmount,
            entryPrice, solSpent, cfg.TrailingStopPercent, entryMc,
            cfg.WalletAddress, cfg.WalletPrivateKeyBase58, source: "Manual", takeProfitLevels: cfg.TakeProfitLevels);

        return (true, $"✓ Bought {tokenUnits:N0} ${alert.TokenSymbol} for {solSpent} SOL | sig {sig[..8]}…");
    }

    private async Task<bool> IsAlreadyActiveAsync(string mint)
    {
        if (_tradedMints.ContainsKey(mint)) return true;
        try
        {
            using var scope = scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            return await db.Trades.AnyAsync(t => t.TokenMint == mint && t.Status == "Active");
        }
        catch { return false; }
    }

    private async Task<int> SaveTradeAsync(StreamAlert alert, string sig, long tokenAmount,
        double entryPrice, double solSpent, string source = "Auto", double entryMc = 0)
    {
        try
        {
            using var scope = scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var trade = new Trade
            {
                TokenMint         = alert.TokenMint,
                TokenSymbol       = alert.TokenSymbol,
                EntryTime         = DateTimeOffset.UtcNow,
                EntryPriceSol     = entryPrice,
                EntryMarketCapUsd = entryMc > 0 ? entryMc : (double)alert.MarketCapUsd,
                SolSpent          = solSpent,
                TokenAmount       = tokenAmount,
                BuySignature      = sig,
                Status            = "Active",
                Source            = source,
                VestingDays       = alert.VestingDays,
                PercentSupply     = (double)alert.PercentSupply,
                LockedUsd         = (double)alert.UsdValue,
            };
            db.Trades.Add(trade);
            await db.SaveChangesAsync();
            return trade.Id;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to save trade record");
            return 0;
        }
    }

    public async Task<TradingSettings> GetSettingsAsync()
    {
        try
        {
            using var scope = scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            return await db.TradingSettings.FindAsync(1) ?? new TradingSettings();
        }
        catch { return new TradingSettings(); }
    }

    public async Task SaveSettingsAsync(TradingSettings settings)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var existing = await db.TradingSettings.FindAsync(1);
        if (existing is null)
        {
            settings.Id = 1;
            db.TradingSettings.Add(settings);
        }
        else
        {
            existing.Enabled             = settings.Enabled;
            existing.BuySolLamports      = settings.BuySolLamports;
            existing.TrailingStopPercent = settings.TrailingStopPercent;
            existing.MinPercentLocked    = settings.MinPercentLocked;
            existing.MinMarketCapUsd     = settings.MinMarketCapUsd;
            existing.MaxMarketCapUsd     = settings.MaxMarketCapUsd;
            existing.MaxTokenAgeHours    = settings.MaxTokenAgeHours;
            existing.MinVestingDays      = settings.MinVestingDays;
            existing.TakeProfitLevels    = settings.TakeProfitLevels;
            if (!string.IsNullOrEmpty(settings.WalletAddress))
                existing.WalletAddress = settings.WalletAddress;
            if (!string.IsNullOrEmpty(settings.WalletPrivateKeyBase58))
                existing.WalletPrivateKeyBase58 = settings.WalletPrivateKeyBase58;
        }
        await db.SaveChangesAsync();
    }

    public void ReleaseMint(string mint) => _tradedMints.TryRemove(mint, out _);

    public void ClearAllMints() => _tradedMints.Clear();

    // Returns null if alert passes all filters, or a short reason string if it fails
    private static string? FilterFailReason(StreamAlert alert, TradingSettings cfg)
    {
        if (alert.MarketCapUsd <= 0) return "MC=0";
        if (alert.MarketCapUsd < cfg.MinMarketCapUsd)
            return $"MC=${alert.MarketCapUsd:N0} < min ${cfg.MinMarketCapUsd:N0}";
        if (alert.MarketCapUsd > cfg.MaxMarketCapUsd)
            return $"MC=${alert.MarketCapUsd:N0} > max ${cfg.MaxMarketCapUsd:N0}";
        if ((double)alert.PercentSupply < cfg.MinPercentLocked)
            return $"supply={alert.PercentSupply:F2}% < min {cfg.MinPercentLocked:F2}%";
        if (alert.PairCreatedAt.HasValue &&
            (DateTimeOffset.UtcNow - alert.PairCreatedAt.Value).TotalHours > cfg.MaxTokenAgeHours)
            return $"age={(DateTimeOffset.UtcNow - alert.PairCreatedAt.Value).TotalHours:F1}h > max {cfg.MaxTokenAgeHours}h";
        if (cfg.MinVestingDays > 0 && alert.VestingDays < cfg.MinVestingDays)
            return $"vesting={alert.VestingDays}d < min {cfg.MinVestingDays}d";
        return null;
    }

    private static void WriteDecisionLog(StreamAlert alert, TradingSettings cfg, string outcome)
    {
        try
        {
            var age = alert.PairCreatedAt.HasValue
                ? $"{(DateTimeOffset.UtcNow - alert.PairCreatedAt.Value).TotalHours:F1}h old"
                : "age unknown";
            var entry =
                $"[{DateTimeOffset.UtcNow:yyyy-MM-dd HH:mm:ss}] ${alert.TokenSymbol} | {alert.TokenMint[..8]}…\n" +
                $"  Alert:   MC=${alert.MarketCapUsd:N0} | supply={alert.PercentSupply:F2}% | {age} | vesting={alert.VestingDays}d\n" +
                $"  Filters: MC=${cfg.MinMarketCapUsd:N0}–${cfg.MaxMarketCapUsd:N0} | supply≥{cfg.MinPercentLocked:F2}% | age≤{cfg.MaxTokenAgeHours}h | vesting≥{cfg.MinVestingDays}d\n" +
                $"  Result:  {outcome}\n\n";
            using var fs = new FileStream(@"C:\Users\Administrator\Desktop\trade-decisions.txt",
                FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
            using var sw = new StreamWriter(fs, System.Text.Encoding.UTF8);
            sw.Write(entry);
        }
        catch { }
    }
}
