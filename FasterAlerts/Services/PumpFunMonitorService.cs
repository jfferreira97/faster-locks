using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using FasterAlerts.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Solnet.Wallet;

namespace FasterAlerts.Services;

public record PriceTick(DateTimeOffset Time, double MarketCapUsd, double MultFromEntry);

public record PositionSnapshot(
    int    TradeId,
    string Mint,
    string Symbol,
    double EntryPriceSol,
    double CurrentPriceSol,
    double HighestPriceSol,
    double StopPriceSol,
    double SolSpent,
    double EntryMarketCapUsd,
    long   TokenAmount,
    long   RemainingTokenAmount,
    DateTimeOffset EntryTime,
    IReadOnlyList<string> Notes,
    string Source,
    IReadOnlyList<PriceTick> PriceHistory,
    string PriceSource,
    DateTimeOffset LastPriceUpdate);

public class PumpFunMonitorService(
    JupiterService jupiter,
    TradingEventLog eventLog,
    IServiceScopeFactory scopeFactory,
    IConfiguration config,
    ILogger<PumpFunMonitorService> logger)
{
    private const string PumpFunProgramId = "6EF8rrecthR5Dkzon8Nwu78hRvfCKubJ14M5uBEwF6P";

    private sealed class PositionState
    {
        public int    TradeId          { get; init; }
        public string Symbol           { get; init; } = "";
        public double EntryPriceSol    { get; init; }
        public double SolSpent         { get; init; }
        public double EntryMarketCapUsd{ get; init; }
        public long   TokenAmount      { get; init; }
        public long   RemainingTokenAmount { get; set; }
        public DateTimeOffset EntryTime{ get; init; }
        public string WalletAddress    { get; init; } = "";
        public string PrivateKey       { get; init; } = "";
        public string Source           { get; init; } = "Auto";
        public List<(int GainPct, int SellPct)> TpLevels { get; init; } = new();
        public ConcurrentDictionary<int, byte>  FiredTpLevels { get; } = new();
        public double CurrentPriceSol  { get; set; }
        public double HighestPriceSol  { get; set; }
        public double StopPriceSol     { get; set; }
        public DateTimeOffset LastWsPriceTime  { get; set; } = DateTimeOffset.MinValue;
        public string PriceSource             { get; set; } = "—";
        public DateTimeOffset LastPriceUpdate { get; set; } = DateTimeOffset.MinValue;
        public List<string>    Notes        { get; } = new();
        public List<PriceTick> PriceHistory { get; } = new();
        public CancellationTokenSource Cts { get; } = new();
    }

    private readonly ConcurrentDictionary<string, PositionState> _positions = new();
    private readonly HttpClient _pollHttp = new();

    public IReadOnlyList<PositionSnapshot> GetActiveSnapshots() =>
        _positions.Select(kv =>
        {
            IReadOnlyList<PriceTick> history;
            lock (kv.Value.PriceHistory)
                history = kv.Value.PriceHistory.TakeLast(200).ToList();
            return new PositionSnapshot(
                kv.Value.TradeId, kv.Key, kv.Value.Symbol,
                kv.Value.EntryPriceSol, kv.Value.CurrentPriceSol,
                kv.Value.HighestPriceSol, kv.Value.StopPriceSol,
                kv.Value.SolSpent, kv.Value.EntryMarketCapUsd,
                kv.Value.TokenAmount, kv.Value.RemainingTokenAmount, kv.Value.EntryTime,
                kv.Value.Notes.ToList(), kv.Value.Source, history,
                kv.Value.PriceSource, kv.Value.LastPriceUpdate);
        }).ToList();

    public void StartMonitor(int tradeId, string mint, string symbol, long tokenAmount,
        double entryPriceSol, double solSpent, int trailingStopPercent, double entryMarketCapUsd,
        string walletAddress, string privateKey, string source = "Auto", string takeProfitLevels = "",
        IEnumerable<int>? firedThresholds = null)
    {
        if (_positions.ContainsKey(mint)) return;

        var state = new PositionState
        {
            TradeId          = tradeId,
            Symbol           = symbol,
            EntryPriceSol    = entryPriceSol,
            SolSpent         = solSpent,
            EntryMarketCapUsd= entryMarketCapUsd,
            TokenAmount      = tokenAmount,
            EntryTime        = DateTimeOffset.UtcNow,
            WalletAddress    = walletAddress,
            PrivateKey       = privateKey,
            Source           = source,
            TpLevels             = ParseTpLevels(takeProfitLevels),
            RemainingTokenAmount = tokenAmount,
            CurrentPriceSol  = entryPriceSol,
            HighestPriceSol  = entryPriceSol,
            StopPriceSol     = entryPriceSol * (1.0 - trailingStopPercent / 100.0),
        };
        if (firedThresholds is not null)
            foreach (var t in firedThresholds)
                state.FiredTpLevels.TryAdd(t, 0);

        _positions[mint] = state;

        var msg = $"Monitor started | ${symbol} | entry={entryPriceSol:E3} SOL | stop={trailingStopPercent}%";
        logger.LogInformation("👁 {Msg}", msg);
        eventLog.Info(msg);

        _ = Task.Run(() => MonitorAsync(mint, state, trailingStopPercent));
        _ = Task.Run(() => PollPriceAsync(mint, state, trailingStopPercent, state.Cts.Token));
    }

    public async Task ManualPartialSellAsync(string mint, int sellPct)
    {
        if (!_positions.TryGetValue(mint, out var state)) return;
        sellPct = Math.Clamp(sellPct, 1, 100);
        await ExecutePartialSellAsync(mint, state, 0, sellPct, state.CurrentPriceSol);
    }

    public void UpdateTpLevels(string takeProfitLevels)
    {
        var levels = ParseTpLevels(takeProfitLevels);
        foreach (var state in _positions.Values)
        {
            state.TpLevels.Clear();
            state.TpLevels.AddRange(levels);
            state.FiredTpLevels.Clear();
        }
    }

    public void CancelAllMonitors()
    {
        foreach (var kvp in _positions.ToArray())
        {
            if (_positions.TryRemove(kvp.Key, out var state))
                state.Cts.Cancel();
        }
    }

    public void StopMonitor(string mint)
    {
        if (_positions.TryRemove(mint, out var state))
        {
            state.Cts.Cancel();
            var msg = $"Position manually closed | ${state.Symbol} — selling";
            eventLog.Warn(msg);
            _ = AppendNoteAsync(state.TradeId, "Manually closed by user — selling");
            _ = Task.Run(() => ExecuteSellAsync(mint, state, state.CurrentPriceSol));
        }
    }

    private async Task MonitorAsync(string mint, PositionState state, int trailingStopPercent)
    {
        var bondingCurve = DeriveBondingCurve(mint);
        var heliusWs = $"wss://mainnet.helius-rpc.com/?api-key={config["Helius:ApiKey"]}";
        var ct = state.Cts.Token;

        while (!ct.IsCancellationRequested)
        {
            try
            {
                using var ws = new ClientWebSocket();
                ws.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
                await ws.ConnectAsync(new Uri(heliusWs), ct);
                var wsMsg = $"WS connected | ${state.Symbol} | curve={bondingCurve[..8]}…";
                logger.LogInformation("🔌 {Msg}", wsMsg);
                eventLog.Info(wsMsg);

                var sub = JsonSerializer.Serialize(new
                {
                    jsonrpc = "2.0", id = 1, method = "accountSubscribe",
                    @params = new object[] { bondingCurve, new { encoding = "base64", commitment = "processed" } }
                });
                await ws.SendAsync(Encoding.UTF8.GetBytes(sub), WebSocketMessageType.Text, true, ct);

                var buf = new byte[65536];
                var acc = new MemoryStream();

                while (ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
                {
                    WebSocketReceiveResult result;
                    do
                    {
                        result = await ws.ReceiveAsync(buf, ct);
                        acc.Write(buf, 0, result.Count);
                    } while (!result.EndOfMessage);

                    var raw = Encoding.UTF8.GetString(acc.ToArray());
                    acc.SetLength(0);
                    var price = ParsePrice(raw);
                    if (price <= 0)
                    {
                        // Only log non-subscription-ack messages we can't parse
                        if (!raw.Contains("\"subscription\"") && !raw.Contains("\"result\""))
                            eventLog.Warn($"WS msg unparseable for ${state.Symbol}: {raw[..Math.Min(120, raw.Length)]}");
                        continue;
                    }

                    state.CurrentPriceSol = price;
                    state.LastWsPriceTime  = DateTimeOffset.UtcNow;
                    state.PriceSource      = "WS";
                    state.LastPriceUpdate  = DateTimeOffset.UtcNow;
                    var mult = state.EntryPriceSol > 0 ? price / state.EntryPriceSol : 1.0;
                    var mc   = state.EntryMarketCapUsd * mult;
                    lock (state.PriceHistory)
                    {
                        state.PriceHistory.Add(new PriceTick(DateTimeOffset.UtcNow, mc, mult));
                        if (state.PriceHistory.Count > 500) state.PriceHistory.RemoveAt(0);
                    }

                    if (price > state.HighestPriceSol)
                    {
                        state.HighestPriceSol = price;
                        state.StopPriceSol = price * (1.0 - trailingStopPercent / 100.0);
                        eventLog.Info($"ATH | ${state.Symbol} | MC={mc:N0} | stop reset");
                    }

                    CheckTakeProfits(mint, state, price);

                    if (price <= state.StopPriceSol)
                    {
                        if (!_positions.TryRemove(mint, out _)) return;
                        var msg = $"Stop hit | ${state.Symbol} | price={price:E3} stop={state.StopPriceSol:E3}";
                        logger.LogInformation("🛑 {Msg}", msg);
                        eventLog.Warn(msg);
                        _ = AppendNoteAsync(state.TradeId, $"Stop triggered | price={price:E3} stop={state.StopPriceSol:E3} ATH={state.HighestPriceSol:E3}");
                        await ExecuteSellAsync(mint, state, price);
                        return;
                    }
                }
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex)
            {
                var msg = $"WS error ${state.Symbol}: {ex.Message}";
                logger.LogError(ex, "{Msg}", msg);
                eventLog.Error(msg);
                var noteMsg = $"WS disconnect: {ex.Message}";
                lock (state.Notes) state.Notes.Add($"[{DateTimeOffset.UtcNow:HH:mm:ss}] {noteMsg}");
                _ = AppendNoteAsync(state.TradeId, noteMsg);
                try { await Task.Delay(2000, ct); } catch { return; }
            }
        }
    }

    private async Task ExecuteSellAsync(string mint, PositionState state, double exitPrice)
    {
        var balance = await GetTokenBalanceAsync(mint, state.WalletAddress);
        if (balance is null or <= 0)
        {
            // On-chain balance unavailable — could be confirmation lag on a fast stop.
            // Fall back to our tracked remaining amount (same as partial-sell path).
            var fallback = state.RemainingTokenAmount > 0 ? state.RemainingTokenAmount : state.TokenAmount;
            if (fallback <= 0)
            {
                eventLog.Warn($"Sell skipped — no token balance and no tracked amount | ${state.Symbol}");
                await AppendNoteAsync(state.TradeId, "Sell skipped — balance check returned 0 and RemainingTokenAmount=0; position may already be closed");
                using var rel = scopeFactory.CreateScope();
                rel.ServiceProvider.GetRequiredService<AutoTradeService>().ReleaseMint(mint);
                return;
            }
            eventLog.Warn($"Balance check returned 0 — falling back to tracked amount ({fallback / 1e6:N0} tokens) | ${state.Symbol}");
            await AppendNoteAsync(state.TradeId, $"Balance check returned 0 at sell time — used fallback amount {fallback / 1e6:N0} tokens (likely confirmation lag)");
            balance = fallback;
        }

        // Sum actual SOL received from any TP sells that already completed
        var tpSol = 0.0;
        if (state.TradeId > 0)
        {
            try
            {
                using var tpScope = scopeFactory.CreateScope();
                var tpDb = tpScope.ServiceProvider.GetRequiredService<AppDbContext>();
                tpSol = await tpDb.TpOrders
                    .Where(o => o.TradeId == state.TradeId && o.Status == "Filled")
                    .SumAsync(o => o.SolReceived);
            }
            catch { }
        }

        var sig = await jupiter.SellAsync(mint, balance.Value, state.PrivateKey, state.WalletAddress);

        double pnl;
        if (sig is not null)
        {
            var solReceived = await FetchActualSolReceivedAsync(sig, state.WalletAddress);
            var totalIn     = (solReceived > 0 ? solReceived : 0.0) + tpSol;
            pnl = totalIn > 0
                ? totalIn - state.SolSpent
                : (exitPrice - state.EntryPriceSol) * (balance.Value / 1e6) + tpSol;
            var tpPart = tpSol > 0 ? $" + TP={tpSol:F4}◎" : "";
            var note = solReceived > 0
                ? $"Sold {balance.Value:N0} tokens | exit={solReceived:F4}◎{tpPart} | total in={totalIn:F4}◎ | PnL={pnl:+0.0000;-0.0000} SOL | sig={sig[..8]}"
                : $"Sold {balance.Value:N0} tokens{tpPart} | PnL≈{pnl:+0.0000;-0.0000} SOL (exit fetch failed) | sig={sig[..8]}";
            var logMsg = $"Sold ${state.Symbol} | PnL={pnl:+0.0000;-0.0000} SOL | sig={sig[..8]}";
            logger.LogInformation("✅ {Msg}", logMsg);
            eventLog.Info(logMsg);
            await AppendNoteAsync(state.TradeId, note);
        }
        else
        {
            pnl = (exitPrice - state.EntryPriceSol) * (balance.Value / 1e6) + tpSol - state.SolSpent;
            var errNote = $"Sell tx FAILED — Jupiter returned null sig | estimated PnL={pnl:+0.0000;-0.0000} SOL";
            logger.LogError("❌ Sell tx failed | ${Symbol}", state.Symbol);
            eventLog.Error($"Sell tx failed | ${state.Symbol}");
            await AppendNoteAsync(state.TradeId, errNote);
        }

        await MarkTradeClosedAsync(state, sig, pnl, exitPrice, sig is not null ? "Closed" : "SellFailed");

        using var scope = scopeFactory.CreateScope();
        scope.ServiceProvider.GetRequiredService<AutoTradeService>().ReleaseMint(mint);
    }

    private async Task MarkTradeClosedAsync(PositionState state, string? sig, double pnl, double exitPrice, string status)
    {
        try
        {
            using var scope = scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var trade = await db.Trades.FindAsync(state.TradeId);
            if (trade is not null)
            {
                trade.Status           = status;
                trade.CloseTime        = DateTimeOffset.UtcNow;
                trade.ExitPriceSol     = exitPrice;
                trade.SellSignature    = sig;
                trade.PnlSol           = pnl;
                if (state.EntryPriceSol > 0)
                {
                    trade.AthMarketCapUsd  = state.EntryMarketCapUsd * (state.HighestPriceSol / state.EntryPriceSol);
                    trade.ExitMarketCapUsd = state.EntryMarketCapUsd * (exitPrice             / state.EntryPriceSol);
                }
                await db.SaveChangesAsync();
            }
        }
        catch (Exception ex)
        {
            var errMsg = $"DB update failed after sell | ${state.Symbol}: {ex.Message}";
            logger.LogError(ex, "{Msg}", errMsg);
            eventLog.Error(errMsg);
        }
    }

    private async Task AppendNoteAsync(int tradeId, string note)
    {
        if (tradeId <= 0) return;
        var stamped = $"[{DateTimeOffset.UtcNow:HH:mm:ss}] {note}";
        try
        {
            using var scope = scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var trade = await db.Trades.FindAsync(tradeId);
            if (trade is not null)
            {
                trade.Notes = string.IsNullOrEmpty(trade.Notes) ? stamped : trade.Notes + "\n" + stamped;
                await db.SaveChangesAsync();
            }
        }
        catch (Exception ex) { logger.LogWarning(ex, "AppendNote failed for trade {Id}", tradeId); }
    }

    private async Task<double> FetchActualSolReceivedAsync(string txSig, string wallet)
    {
        if (string.IsNullOrEmpty(wallet)) return 0;
        var rpc = $"https://mainnet.helius-rpc.com/?api-key={config["Helius:ApiKey"]}";
        var body = JsonSerializer.Serialize(new
        {
            jsonrpc = "2.0", id = 1, method = "getTransaction",
            @params = new object[] { txSig, new { commitment = "confirmed", maxSupportedTransactionVersion = 0 } }
        });

        for (int attempt = 0; attempt < 8; attempt++)
        {
            await Task.Delay(attempt == 0 ? 2500 : 3000);
            try
            {
                using var http = new HttpClient();
                var resp = await http.PostAsync(rpc, new StringContent(body, Encoding.UTF8, "application/json"));
                using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
                var result = doc.RootElement.GetProperty("result");
                if (result.ValueKind == JsonValueKind.Null) continue;

                var keys   = result.GetProperty("transaction").GetProperty("message").GetProperty("accountKeys");
                var preBal = result.GetProperty("meta").GetProperty("preBalances");
                var postBal= result.GetProperty("meta").GetProperty("postBalances");

                for (int i = 0; i < keys.GetArrayLength(); i++)
                {
                    var key = keys[i].ValueKind == JsonValueKind.String
                        ? keys[i].GetString()
                        : keys[i].GetProperty("pubkey").GetString();
                    if (key != wallet) continue;
                    var delta = postBal[i].GetInt64() - preBal[i].GetInt64();
                    return delta / 1e9;
                }
                return 0;
            }
            catch { }
        }
        return 0;
    }

    private async Task<long?> GetTokenBalanceAsync(string mint, string wallet)
    {
        if (string.IsNullOrEmpty(wallet)) return null;
        var rpc = $"https://mainnet.helius-rpc.com/?api-key={config["Helius:ApiKey"]}";
        var body = JsonSerializer.Serialize(new
        {
            jsonrpc = "2.0", id = 1, method = "getTokenAccountsByOwner",
            @params = new object[] { wallet, new { mint }, new { encoding = "jsonParsed", commitment = "confirmed" } }
        });
        try
        {
            using var http = new HttpClient();
            var resp = await http.PostAsync(rpc, new StringContent(body, Encoding.UTF8, "application/json"));
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
            foreach (var acct in doc.RootElement.GetProperty("result").GetProperty("value").EnumerateArray())
            {
                var amount = acct.GetProperty("account").GetProperty("data")
                    .GetProperty("parsed").GetProperty("info")
                    .GetProperty("tokenAmount").GetProperty("amount").GetString();
                if (long.TryParse(amount, out var bal) && bal > 0) return bal;
            }
        }
        catch (Exception ex) { logger.LogError(ex, "getTokenBalance failed for {Mint}", mint[..8]); }
        return null;
    }

    private async Task PollPriceAsync(string mint, PositionState state, int trailingStopPercent, CancellationToken ct)
    {
        var bondingCurve = DeriveBondingCurve(mint);
        var apiKey = config["Helius:ApiKey"]
                  ?? Environment.GetEnvironmentVariable("HELIUS_API_KEY")
                  ?? "";
        var rpc  = $"https://mainnet.helius-rpc.com/?api-key={apiKey}";
        var body = JsonSerializer.Serialize(new
        {
            jsonrpc = "2.0", id = 1, method = "getAccountInfo",
            @params = new object[] { bondingCurve, new { encoding = "base64" } }
        });

        while (!ct.IsCancellationRequested)
        {
            try { await Task.Delay(3000, ct); } catch { return; }

            // WS just fired — skip this tick to avoid duplicate stop/TP processing
            if ((DateTimeOffset.UtcNow - state.LastWsPriceTime).TotalSeconds < 2) continue;

            if (string.IsNullOrEmpty(apiKey)) continue;

            try
            {
                var resp = await _pollHttp.PostAsync(rpc,
                    new StringContent(body, Encoding.UTF8, "application/json"), ct);
                var raw = await resp.Content.ReadAsStringAsync(ct);
                using var doc = JsonDocument.Parse(raw);

                var valueEl = doc.RootElement.GetProperty("result").GetProperty("value");
                if (valueEl.ValueKind == JsonValueKind.Null)
                {
                    // Bonding curve closed — token has graduated from pump.fun
                    eventLog.Warn($"${state.Symbol}: bonding curve closed (graduated to DEX) — poll stopped");
                    return;
                }

                var b64   = valueEl.GetProperty("data")[0].GetString()!;
                var bytes = Convert.FromBase64String(b64);
                if (bytes.Length < 24) continue;

                var vtr = BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(8,  8));
                var vsr = BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(16, 8));
                if (vtr == 0) continue;

                // Identical formula to WS ParsePrice — same source, same price
                var priceSol = (double)vsr / ((double)vtr * 1000.0);

                state.CurrentPriceSol = priceSol;
                state.PriceSource     = "Poll";
                state.LastPriceUpdate = DateTimeOffset.UtcNow;
                var mult = state.EntryPriceSol > 0 ? priceSol / state.EntryPriceSol : 1.0;
                var mc   = state.EntryMarketCapUsd * mult;
                lock (state.PriceHistory)
                {
                    state.PriceHistory.Add(new PriceTick(DateTimeOffset.UtcNow, mc, mult));
                    if (state.PriceHistory.Count > 500) state.PriceHistory.RemoveAt(0);
                }

                if (priceSol > state.HighestPriceSol)
                {
                    state.HighestPriceSol = priceSol;
                    state.StopPriceSol    = priceSol * (1.0 - trailingStopPercent / 100.0);
                    eventLog.Info($"ATH (poll) | ${state.Symbol} | MC=${mc:N0} | stop reset");
                }

                CheckTakeProfits(mint, state, priceSol);

                if (priceSol <= state.StopPriceSol)
                {
                    if (!_positions.TryRemove(mint, out _)) return;
                    var msg = $"Stop hit (poll) | ${state.Symbol} | MC=${mc:N0}";
                    logger.LogInformation("🛑 {Msg}", msg);
                    eventLog.Warn(msg);
                    _ = AppendNoteAsync(state.TradeId, $"Stop triggered via Helius poll | MC=${mc:N0}");
                    await ExecuteSellAsync(mint, state, priceSol);
                    return;
                }
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex)
            {
                eventLog.Error($"Helius poll error ${state.Symbol}: {ex.Message}");
            }
        }
    }

    private void CheckTakeProfits(string mint, PositionState state, double price)
    {
        if (state.TpLevels.Count == 0 || state.EntryPriceSol <= 0) return;
        var gainPct = (int)((price / state.EntryPriceSol - 1.0) * 100);
        foreach (var (threshold, sellPct) in state.TpLevels)
        {
            if (gainPct >= threshold && state.FiredTpLevels.TryAdd(threshold, 0))
                _ = Task.Run(() => ExecutePartialSellAsync(mint, state, threshold, sellPct, price));
        }
    }

    private async Task ExecutePartialSellAsync(string mint, PositionState state, int gainPct, int sellPct, double price)
    {
        var balance = await GetTokenBalanceAsync(mint, state.WalletAddress) ?? state.RemainingTokenAmount;
        if (balance <= 0)
        {
            eventLog.Error($"TP +{gainPct}% skipped — no balance | ${state.Symbol}");
            return;
        }

        var toSell = (long)(balance * sellPct / 100.0);
        if (toSell <= 0) return;

        var mc  = state.EntryMarketCapUsd * (state.EntryPriceSol > 0 ? price / state.EntryPriceSol : 1.0);
        var msg = $"TP +{gainPct}% | ${state.Symbol} | selling {sellPct}% of bag ({toSell/1e6:N0} tokens) | MC=${mc:N0}";
        logger.LogInformation("💰 {Msg}", msg);
        eventLog.Info(msg);

        var sig = await jupiter.SellAsync(mint, toSell, state.PrivateKey, state.WalletAddress);
        var status = sig is not null ? "Filled" : "Failed";

        var tpOrderId = 0;
        try
        {
            using var scope = scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var order = new FasterAlerts.Models.TpOrder
            {
                TradeId    = state.TradeId,
                Threshold  = gainPct,
                SellPct    = sellPct,
                FiredAt    = DateTimeOffset.UtcNow,
                Status     = status,
                Signature  = sig ?? "",
                TokensSold = sig is not null ? toSell : 0
            };
            db.TpOrders.Add(order);
            await db.SaveChangesAsync();
            tpOrderId = order.Id;
        }
        catch (Exception ex) { logger.LogWarning("TpOrder save failed: {Err}", ex.Message); }

        if (sig is not null)
        {
            state.RemainingTokenAmount = Math.Max(0, state.RemainingTokenAmount - toSell);
            eventLog.Info($"TP sold | ${state.Symbol} | sig={sig[..8]}");

            // Fetch actual SOL received from chain and persist — fires in background, doesn't block monitor
            var capturedSig = sig;
            var capturedOrderId = tpOrderId;
            _ = Task.Run(async () =>
            {
                var solRec = await FetchActualSolReceivedAsync(capturedSig, state.WalletAddress);
                if (solRec > 0 && capturedOrderId > 0)
                {
                    try
                    {
                        using var s = scopeFactory.CreateScope();
                        var d = s.ServiceProvider.GetRequiredService<AppDbContext>();
                        var o = await d.TpOrders.FindAsync(capturedOrderId);
                        if (o is not null) { o.SolReceived = solRec; await d.SaveChangesAsync(); }
                    }
                    catch { }
                }
                var solStr = solRec > 0 ? $"{solRec:F4}◎" : "fetch failed";
                var note = $"TP +{gainPct}% fired | sold {sellPct}% ({toSell/1e6:N0} tokens) | SOL received={solStr} | sig={capturedSig[..8]}";
                eventLog.Info($"TP result | ${state.Symbol} | SOL in={solStr}");
                await AppendNoteAsync(state.TradeId, note);
            });
        }
        else
        {
            eventLog.Error($"TP +{gainPct}% sell FAILED — use manual partial sell | ${state.Symbol}");
            await AppendNoteAsync(state.TradeId, $"TP +{gainPct}% sell FAILED — use dashboard to sell manually");
        }
    }

    private static List<(int GainPct, int SellPct)> ParseTpLevels(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return new();
        return raw.Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(s => s.Trim().Split(':'))
            .Where(p => p.Length == 2 && int.TryParse(p[0], out _) && int.TryParse(p[1], out _))
            .Select(p => (int.Parse(p[0]), int.Parse(p[1])))
            .OrderBy(t => t.Item1)
            .ToList();
    }

    private static double ParsePrice(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (!root.TryGetProperty("method", out var m) || m.GetString() != "accountNotification") return 0;
            var data = root.GetProperty("params").GetProperty("result").GetProperty("value").GetProperty("data");
            var bytes = Convert.FromBase64String(data[0].GetString()!);
            if (bytes.Length < 24) return 0;
            var vtr = BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(8, 8));
            var vsr = BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(16, 8));
            return vtr == 0 ? 0 : (double)vsr / ((double)vtr * 1000.0);
        }
        catch { return 0; }
    }

    private static string DeriveBondingCurve(string mint)
    {
        var seeds = new List<byte[]> { Encoding.UTF8.GetBytes("bonding-curve"), new PublicKey(mint).KeyBytes };
        PublicKey.TryFindProgramAddress(seeds, new PublicKey(PumpFunProgramId), out var pda, out _);
        return pda.Key;
    }
}
