namespace FasterAlerts.Models;

public class AutoTradeConfig
{
    public bool Enabled { get; set; }
    public string WalletPrivateKeyBase58 { get; set; } = "";
    public string WalletAddress { get; set; } = "";
    public long BuySolLamports { get; set; } = 100_000_000;
    public int TrailingStopPercent { get; set; } = 25;
    public AutoTradeFilters Filters { get; set; } = new();
}

public class AutoTradeFilters
{
    public double MinPercentLocked { get; set; } = 1.0;
    public decimal MinMarketCapUsd { get; set; } = 1m;
    public decimal MaxMarketCapUsd { get; set; } = 10_000m;
    public int MaxTokenAgeDays { get; set; } = 2;
}
