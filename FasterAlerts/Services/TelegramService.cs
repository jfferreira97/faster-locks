using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using FasterAlerts.Models;
using Microsoft.Extensions.Logging;

namespace FasterAlerts.Services;

public class TelegramService(HttpClient http, IConfiguration config, ILogger<TelegramService> logger)
{
    private string BotToken => config["Telegram:BotToken"] ?? "";
    private IEnumerable<string> ChatIds => (config["Telegram:CommaSeperatedChatIds"] ?? "")
        .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    // Returns list of (chatId, messageId) for each successfully sent message
    public async Task<List<(string ChatId, int MsgId)>> SendAlertAsync(StreamAlert alert)
    {
        var sent = new List<(string, int)>();

        if (string.IsNullOrEmpty(BotToken))
        {
            logger.LogWarning("Telegram BotToken not configured — skipping alert");
            return sent;
        }

        var ids = ChatIds.Where(id => !string.IsNullOrWhiteSpace(id)).ToList();
        if (ids.Count == 0)
        {
            logger.LogWarning("No Telegram ChatIds configured — skipping alert");
            return sent;
        }

        var text = BuildMessage(alert);

        foreach (var chatId in ids)
        {
            var payload = new { chat_id = chatId, text, parse_mode = "HTML", disable_web_page_preview = true };
            try
            {
                var content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
                var resp = await http.PostAsync($"https://api.telegram.org/bot{BotToken}/sendMessage", content);
                if (resp.IsSuccessStatusCode)
                {
                    var body = await resp.Content.ReadAsStringAsync();
                    var result = JsonSerializer.Deserialize<TgResponse>(body, JsonOpts);
                    if (result?.Ok == true && result.Result?.MessageId is int msgId)
                        sent.Add((chatId, msgId));
                }
                else
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

        return sent;
    }

    public async Task EditAlertAsync(string chatId, int messageId, StreamAlert alert)
    {
        if (string.IsNullOrEmpty(BotToken)) return;
        try
        {
            var text    = BuildMessage(alert);
            var payload = new { chat_id = chatId, message_id = messageId, text, parse_mode = "HTML", disable_web_page_preview = true };
            var content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
            var resp    = await http.PostAsync($"https://api.telegram.org/bot{BotToken}/editMessageText", content);
            if (!resp.IsSuccessStatusCode)
            {
                var body = await resp.Content.ReadAsStringAsync();
                logger.LogWarning("Telegram editMessageText failed {Status}: {Body}", resp.StatusCode, body);
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to edit Telegram message {MsgId} in {ChatId}", messageId, chatId);
        }
    }

    private static string BuildMessage(StreamAlert a)
    {
        var sb = new StringBuilder();

        var pct = a.PercentSupply > 0 ? $"{a.PercentSupply:F2}%" : "?%";
        var mc  = a.MarketCapUsd > 0 ? $" (${FormatMc(a.MarketCapUsd)} MC)" : "";
        var age = a.PairCreatedAt.HasValue ? $" · {FormatAge(a.PairCreatedAt.Value)}" : "";
        sb.AppendLine($"🔒 <b>{pct} of ${HtmlEncode(a.TokenSymbol)}{mc} LOCKED</b>{age}");
        sb.AppendLine();
        sb.AppendLine($"📝 Contract: <code>{a.TokenMint}</code>");

        if (a.UnlockDate.HasValue && a.UnlockDate.Value.Year >= 2020)
        {
            var until    = a.UnlockDate.Value.ToString("MMM d, yyyy HH:mm UTC");
            var duration = FormatTimeUntil(a.UnlockDate.Value);
            sb.AppendLine($"⏰ For: <b>{duration}</b> | Until {until}");
        }
        else if (a.UnlockDate.HasValue && a.UnlockDate.Value.Year < 2020)
        {
            sb.AppendLine("⏰ <b>Permanent Lock</b>");
        }
        else
        {
            sb.AppendLine("⏰ Until: <b>See Streamflow</b>");
        }

        var streamUrl  = $"https://streamflow.finance/vesting/#/solana/mainnet/{a.StreamAccount}";
        var solscanUrl = $"https://solscan.io/tx/{a.Signature}";
        sb.Append($"🔗 <a href=\"{solscanUrl}\">Solscan</a> | <a href=\"{streamUrl}\">Streamflow</a> | <code>#{a.NotificationId}</code>");

        return sb.ToString();
    }

    private static string FormatAge(DateTimeOffset createdAt)
    {
        var diff    = DateTimeOffset.UtcNow - createdAt;
        var days    = (int)diff.TotalDays;
        var hours   = diff.Hours;
        var minutes = diff.Minutes;

        if (days >= 1)   return hours > 0   ? $"{days}d {hours}h old"    : $"{days}d old";
        if (hours >= 1)  return minutes > 0 ? $"{hours}h {minutes}m old" : $"{hours}h old";
        return $"{minutes}min old";
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

    private static string HtmlEncode(string s) =>
        s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");

    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };
}

file class TgResponse
{
    [JsonPropertyName("ok")]     public bool      Ok     { get; set; }
    [JsonPropertyName("result")] public TgMessage? Result { get; set; }
}

file class TgMessage
{
    [JsonPropertyName("message_id")] public int MessageId { get; set; }
}
