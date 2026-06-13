using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using FasterAlerts.Models;
using Microsoft.Extensions.Logging;

namespace FasterAlerts.Services;

public record DsTokenSnapshot(string Symbol, decimal PriceUsd, decimal MarketCapUsd,
    decimal Vol1h, decimal Vol6h, decimal Vol24h);

public class DexScreenerService(HttpClient http, IConfiguration config, ILogger<DexScreenerService> logger)
{
    private string HeliusRpc => $"https://mainnet.helius-rpc.com/?api-key={config["Helius:ApiKey"]}";

    public async Task EnrichAsync(StreamAlert alert)
    {
        if (string.IsNullOrEmpty(alert.TokenMint)) return;

        try
        {
            var resp = await http.GetAsync($"https://api.dexscreener.com/tokens/v1/solana/{alert.TokenMint}");
            if (resp.IsSuccessStatusCode)
            {
                var json  = await resp.Content.ReadAsStringAsync();
                var pairs = JsonSerializer.Deserialize<List<DexPair>>(json, JsonOptions);
                var pair  = pairs?.FirstOrDefault();

                if (pair is not null)
                {
                    alert.TokenName   = pair.BaseToken?.Name   ?? ShortAddr(alert.TokenMint);
                    alert.TokenSymbol = pair.BaseToken?.Symbol ?? "???";
                    alert.PairAddress = pair.PairAddress ?? "";

                    if (pair.PairCreatedAt > 0)
                        alert.PairCreatedAt = DateTimeOffset.FromUnixTimeMilliseconds(pair.PairCreatedAt);

                    if (decimal.TryParse(pair.PriceUsd, System.Globalization.NumberStyles.Float,
                            System.Globalization.CultureInfo.InvariantCulture, out var price))
                        alert.PriceUsd = price;

                    var mcap = pair.MarketCap > 0 ? pair.MarketCap : pair.Fdv;
                    alert.MarketCapUsd = mcap;

                    if (alert.PriceUsd > 0 && mcap > 0)
                    {
                        alert.UsdValue = alert.AmountLocked * alert.PriceUsd;
                        var totalSupply = mcap / alert.PriceUsd;
                        alert.PercentSupply = totalSupply > 0 ? alert.AmountLocked / totalSupply * 100m : 0;
                    }

                    return;
                }
            }
            else
            {
                logger.LogWarning("DexScreener returned {Status} for {Mint}", resp.StatusCode, alert.TokenMint);
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "DexScreener enrichment failed for {Mint}", alert.TokenMint);
        }

        // No DEX pair — fall back to Helius DAS for on-chain metadata + supply
        await EnrichFromHeliusDasAsync(alert);
    }

    private async Task EnrichFromHeliusDasAsync(StreamAlert alert)
    {
        try
        {
            var body = JsonSerializer.Serialize(new
            {
                jsonrpc = "2.0", id = 1,
                method  = "getAsset",
                @params = new { id = alert.TokenMint }
            });

            var resp = await http.PostAsync(HeliusRpc,
                new StringContent(body, Encoding.UTF8, "application/json"));
            if (!resp.IsSuccessStatusCode) { FallbackNames(alert); return; }

            var json   = await resp.Content.ReadAsStringAsync();
            var result = JsonSerializer.Deserialize<DasResponse>(json, JsonOptions);
            var asset  = result?.Result;
            if (asset is null) { FallbackNames(alert); return; }

            alert.TokenName   = asset.Content?.Metadata?.Name   ?? ShortAddr(alert.TokenMint);
            alert.TokenSymbol = asset.Content?.Metadata?.Symbol ?? "???";

            // Compute % of supply if we have on-chain supply data
            var info = asset.TokenInfo;
            if (info is not null && info.Supply > 0 && info.Decimals >= 0)
            {
                var totalSupply = info.Supply / (decimal)Math.Pow(10, info.Decimals);
                if (totalSupply > 0)
                    alert.PercentSupply = alert.AmountLocked / totalSupply * 100m;
            }

            logger.LogInformation("DAS fallback: {Symbol} ({Name}), supply%={Pct:F2}",
                alert.TokenSymbol, alert.TokenName, alert.PercentSupply);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Helius DAS fallback failed for {Mint}", alert.TokenMint);
            FallbackNames(alert);
        }
    }

    private static void FallbackNames(StreamAlert alert)
    {
        if (string.IsNullOrEmpty(alert.TokenName))
            alert.TokenName = ShortAddr(alert.TokenMint);
        if (string.IsNullOrEmpty(alert.TokenSymbol))
            alert.TokenSymbol = "???";
    }

    private static string ShortAddr(string addr) =>
        addr.Length > 12 ? $"{addr[..6]}...{addr[^4..]}" : addr;

    public async Task<Dictionary<string, DsTokenSnapshot>> GetTokensInfoAsync(IList<string> mints)
    {
        var result = new Dictionary<string, DsTokenSnapshot>(StringComparer.OrdinalIgnoreCase);
        const int batchSize = 30;

        for (int i = 0; i < mints.Count; i += batchSize)
        {
            var batch  = mints.Skip(i).Take(batchSize).ToList();
            var joined = string.Join(",", batch);
            try
            {
                var resp = await http.GetAsync($"https://api.dexscreener.com/tokens/v1/solana/{joined}");
                if (!resp.IsSuccessStatusCode) continue;
                var json  = await resp.Content.ReadAsStringAsync();
                var pairs = JsonSerializer.Deserialize<List<DexPair>>(json, JsonOptions);
                if (pairs is null) continue;

                foreach (var pair in pairs)
                {
                    var mint = pair.BaseToken?.Address;
                    if (mint is null || result.ContainsKey(mint)) continue;

                    decimal.TryParse(pair.PriceUsd,
                        System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out var price);
                    var mc = pair.MarketCap > 0 ? pair.MarketCap : pair.Fdv;

                    result[mint] = new DsTokenSnapshot(
                        pair.BaseToken?.Symbol ?? "",
                        price, mc,
                        pair.Volume?.H1  ?? 0,
                        pair.Volume?.H6  ?? 0,
                        pair.Volume?.H24 ?? 0);
                }
            }
            catch (Exception ex) { logger.LogWarning(ex, "GetTokensInfoAsync batch {I} failed", i); }

            if (i + batchSize < mints.Count) await Task.Delay(250);
        }

        return result;
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };
}

file class DexPair
{
    [JsonPropertyName("baseToken")]    public DexToken?  BaseToken    { get; set; }
    [JsonPropertyName("priceUsd")]     public string?    PriceUsd     { get; set; }
    [JsonPropertyName("marketCap")]    public decimal    MarketCap    { get; set; }
    [JsonPropertyName("fdv")]          public decimal    Fdv          { get; set; }
    [JsonPropertyName("pairCreatedAt")]public long       PairCreatedAt{ get; set; }
    [JsonPropertyName("pairAddress")]  public string?    PairAddress  { get; set; }
    [JsonPropertyName("volume")]       public DexVolume? Volume       { get; set; }
}

file class DexVolume
{
    [JsonPropertyName("h1")]  public decimal H1  { get; set; }
    [JsonPropertyName("h6")]  public decimal H6  { get; set; }
    [JsonPropertyName("h24")] public decimal H24 { get; set; }
}

file class DexToken
{
    [JsonPropertyName("name")]    public string? Name    { get; set; }
    [JsonPropertyName("symbol")]  public string? Symbol  { get; set; }
    [JsonPropertyName("address")] public string? Address { get; set; }
}

file class DasResponse
{
    [JsonPropertyName("result")] public DasAsset? Result { get; set; }
}

file class DasAsset
{
    [JsonPropertyName("content")]    public DasContent?   Content   { get; set; }
    [JsonPropertyName("token_info")] public DasTokenInfo? TokenInfo { get; set; }
}

file class DasContent
{
    [JsonPropertyName("metadata")] public DasMetadata? Metadata { get; set; }
}

file class DasMetadata
{
    [JsonPropertyName("name")]   public string? Name   { get; set; }
    [JsonPropertyName("symbol")] public string? Symbol { get; set; }
}

file class DasTokenInfo
{
    [JsonPropertyName("supply")]   public long Supply   { get; set; }
    [JsonPropertyName("decimals")] public int  Decimals { get; set; }
}
