namespace FasterAlerts.Models;

public class TradingSettings
{
    public int     Id                  { get; set; } = 1;
    public bool    Enabled             { get; set; }
    public long    BuySolLamports      { get; set; } = 100_000_000;
    public int     TrailingStopPercent { get; set; } = 25;
    public double  MinPercentLocked    { get; set; } = 1.0;
    public decimal MinMarketCapUsd     { get; set; } = 1m;
    public decimal MaxMarketCapUsd     { get; set; } = 10_000m;
    public int     MaxTokenAgeHours         { get; set; } = 48;
    public int     MinVestingDays           { get; set; } = 0;
    public string  WalletAddress            { get; set; } = "";
    public string  WalletPrivateKeyBase58   { get; set; } = "";
    public string  TakeProfitLevels         { get; set; } = "";
}
