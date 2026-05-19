using System.Text;
using System.Text.Json;
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
    ILogger<HeliusWebhookController> logger) : ControllerBase
{
    private const string PayloadLog = @"C:\Users\Administrator\Desktop\faster-locks-payloads.txt";
    private static readonly JsonSerializerOptions Pretty = new() { WriteIndented = true };

    [HttpPost("helius")]
    public async Task<IActionResult> Receive([FromBody] List<EnhancedTransaction> transactions)
    {
        if (transactions is null || transactions.Count == 0)
            return Ok();

        // Fire-and-forget payload dump — never blocks or throws into main flow
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
            catch { /* never blow up the main thread */ }
        });

        foreach (var tx in transactions)
        {
            try
            {
                var alert = await parser.ParseAsync(tx);
                if (alert is null) continue;

                await dexScreener.EnrichAsync(alert);
                await telegram.SendAlertAsync(alert);

                logger.LogInformation("✅ alert sent | {Symbol} | {Amount:N0} locked | sig={Sig}",
                    alert.TokenSymbol, alert.AmountLocked, alert.Signature?[..8]);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "❌ error processing tx {Signature}", tx.Signature);
            }
        }

        return Ok();
    }
}
