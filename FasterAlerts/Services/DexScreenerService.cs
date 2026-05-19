using System.Text.Json;
using System.Text.Json.Serialization;
using FasterAlerts.Models;
using Microsoft.Extensions.Logging;

namespace FasterAlerts.Services;

public class DexScreenerService(HttpClient http, ILogger<DexScreenerService> logger)
{
    public async Task EnrichAsync(StreamAlert alert)
    {
        if (string.IsNullOrEmpty(alert.TokenMint)) return;

        try
        {
            var resp = await http.GetAsync($"https://api.dexscreener.com/tokens/v1/solana/{alert.TokenMint}");
            if (!resp.IsSuccessStatusCode)
            {
                logger.LogWarning("DexScreener returned {Status} for {Mint}", resp.StatusCode, alert.TokenMint);
                FallbackNames(alert);
                return;
            }

            var json = await resp.Content.ReadAsStringAsync();
            var pairs = JsonSerializer.Deserialize<List<DexPair>>(json, JsonOptions);
            var pair = pairs?.FirstOrDefault();

            if (pair is null)
            {
                FallbackNames(alert);
                return;
            }

            alert.TokenName = pair.BaseToken?.Name ?? ShortAddr(alert.TokenMint);
            alert.TokenSymbol = pair.BaseToken?.Symbol ?? "???";

            if (decimal.TryParse(pair.PriceUsd, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var price))
                alert.PriceUsd = price;

            // Prefer marketCap; fall back to fdv
            var mcap = pair.MarketCap > 0 ? pair.MarketCap : pair.Fdv;
            alert.MarketCapUsd = mcap;

            if (alert.PriceUsd > 0 && mcap > 0)
            {
                alert.UsdValue = alert.AmountLocked * alert.PriceUsd;
                var totalSupply = mcap / alert.PriceUsd;
                alert.PercentSupply = totalSupply > 0 ? alert.AmountLocked / totalSupply * 100m : 0;
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "DexScreener enrichment failed for {Mint}", alert.TokenMint);
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

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };
}

file class DexPair
{
    [JsonPropertyName("baseToken")]
    public DexToken? BaseToken { get; set; }

    [JsonPropertyName("priceUsd")]
    public string? PriceUsd { get; set; }

    [JsonPropertyName("marketCap")]
    public decimal MarketCap { get; set; }

    [JsonPropertyName("fdv")]
    public decimal Fdv { get; set; }
}

file class DexToken
{
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("symbol")]
    public string? Symbol { get; set; }
}
