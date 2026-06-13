using System.Text;
using FasterAlerts.Data;
using Microsoft.EntityFrameworkCore;
using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;

namespace FasterAlerts.Services;

public class TelegramPollingService(
    DexScreenerService dexScreener,
    IServiceScopeFactory scopeFactory,
    IConfiguration config,
    ILogger<TelegramPollingService> logger) : BackgroundService
{
    private TelegramBotClient? _bot;
    private int _offset;
    private HashSet<long> _allowedChats = new();

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        var token = config["Telegram:BotToken"];
        if (string.IsNullOrWhiteSpace(token))
        {
            logger.LogWarning("TelegramPolling: BotToken not configured — skipping");
            return;
        }

        _bot          = new TelegramBotClient(token);
        _allowedChats = ParseChatIds(config["Telegram:CommaSeperatedChatIds"] ?? "");

        logger.LogInformation("TelegramPolling started");

        while (!ct.IsCancellationRequested)
        {
            try
            {
                var updates = await _bot.GetUpdatesAsync(
                    offset: _offset,
                    timeout: 30,
                    cancellationToken: ct);

                foreach (var update in updates)
                {
                    _offset = update.Id + 1;
                    try   { await HandleUpdateAsync(update, ct); }
                    catch (Exception ex) { logger.LogError(ex, "Error handling Telegram update {Id}", update.Id); }
                }
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                logger.LogError(ex, "TelegramPolling loop error");
                try { await Task.Delay(5000, ct); } catch { break; }
            }
        }

        logger.LogInformation("TelegramPolling stopped");
    }

    private async Task HandleUpdateAsync(Update update, CancellationToken ct)
    {
        if (update.Message is not { } msg) return;
        if (msg.Text is not { } text || !text.StartsWith("/")) return;

        var chatId = msg.Chat.Id;
        if (_allowedChats.Count > 0 && !_allowedChats.Contains(chatId)) return;

        var parts = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var cmd   = parts[0].Split('@')[0].ToLowerInvariant();

        switch (cmd)
        {
            case "/top":
                if (parts.Length == 1) { await SendTopHelpAsync(chatId, ct); break; }
                await HandleTopAsync(chatId, parts, sortByVol: false, ct);
                break;
            case "/topvol":
                if (parts.Length == 1) { await SendTopHelpAsync(chatId, ct); break; }
                await HandleTopAsync(chatId, parts, sortByVol: true, ct);
                break;
        }
    }

    private async Task HandleTopAsync(long chatId, string[] parts, bool sortByVol, CancellationToken ct)
    {
        var count   = Math.Clamp(TryGetArg(parts, 1, 10), 1, 30);
        var minLock = (decimal)TryGetArg(parts, 2, 1);
        var maxAgeH = TryGetArg(parts, 3, 48);
        var since   = DateTimeOffset.UtcNow.AddHours(-maxAgeH);

        List<FasterAlerts.Models.SentAlert> alerts;
        using (var scope = scopeFactory.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            // SQLite EF Core can't translate DateTimeOffset/decimal comparisons — filter client-side
            var all = await db.SentAlerts.ToListAsync(ct);
            alerts = all
                .Where(a => a.SentAt >= since && a.PercentSupply >= minLock)
                .OrderByDescending(a => a.SentAt)
                .Take(count * 3)
                .ToList();

        }

        if (alerts.Count == 0)
        {
            await _bot!.SendTextMessageAsync(chatId,
                $"No locks found in the last {maxAgeH}h with ≥{minLock}% locked.",
                cancellationToken: ct);
            return;
        }

        var mints     = alerts.Select(a => a.TokenMint).Distinct().ToList();
        var snapshots = await dexScreener.GetTokensInfoAsync(mints);

        var rows = alerts
            .Select(a => (a, snap: snapshots.GetValueOrDefault(a.TokenMint)))
            .ToList();

        if (sortByVol)
            rows = rows.OrderByDescending(r => r.snap?.Vol24h ?? 0).ToList();

        rows = rows.Take(count).ToList();

        var header = sortByVol
            ? $"📊 <b>Top Volume Since Lock</b> — last {maxAgeH}h · min {minLock}%"
            : $"🔒 <b>Top Locks</b> — last {maxAgeH}h · min {minLock}%";

        var sb = new StringBuilder();
        sb.AppendLine(header);
        sb.AppendLine();

        for (int i = 0; i < rows.Count; i++)
        {
            var (a, snap) = rows[i];
            var nowMc  = snap?.MarketCapUsd ?? 0;
            var lockMc = a.MarketCapUsd;

            var mcLine = (lockMc > 0, nowMc > 0) switch
            {
                (true,  true)  => $"{FormatMc(lockMc)} (@ lock) → {FormatMc(nowMc)} (Current)",
                (true,  false) => $"{FormatMc(lockMc)} (@ lock)",
                (false, true)  => $"{FormatMc(nowMc)} (Current)",
                _              => "n/a"
            };

            var lockDur  = FormatLockDuration(a.SentAt, a.UnlockDate);
            var lockInfo = $"{a.PercentSupply:F1}%" + (lockDur != "" ? $" · {lockDur}" : "") + $" · {FormatAge(a.SentAt)}";

            sb.AppendLine($"{i + 1}. <b>${HtmlEncode(a.TokenSymbol)}</b> · <code>{a.TokenMint}</code>");

            if (sortByVol && snap != null && snap.Vol24h > 0)
                sb.AppendLine($"   📊 Vol 24H: {FormatMc(snap.Vol24h)}");

            sb.AppendLine($"   🔒 {lockInfo}");
            sb.AppendLine($"   💰 MC: {mcLine}");
            sb.AppendLine();
        }

        var text = sb.ToString().TrimEnd();
        if (text.Length > 4000) text = text[..4000] + "\n…";

        await _bot!.SendTextMessageAsync(chatId, text, parseMode: ParseMode.Html, cancellationToken: ct);
    }

    private Task SendTopHelpAsync(long chatId, CancellationToken ct) =>
        _bot!.SendTextMessageAsync(chatId, """
            📖 <b>Lock Scanner Commands</b>

            <b>/top</b> <code>[N] [minLock%] [maxAgeH]</code>
            Recent significant locks, newest first.

            <b>/topvol</b> <code>[N] [minLock%] [maxAgeH]</code>
            Same list sorted by volume since the lock.

            <b>Parameters</b> (all optional, positional):
              <code>N</code>        — how many results (default 10, max 30)
              <code>minLock%</code> — minimum % of supply locked (default 1)
              <code>maxAgeH</code>  — only include locks from last N hours (default 48)

            <b>Examples:</b>
            <code>/top</code>              — 10 newest locks, last 48h
            <code>/top 5 3 24</code>      — top 5, min 3% locked, last 24h
            <code>/topvol 10 2 72</code>  — top 10 by volume, min 2%, last 72h
            <code>/topvol 20 5</code>     — top 20 by volume, min 5%, last 48h
            """,
            parseMode: ParseMode.Html, cancellationToken: ct);

    // ── helpers ───────────────────────────────────────────────────────────────

    private static int TryGetArg(string[] parts, int index, int def) =>
        parts.Length > index && int.TryParse(parts[index], out var v) ? v : def;

    private static HashSet<long> ParseChatIds(string raw)
    {
        var result = new HashSet<long>();
        foreach (var part in raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            if (long.TryParse(part, out var id)) result.Add(id);
        return result;
    }

    private static (decimal vol, string window) ApproxVol(DsTokenSnapshot snap, DateTimeOffset sentAt)
    {
        var age = DateTimeOffset.UtcNow - sentAt;
        if (age.TotalHours <= 1.5) return (snap.Vol1h,  "1h");
        if (age.TotalHours <= 9)   return (snap.Vol6h,  "6h");
        return                            (snap.Vol24h, "24h");
    }

    private static string FormatAge(DateTimeOffset sentAt)
    {
        var d = DateTimeOffset.UtcNow - sentAt;
        if (d.TotalDays  >= 1) return d.Hours > 0 ? $"{(int)d.TotalDays}d {d.Hours}h ago" : $"{(int)d.TotalDays}d ago";
        if (d.TotalHours >= 1) return d.Minutes > 0 ? $"{d.Hours}h {d.Minutes}m ago" : $"{d.Hours}h ago";
        return $"{d.Minutes}m ago";
    }

    private static string FormatLockDuration(DateTimeOffset sentAt, DateTimeOffset? unlockDate)
    {
        if (unlockDate is null) return "";
        if (unlockDate.Value.Year < 2020) return "permanent";
        var dur = unlockDate.Value - sentAt;
        if (dur.TotalDays  >= 1) return $"{(int)dur.TotalDays}d lock";
        if (dur.TotalHours >= 1) return $"{dur.Hours}h lock";
        return "";
    }

    private static string FormatMc(decimal mc) => mc switch
    {
        >= 1_000_000_000 => $"${mc / 1_000_000_000:0.##}B",
        >= 1_000_000     => $"${mc / 1_000_000:0.##}M",
        >= 1_000         => $"${mc / 1_000:0.##}K",
        _                => $"${mc:0}"
    };

    private static string McChange(decimal lockMc, decimal nowMc)
    {
        if (lockMc <= 0) return "";
        var pct = (nowMc - lockMc) / lockMc * 100;
        return pct >= 0 ? $"🟢 +{pct:0.#}%" : $"🔴 {pct:0.#}%";
    }

    private static string HtmlEncode(string s) =>
        s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");
}
