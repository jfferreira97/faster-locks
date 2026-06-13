using System.Collections.Concurrent;
using System.Text;

namespace FasterAlerts.Services;

public class TradingEventLog : IDisposable
{
    private readonly ConcurrentQueue<TradingLogEntry> _entries = new();
    private const int MaxEntries = 200;
    private readonly StreamWriter? _file;

    public static string LogPath { get; } =
        Path.Combine(AppContext.BaseDirectory, "copytrade.log");

    public TradingEventLog()
    {
        try
        {
            _file = new StreamWriter(LogPath, append: true, Encoding.UTF8) { AutoFlush = true };
        }
        catch { }
    }

    public void Add(string level, string message)
    {
        var entry = new TradingLogEntry(DateTimeOffset.UtcNow, level, message);
        _entries.Enqueue(entry);
        while (_entries.Count > MaxEntries)
            _entries.TryDequeue(out _);
        try { _file?.WriteLine($"[{entry.Time:yyyy-MM-dd HH:mm:ss}] [{level,-5}] {message}"); }
        catch { }
    }

    public void Info(string msg)  => Add("INFO",  msg);
    public void Error(string msg) => Add("ERROR", msg);
    public void Warn(string msg)  => Add("WARN",  msg);

    public IReadOnlyList<TradingLogEntry> GetRecent() => _entries.Reverse().Take(100).ToList();

    public void Dispose() => _file?.Dispose();
}

public record TradingLogEntry(DateTimeOffset Time, string Level, string Message);
