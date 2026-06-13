namespace FasterAlerts.Models;

public class TpOrder
{
    public int            Id         { get; set; }
    public int            TradeId    { get; set; }
    public int            Threshold  { get; set; }
    public int            SellPct    { get; set; }
    public DateTimeOffset FiredAt    { get; set; }
    public string         Status     { get; set; } = "Filled"; // Filled | Failed
    public string         Signature  { get; set; } = "";
    public long           TokensSold  { get; set; }
    public double         SolReceived { get; set; } = 0;
}
