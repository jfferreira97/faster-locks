using System.Text;
using System.Text.Json;
using FasterAlerts.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace FasterAlerts.Services;

public class TelegramService(HttpClient http, IConfiguration config, ILogger<TelegramService> logger)
{
    private string BotToken => config["Telegram:BotToken"] ?? "";
    private IEnumerable<string> ChatIds => (config["Telegram:CommaSeperatedChatIds"] ?? "")
        .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    public async Task SendAlertAsync(StreamAlert alert)
    {
        if (string.IsNullOrEmpty(BotToken))
        {
            logger.LogWarning("Telegram BotToken not configured — skipping alert");
            return;
        }

        var ids = ChatIds.Where(id => !string.IsNullOrWhiteSpace(id)).ToList();
        if (ids.Count == 0)
        {
            logger.LogWarning("No Telegram ChatIds configured — skipping alert");
            return;
        }

        var text = BuildMessage(alert);

        foreach (var chatId in ids)
        {
            var payload = new { chat_id = chatId, text, parse_mode = "HTML", disable_web_page_preview = true };
            try
            {
                var content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
                var resp = await http.PostAsync($"https://api.telegram.org/bot{BotToken}/sendMessage", content);
                if (!resp.IsSuccessStatusCode)
                {
                    var body = await resp.Content.ReadAsStringAsync();
                    logger.LogError("Telegram sendMessage failed for {ChatId} {Status}: {Body}", chatId, resp.StatusCode, body);
                }
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to send Telegram alert to {ChatId}", chatId);
            }
        }
    }

    private static string BuildMessage(StreamAlert a)
    {
        var sb = new StringBuilder();

        // Line 1: pct of supply + symbol + market cap
        var pct = a.PercentSupply > 0 ? $"{a.PercentSupply:F1}%" : "?%";
        var mc  = a.MarketCapUsd > 0 ? $" (${FormatMc(a.MarketCapUsd)} MC)" : "";
        sb.AppendLine($"🔒 <b>{pct} of ${HtmlEncode(a.TokenSymbol)}{mc} LOCKED</b>");
        sb.AppendLine();
        sb.AppendLine($"📝 Contract: <code>{a.TokenMint}</code>");

        if (a.UnlockDate.HasValue)
        {
            var until    = a.UnlockDate.Value.ToString("MMM d, yyyy HH:mm UTC");
            var duration = FormatTimeUntil(a.UnlockDate.Value);
            sb.AppendLine($"⏰ For: <b>{duration}</b> | Until {until}");
        }
        else
        {
            sb.AppendLine("⏰ Until: <b>See Streamflow</b>");
        }

        var streamUrl  = $"https://streamflow.finance/vesting/#/solana/mainnet/{a.StreamAccount}";
        var solscanUrl = $"https://solscan.io/tx/{a.Signature}";
        sb.Append($"🔗 <a href=\"{solscanUrl}\">Solscan</a> | <a href=\"{streamUrl}\">Streamflow</a>");

        return sb.ToString();
    }

    private static string FormatTimeUntil(DateTimeOffset unlockDate)
    {
        var diff = unlockDate - DateTimeOffset.UtcNow;
        if (diff <= TimeSpan.Zero) return "imminent";

        var totalDays = (int)diff.TotalDays;
        var hours     = diff.Hours;
        var minutes   = diff.Minutes;

        if (totalDays >= 60)
        {
            var months = totalDays / 30;
            var days   = totalDays % 30;
            return days > 0 ? $"{months}mo {days}d" : $"{months}mo";
        }
        if (totalDays >= 1)
            return hours > 0 ? $"{totalDays}d {hours}h" : $"{totalDays}d";

        return minutes > 0 ? $"{hours}h {minutes}m" : $"{hours}h";
    }

    private static string FormatMc(decimal mc) => mc switch
    {
        >= 1_000_000_000 => $"{mc / 1_000_000_000:0.##}b",
        >= 1_000_000     => $"{mc / 1_000_000:0.##}m",
        _                => $"{mc / 1_000:0.##}k"
    };

    private static string ShortAddr(string addr)
    {
        if (string.IsNullOrEmpty(addr)) return "unknown";
        return addr.Length > 12 ? $"{addr[..6]}...{addr[^6..]}" : addr;
    }

    private static string HtmlEncode(string s) =>
        s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");

    private static string FormatAgo(DateTimeOffset ts)
    {
        var diff = DateTimeOffset.UtcNow - ts;
        if (diff.TotalSeconds < 60) return "Just now";
        if (diff.TotalMinutes < 60) return $"{(int)diff.TotalMinutes} min ago";
        if (diff.TotalHours < 24) return $"{(int)diff.TotalHours} hr ago";
        return ts.ToString("MMM d, HH:mm UTC");
    }
}
