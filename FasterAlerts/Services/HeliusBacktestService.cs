using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using FasterAlerts.Data;
using FasterAlerts.Models;
using Microsoft.EntityFrameworkCore;

namespace FasterAlerts.Services;

public class HeliusBacktestService(
    HttpClient http,
    IConfiguration config,
    IServiceScopeFactory scopeFactory,
    ILogger<HeliusBacktestService> logger)
{
    // ── job state (singleton) ─────────────────────────────────────────────
    public static volatile bool IsRunning;
    public static int Processed;
    public static int TotalToProcess;
    public static string CurrentMsg = "idle";

    private string ApiKey  => config["Helius:ApiKey"] ?? "";
    private string RpcUrl  => $"https://mainnet.helius-rpc.com/?api-key={ApiKey}";
    private string ParseUrl => $"https://api.helius.xyz/v0/transactions/?api-key={ApiKey}";

    private const string WSOL = "So11111111111111111111111111111111111111112";
    private const string USDC = "EPjFWdd5AufqSSqeM2qN1xzybapC8G4wEGGkZwyTDt1v";

    // ── public API ────────────────────────────────────────────────────────

    public void StartJob(List<int> alertIds)
    {
        if (IsRunning) return;
        IsRunning = true;
        Processed = 0;
        TotalToProcess = alertIds.Count;
        CurrentMsg = "starting";
        Task.Run(() => RunJobAsync(alertIds));
    }

    // ── background job ────────────────────────────────────────────────────

    private async Task RunJobAsync(List<int> alertIds)
    {
        try
        {
            foreach (var id in alertIds)
            {
                using var scope = scopeFactory.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

                var alert = await db.SentAlerts.FindAsync(id);
                if (alert is null) { Processed++; continue; }

                var existing = await db.BacktestCache.FirstOrDefaultAsync(b => b.SentAlertId == id);
                if (existing?.FetchStatus == "DONE") { Processed++; continue; }

                CurrentMsg = $"fetching {alert.TokenSymbol} #{id}";
                var cache = existing ?? new BacktestCache { SentAlertId = id };

                cache.TokenMint   = alert.TokenMint;
                cache.TokenSymbol = alert.TokenSymbol;
                cache.AlertTime   = alert.SentAt;
                cache.FetchedAt   = DateTimeOffset.UtcNow;
                cache.FetchStatus = "PENDING";

                try
                {
                    // get pair address — use stored one or re-query DexScreener
                    var pair = !string.IsNullOrEmpty(alert.PairAddress)
                        ? alert.PairAddress
                        : await GetPairAddressAsync(alert.TokenMint);

                    if (string.IsNullOrEmpty(pair))
                    {
                        cache.FetchStatus = "NO_PAIR";
                        cache.FetchError  = "pair address not found";
                    }
                    else
                    {
                        cache.PairAddress = pair;
                        var sigs  = await GetSignaturesInWindowAsync(pair, alert.SentAt, alert.SentAt.AddHours(48));
                        var series = await ParseSignaturesAsync(alert.TokenMint, sigs);

                        cache.EntryPrice  = series.FirstOrDefault()?.P ?? 0;
                        cache.SeriesJson  = JsonSerializer.Serialize(series);
                        cache.FetchStatus = series.Count > 0 ? "DONE" : "ERROR";
                        if (series.Count == 0) cache.FetchError = "no swap events found";
                    }
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "backtest fetch failed for alert #{Id}", id);
                    cache.FetchStatus = "ERROR";
                    cache.FetchError  = ex.Message;
                }

                if (existing is null) db.BacktestCache.Add(cache);
                await db.SaveChangesAsync();
                Processed++;
                await Task.Delay(300); // gentle rate limiting
            }
            CurrentMsg = $"done — {Processed} processed";
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "backtest job crashed");
            CurrentMsg = $"error: {ex.Message}";
        }
        finally
        {
            IsRunning = false;
        }
    }

    // ── DexScreener: get pair/pool address ────────────────────────────────

    private async Task<string?> GetPairAddressAsync(string mint)
    {
        try
        {
            var resp = await http.GetAsync($"https://api.dexscreener.com/tokens/v1/solana/{mint}");
            if (!resp.IsSuccessStatusCode) return null;
            var json = await resp.Content.ReadAsStringAsync();
            var pairs = JsonSerializer.Deserialize<List<DsPair>>(json, JsonOpts);
            return pairs?.FirstOrDefault()?.PairAddress;
        }
        catch { return null; }
    }

    // ── Helius: get signatures for pool in time window ────────────────────

    private async Task<List<SigEntry>> GetSignaturesInWindowAsync(
        string pool, DateTimeOffset from, DateTimeOffset to)
    {
        var fromTs = from.ToUnixTimeSeconds();
        var toTs   = to.ToUnixTimeSeconds();
        var all    = new List<SigEntry>();
        string? before = null;
        const int maxPages = 15;

        for (var page = 0; page < maxPages; page++)
        {
            var paramsArr = before is null
                ? (object)new { limit = 1000, commitment = "confirmed" }
                : (object)new { limit = 1000, commitment = "confirmed", before };

            var body = JsonSerializer.Serialize(new
            {
                jsonrpc = "2.0", id = 1,
                method  = "getSignaturesForAddress",
                @params = new object[] { pool, paramsArr }
            });

            var resp = await http.PostAsync(RpcUrl,
                new StringContent(body, Encoding.UTF8, "application/json"));
            if (!resp.IsSuccessStatusCode) break;

            var json    = await resp.Content.ReadAsStringAsync();
            var rpcResp = JsonSerializer.Deserialize<RpcResp<List<SigInfo>>>(json, JsonOpts);
            var batch   = rpcResp?.Result;
            if (batch is null || batch.Count == 0) break;

            // collect sigs in our window
            foreach (var s in batch)
            {
                if (s.Err is not null) continue;
                if (s.BlockTime >= fromTs && s.BlockTime <= toTs)
                    all.Add(new SigEntry(s.Signature, s.BlockTime));
            }

            // if oldest in this batch is before our window start → done
            if (batch.Last().BlockTime < fromTs) break;

            before = batch.Last().Signature;
            await Task.Delay(150);
        }

        return all.OrderBy(s => s.BlockTime).ToList();
    }

    // ── Helius: parse signatures → price series ───────────────────────────

    private async Task<List<PricePoint>> ParseSignaturesAsync(string mint, List<SigEntry> sigs)
    {
        var result = new List<PricePoint>();
        const int batchSize = 100;

        for (var i = 0; i < sigs.Count; i += batchSize)
        {
            var batch = sigs.Skip(i).Take(batchSize).Select(s => s.Signature).ToList();
            var body  = JsonSerializer.Serialize(new { transactions = batch });

            var resp = await http.PostAsync(ParseUrl,
                new StringContent(body, Encoding.UTF8, "application/json"));
            if (!resp.IsSuccessStatusCode) { await Task.Delay(500); continue; }

            var json = await resp.Content.ReadAsStringAsync();
            var txns = JsonSerializer.Deserialize<List<HeliusTx>>(json, JsonOpts);
            if (txns is null) continue;

            foreach (var tx in txns.OrderBy(t => t.Timestamp))
            {
                if (tx.Type != "SWAP" && tx.Type != "UNKNOWN") continue;
                var price = ExtractPriceSol(tx, mint);
                if (price is > 0)
                    result.Add(new PricePoint(tx.Timestamp, price.Value));
            }

            await Task.Delay(200);
        }

        return result;
    }

    // ── price extraction from enhanced transaction ────────────────────────

    private decimal? ExtractPriceSol(HeliusTx tx, string mint)
    {
        var swap = tx.Events?.Swap;
        if (swap is null) return null;

        // buy: SOL in → token out
        if (swap.NativeInput is not null)
        {
            var tokenOut = swap.TokenOutputs?.FirstOrDefault(t =>
                string.Equals(t.Mint, mint, StringComparison.OrdinalIgnoreCase));
            if (tokenOut?.Raw is not null)
            {
                var solIn    = ParseLamports(swap.NativeInput.Amount);
                var tokenAmt = ParseTokenAmount(tokenOut.Raw);
                if (tokenAmt > 0) return solIn / tokenAmt;
            }
        }

        // sell: token in → SOL out
        if (swap.NativeOutput is not null)
        {
            var tokenIn = swap.TokenInputs?.FirstOrDefault(t =>
                string.Equals(t.Mint, mint, StringComparison.OrdinalIgnoreCase));
            if (tokenIn?.Raw is not null)
            {
                var solOut   = ParseLamports(swap.NativeOutput.Amount);
                var tokenAmt = ParseTokenAmount(tokenIn.Raw);
                if (tokenAmt > 0) return solOut / tokenAmt;
            }
        }

        // WSOL input
        var wsolIn = swap.TokenInputs?.FirstOrDefault(t =>
            string.Equals(t.Mint, WSOL, StringComparison.OrdinalIgnoreCase));
        var tokenFromWsol = swap.TokenOutputs?.FirstOrDefault(t =>
            string.Equals(t.Mint, mint, StringComparison.OrdinalIgnoreCase));
        if (wsolIn?.Raw is not null && tokenFromWsol?.Raw is not null)
        {
            var solAmt   = ParseTokenAmount(wsolIn.Raw);           // WSOL has 9 decimals
            var tokenAmt = ParseTokenAmount(tokenFromWsol.Raw);
            if (tokenAmt > 0) return solAmt / tokenAmt;
        }

        // USDC input → return as "units" (comparable for % moves)
        var usdcIn = swap.TokenInputs?.FirstOrDefault(t =>
            string.Equals(t.Mint, USDC, StringComparison.OrdinalIgnoreCase));
        var tokenFromUsdc = swap.TokenOutputs?.FirstOrDefault(t =>
            string.Equals(t.Mint, mint, StringComparison.OrdinalIgnoreCase));
        if (usdcIn?.Raw is not null && tokenFromUsdc?.Raw is not null)
        {
            var usdcAmt  = ParseTokenAmount(usdcIn.Raw);
            var tokenAmt = ParseTokenAmount(tokenFromUsdc.Raw);
            if (tokenAmt > 0) return usdcAmt / tokenAmt;
        }

        return null;
    }

    private static decimal ParseLamports(string lamports) =>
        decimal.TryParse(lamports, out var v) ? v / 1_000_000_000m : 0;

    private static decimal ParseTokenAmount(RawAmount raw) =>
        decimal.TryParse(raw.Amount, out var v) ? v / (decimal)Math.Pow(10, raw.Decimals) : 0;

    // ── JSON options ──────────────────────────────────────────────────────

    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };
}

// ── local data models ─────────────────────────────────────────────────────

public record PricePoint(long T, decimal P);
record SigEntry(string Signature, long BlockTime);

class RpcResp<T> { [JsonPropertyName("result")] public T? Result { get; set; } }

class SigInfo
{
    [JsonPropertyName("signature")] public string Signature { get; set; } = "";
    [JsonPropertyName("blockTime")]  public long BlockTime   { get; set; }
    [JsonPropertyName("err")]        public object? Err      { get; set; }
}

class DsPair
{
    [JsonPropertyName("pairAddress")] public string? PairAddress { get; set; }
}

class HeliusTx
{
    [JsonPropertyName("type")]      public string Type      { get; set; } = "";
    [JsonPropertyName("timestamp")] public long Timestamp   { get; set; }
    [JsonPropertyName("signature")] public string Signature { get; set; } = "";
    [JsonPropertyName("events")]    public TxEvents? Events { get; set; }
}

class TxEvents
{
    [JsonPropertyName("swap")] public SwapEvent? Swap { get; set; }
}

class SwapEvent
{
    [JsonPropertyName("nativeInput")]   public NativeAmt?       NativeInput   { get; set; }
    [JsonPropertyName("nativeOutput")]  public NativeAmt?       NativeOutput  { get; set; }
    [JsonPropertyName("tokenInputs")]   public List<TokenAmt>?  TokenInputs   { get; set; }
    [JsonPropertyName("tokenOutputs")]  public List<TokenAmt>?  TokenOutputs  { get; set; }
}

class NativeAmt  { [JsonPropertyName("amount")] public string Amount { get; set; } = "0"; }

class TokenAmt
{
    [JsonPropertyName("mint")]           public string Mint      { get; set; } = "";
    [JsonPropertyName("rawTokenAmount")] public RawAmount? Raw   { get; set; }
}

class RawAmount
{
    [JsonPropertyName("tokenAmount")] public string Amount   { get; set; } = "0";
    [JsonPropertyName("decimals")]    public int    Decimals { get; set; }
}
