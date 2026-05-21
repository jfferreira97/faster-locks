namespace FasterAlerts.Models;

public class SentAlert
{
    public int Id { get; set; }
    public string Signature { get; set; } = "";
    public string TokenMint { get; set; } = "";
    public string TokenSymbol { get; set; } = "";
    public decimal AmountLocked { get; set; }
    public decimal PercentSupply { get; set; }
    public decimal MarketCapUsd { get; set; }
    public DateTimeOffset? UnlockDate { get; set; }
    public DateTimeOffset SentAt { get; set; }
}
