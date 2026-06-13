using FasterAlerts.Data;
using FasterAlerts.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting.WindowsServices;

var builder = WebApplication.CreateBuilder(args);

var port = builder.Configuration.GetValue<int>("HeliusWebhookCallbackPort", 5000);
builder.WebHost.UseUrls($"http://0.0.0.0:{port}");

builder.Host.UseWindowsService();

builder.Logging.ClearProviders();
builder.Logging.AddSimpleConsole(options =>
{
    options.TimestampFormat = "[yyyy-MM-dd HH:mm:ss] ";
    options.SingleLine = true;
});
builder.Logging.SetMinimumLevel(LogLevel.Information);
builder.Logging.AddFilter("Microsoft", LogLevel.Warning);
builder.Logging.AddFilter("System.Net.Http", LogLevel.Warning);

builder.Services.AddDbContext<AppDbContext>(opt =>
    opt.UseSqlite("Data Source=faster-alerts.db"));

builder.Services.AddControllers();
builder.Services.AddHttpClient<StreamflowParserService>();
builder.Services.AddHttpClient<DexScreenerService>();
builder.Services.AddHttpClient<TelegramService>();
builder.Services.AddSingleton<HeliusBacktestService>();
builder.Services.AddHttpClient<HeliusBacktestService>();
builder.Services.AddHttpClient<JupiterService>();
builder.Services.AddSingleton<TradingEventLog>();
builder.Services.AddSingleton<PumpFunMonitorService>();
builder.Services.AddSingleton<AutoTradeService>();
builder.Services.AddHostedService<TelegramPollingService>();

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    db.Database.EnsureCreated();

    // EnsureCreated is a no-op on existing DBs — manually create any tables added after initial deploy
    db.Database.ExecuteSqlRaw("""
        CREATE TABLE IF NOT EXISTS "TradingSettings" (
            "Id"                   INTEGER NOT NULL CONSTRAINT "PK_TradingSettings" PRIMARY KEY AUTOINCREMENT,
            "Enabled"              INTEGER NOT NULL DEFAULT 0,
            "BuySolLamports"       INTEGER NOT NULL DEFAULT 100000000,
            "TrailingStopPercent"  INTEGER NOT NULL DEFAULT 25,
            "MinPercentLocked"     REAL    NOT NULL DEFAULT 1.0,
            "MinMarketCapUsd"      TEXT    NOT NULL DEFAULT '1',
            "MaxMarketCapUsd"      TEXT    NOT NULL DEFAULT '10000',
            "MaxTokenAgeHours"     INTEGER NOT NULL DEFAULT 48,
            "WalletAddress"        TEXT    NOT NULL DEFAULT '',
            "WalletPrivateKeyBase58" TEXT  NOT NULL DEFAULT ''
        )
        """);
    db.Database.ExecuteSqlRaw("""
        CREATE TABLE IF NOT EXISTS "Trades" (
            "Id"               INTEGER NOT NULL CONSTRAINT "PK_Trades" PRIMARY KEY AUTOINCREMENT,
            "TokenMint"        TEXT    NOT NULL DEFAULT '',
            "TokenSymbol"      TEXT    NOT NULL DEFAULT '',
            "EntryTime"        TEXT    NOT NULL DEFAULT '',
            "EntryPriceSol"    REAL    NOT NULL DEFAULT 0,
            "SolSpent"         REAL    NOT NULL DEFAULT 0,
            "TokenAmount"      INTEGER NOT NULL DEFAULT 0,
            "EntryMarketCapUsd" REAL   NOT NULL DEFAULT 0,
            "BuySignature"     TEXT    NOT NULL DEFAULT '',
            "Status"           TEXT    NOT NULL DEFAULT 'Active',
            "CloseTime"        TEXT,
            "ExitPriceSol"     REAL,
            "SellSignature"    TEXT,
            "PnlSol"           REAL,
            "Notes"            TEXT    NOT NULL DEFAULT ''
        )
        """);

    // Add columns that may be missing from older table versions
    EnsureColumn(db, "TradingSettings", "WalletAddress",          "TEXT    NOT NULL DEFAULT ''");
    EnsureColumn(db, "TradingSettings", "WalletPrivateKeyBase58", "TEXT    NOT NULL DEFAULT ''");
    EnsureColumn(db, "TradingSettings", "MinVestingDays",         "INTEGER NOT NULL DEFAULT 0");
    EnsureColumn(db, "TradingSettings", "TakeProfitLevels",       "TEXT    NOT NULL DEFAULT ''");
    db.Database.ExecuteSqlRaw("""
        CREATE TABLE IF NOT EXISTS "TpOrders" (
            "Id"         INTEGER NOT NULL CONSTRAINT "PK_TpOrders" PRIMARY KEY AUTOINCREMENT,
            "TradeId"    INTEGER NOT NULL DEFAULT 0,
            "Threshold"  INTEGER NOT NULL DEFAULT 0,
            "SellPct"    INTEGER NOT NULL DEFAULT 0,
            "FiredAt"    TEXT    NOT NULL DEFAULT '',
            "Status"     TEXT    NOT NULL DEFAULT 'Filled',
            "Signature"  TEXT    NOT NULL DEFAULT '',
            "TokensSold" INTEGER NOT NULL DEFAULT 0
        )
        """);
    EnsureColumn(db, "Trades",          "Notes",                  "TEXT    NOT NULL DEFAULT ''");
    EnsureColumn(db, "Trades",          "Source",                 "TEXT    NOT NULL DEFAULT 'Auto'");
    EnsureColumn(db, "Trades",          "AthMarketCapUsd",        "REAL    NOT NULL DEFAULT 0");
    EnsureColumn(db, "Trades",          "ExitMarketCapUsd",       "REAL    NOT NULL DEFAULT 0");
    EnsureColumn(db, "Trades",          "VestingDays",            "INTEGER NOT NULL DEFAULT 0");
    EnsureColumn(db, "Trades",          "PercentSupply",          "REAL    NOT NULL DEFAULT 0");
    EnsureColumn(db, "Trades",          "LockedUsd",              "REAL    NOT NULL DEFAULT 0");
    EnsureColumn(db, "TpOrders",        "SolReceived",            "REAL    NOT NULL DEFAULT 0");
}

static void EnsureColumn(AppDbContext db, string table, string column, string definition)
{
    var conn = db.Database.GetDbConnection();
    if (conn.State != System.Data.ConnectionState.Open) conn.Open();
    using var cmd = conn.CreateCommand();
    // Skip entirely if the table itself doesn't exist
    cmd.CommandText = $"SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='{table}'";
    if ((long)(cmd.ExecuteScalar() ?? 0L) == 0) return;
    cmd.CommandText = $"SELECT COUNT(*) FROM pragma_table_info('{table}') WHERE name='{column}'";
    if ((long)(cmd.ExecuteScalar() ?? 0L) == 0)
        db.Database.ExecuteSqlRaw($"ALTER TABLE \"{table}\" ADD COLUMN \"{column}\" {definition}");
}

app.MapControllers();

// Restart active trade monitors from DB — must run before app.Run()
await app.Services.GetRequiredService<AutoTradeService>().RecoverActiveTradesAsync();

// When running interactively (dotnet run), open a second window tailing the copytrade log
if (!WindowsServiceHelpers.IsWindowsService())
{
    var logPath = TradingEventLog.LogPath;
    var ps = $"$host.UI.RawUI.WindowTitle='CopyTrade Log'; " +
             $"Write-Host 'Watching {logPath}' -ForegroundColor Cyan; " +
             $"Get-Content '{logPath}' -Wait -Tail 50";
    try
    {
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = $"-NoExit -Command \"{ps}\"",
            UseShellExecute = true,
            CreateNoWindow  = false
        });
    }
    catch { }

    foreach (var url in new[] { $"http://localhost:{port}/heatmap", $"http://localhost:{port}/copytrade" })
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = url,
                UseShellExecute = true
            });
        }
        catch { }
    }
}

app.Run();
