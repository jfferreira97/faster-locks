namespace FasterAlerts.Models;

public class BacktestCache
{
    public int Id { get; set; }
    public int SentAlertId    { get; set; }
    public string TokenMint   { get; set; } = "";
    public string TokenSymbol { get; set; } = "";
    public string PairAddress { get; set; } = "";
    public decimal EntryPrice { get; set; }          // SOL per token at alert time
    public DateTimeOffset AlertTime  { get; set; }   // = SentAlert.SentAt
    public DateTimeOffset FetchedAt  { get; set; }
    public string SeriesJson  { get; set; } = "[]";  // [{t,p}] unix + SOL price, asc
    public string FetchStatus { get; set; } = "PENDING"; // PENDING|DONE|ERROR|NO_PAIR
    public string? FetchError { get; set; }
}
