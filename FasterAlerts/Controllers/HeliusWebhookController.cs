using System.Text;
using System.Text.Json;
using FasterAlerts.Data;
using FasterAlerts.Models;
using FasterAlerts.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace FasterAlerts.Controllers;

[ApiController]
[Route("webhook")]
public class HeliusWebhookController(
    StreamflowParserService parser,
    DexScreenerService dexScreener,
    TelegramService telegram,
    AutoTradeService autoTrade,
    AppDbContext db,
    IServiceScopeFactory scopeFactory,
    ILogger<HeliusWebhookController> logger) : ControllerBase
{
    private const string PayloadLog = @"C:\Users\Administrator\Desktop\faster-locks-payloads.txt";
    private static readonly JsonSerializerOptions Pretty = new() { WriteIndented = true };

    [HttpPost("helius")]
    public async Task<IActionResult> Receive([FromBody] List<EnhancedTransaction> transactions)
    {
        if (transactions is null || transactions.Count == 0)
            return Ok();

        // _ = Task.Run(async () =>
        // {
        //     try
        //     {
        //         var sb = new StringBuilder();
        //         foreach (var tx in transactions)
        //         {
        //             sb.AppendLine($"[{DateTimeOffset.UtcNow:yyyy-MM-ddTHH:mm:ss.fffZ}]");
        //             sb.AppendLine(JsonSerializer.Serialize(tx, Pretty));
        //             sb.AppendLine(new string('─', 80));
        //         }
        //         await System.IO.File.AppendAllTextAsync(PayloadLog, sb.ToString());
        //     }
        //     catch { }
        // });

        foreach (var tx in transactions)
        {
            try
            {
                // Dedup: Helius may deliver the same tx from multiple webhook subscriptions
                if (tx.Signature != null &&
                    await db.SentAlerts.AnyAsync(a => a.Signature == tx.Signature))
                {
                    logger.LogInformation("⏭  skip duplicate sig {Sig}", tx.Signature[..8]);
                    continue;
                }

                var alert = await parser.ParseAsync(tx);
                if (alert is null) continue;

                await dexScreener.EnrichAsync(alert);

                // Fire trade immediately — don't wait for DB/Telegram
                _ = Task.Run(() => autoTrade.TryTradeAsync(alert));

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

                var sentMsgs = await telegram.SendAlertAsync(alert);

                logger.LogInformation("✅ alert sent | #{Id} | {Symbol} | {Amount:N0} locked | sig={Sig}",
                    alert.NotificationId, alert.TokenSymbol, alert.AmountLocked, alert.Signature?[..8]);

                // If market cap was missing, re-enrich in background and edit the message
                if (alert.MarketCapUsd == 0 && sentMsgs.Count > 0)
                    ScheduleReEnrichment(record.Id, alert, sentMsgs);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "❌ error processing tx {Signature}", tx.Signature);
            }
        }

        return Ok();
    }

    private void ScheduleReEnrichment(int alertId, StreamAlert alert, List<(string ChatId, int MsgId)> sentMsgs)
    {
        _ = Task.Run(async () =>
        {
            for (var attempt = 0; attempt < 10; attempt++)
            {
                await Task.Delay(10_000);
                try
                {
                    using var scope = scopeFactory.CreateScope();
                    var ds  = scope.ServiceProvider.GetRequiredService<DexScreenerService>();
                    var tg  = scope.ServiceProvider.GetRequiredService<TelegramService>();
                    var db2 = scope.ServiceProvider.GetRequiredService<AppDbContext>();

                    await ds.EnrichAsync(alert);
                    if (alert.MarketCapUsd == 0) continue;

                    var record = await db2.SentAlerts.FindAsync(alertId);
                    if (record is not null)
                    {
                        record.MarketCapUsd  = alert.MarketCapUsd;
                        record.PercentSupply = alert.PercentSupply;
                        record.PairCreatedAt = alert.PairCreatedAt;
                        record.PairAddress   = alert.PairAddress;
                        record.TokenSymbol   = alert.TokenSymbol;
                        await db2.SaveChangesAsync();
                    }

                    foreach (var (chatId, msgId) in sentMsgs)
                        await tg.EditAlertAsync(chatId, msgId, alert);

                    logger.LogInformation("✅ re-enriched #{Id} | mc=${Mc:N0} | edited {N} messages",
                        alertId, alert.MarketCapUsd, sentMsgs.Count);
                    return;
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Re-enrichment attempt {Attempt} failed for alert #{Id}", attempt + 1, alertId);
                }
            }

            logger.LogWarning("Re-enrichment gave up after 10 attempts for alert #{Id}", alertId);
        });
    }
}
