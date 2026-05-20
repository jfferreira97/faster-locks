namespace FasterAlerts.Models;

public class StreamAlert
{
    public string Signature { get; set; } = "";
    public string StreamAccount { get; set; } = "";
    public string TokenMint { get; set; } = "";
    public string TokenName { get; set; } = "";
    public string TokenSymbol { get; set; } = "";
    public decimal AmountLocked { get; set; }
    public decimal UsdValue { get; set; }
    public decimal PercentSupply { get; set; }
    public int CliffDays { get; set; }
    public int VestingDays { get; set; }
    public DateTimeOffset? UnlockDate { get; set; }   // when first withdrawal is possible
    public string RecipientWallet { get; set; } = "";
    public string CreatorWallet { get; set; } = "";
    public decimal PriceUsd { get; set; }
    public decimal MarketCapUsd { get; set; }
    public DateTimeOffset? PairCreatedAt { get; set; }
    public DateTimeOffset Timestamp { get; set; }
}
