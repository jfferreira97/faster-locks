using System.Text;
using System.Text.Json;
using FasterAlerts.Data;
using FasterAlerts.Models;
using FasterAlerts.Services;
using Microsoft.AspNetCore.Mvc;

namespace FasterAlerts.Controllers;

[ApiController]
[Route("webhook")]
public class HeliusWebhookController(
    StreamflowParserService parser,
    DexScreenerService dexScreener,
    TelegramService telegram,
    AppDbContext db,
    ILogger<HeliusWebhookController> logger) : ControllerBase
{
    private const string PayloadLog = @"C:\Users\Administrator\Desktop\faster-locks-payloads.txt";
    private static readonly JsonSerializerOptions Pretty = new() { WriteIndented = true };

    [HttpPost("helius")]
    public async Task<IActionResult> Receive([FromBody] List<EnhancedTransaction> transactions)
    {
        if (transactions is null || transactions.Count == 0)
            return Ok();

        _ = Task.Run(async () =>
        {
            try
            {
                var sb = new StringBuilder();
                foreach (var tx in transactions)
                {
                    sb.AppendLine($"[{DateTimeOffset.UtcNow:yyyy-MM-ddTHH:mm:ss.fffZ}]");
                    sb.AppendLine(JsonSerializer.Serialize(tx, Pretty));
                    sb.AppendLine(new string('─', 80));
                }
                await System.IO.File.AppendAllTextAsync(PayloadLog, sb.ToString());
            }
            catch { }
        });

        foreach (var tx in transactions)
        {
            try
            {
                var alert = await parser.ParseAsync(tx);
                if (alert is null) continue;

                await dexScreener.EnrichAsync(alert);

                var record = new SentAlert
                {
                    Signature     = alert.Signature,
                    TokenMint     = alert.TokenMint,
                    TokenSymbol   = alert.TokenSymbol,
                    AmountLocked  = alert.AmountLocked,
                    PercentSupply = alert.PercentSupply,
                    MarketCapUsd  = alert.MarketCapUsd,
                    UnlockDate    = alert.UnlockDate,
                    PairCreatedAt = alert.PairCreatedAt,
                    PairAddress   = alert.PairAddress,
                    SentAt        = DateTimeOffset.UtcNow
                };
                db.SentAlerts.Add(record);
                await db.SaveChangesAsync();

                alert.NotificationId = record.Id;

                await telegram.SendAlertAsync(alert);

                logger.LogInformation("✅ alert sent | #{Id} | {Symbol} | {Amount:N0} locked | sig={Sig}",
                    alert.NotificationId, alert.TokenSymbol, alert.AmountLocked, alert.Signature?[..8]);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "❌ error processing tx {Signature}", tx.Signature);
            }
        }

        return Ok();
    }
}
