namespace FasterAlerts.Models;

public class Trade
{
    public int    Id           { get; set; }
    public string TokenMint    { get; set; } = "";
    public string TokenSymbol  { get; set; } = "";
    public DateTimeOffset EntryTime  { get; set; }
    public double EntryPriceSol      { get; set; }
    public double SolSpent           { get; set; }
    public long   TokenAmount        { get; set; }
    public double EntryMarketCapUsd  { get; set; }
    public string BuySignature       { get; set; } = "";
    public string Status             { get; set; } = "Active";
    public DateTimeOffset? CloseTime { get; set; }
    public double? ExitPriceSol      { get; set; }
    public string? SellSignature     { get; set; }
    public double? PnlSol            { get; set; }
    public string  Notes             { get; set; } = "";
    public string  Source            { get; set; } = "Auto";
    // Analytics — populated at entry/close, never on the buy hot-path
    public double  AthMarketCapUsd   { get; set; }
    public double  ExitMarketCapUsd  { get; set; }
    public int     VestingDays       { get; set; }
    public double  PercentSupply     { get; set; }
    public double  LockedUsd         { get; set; }
}
