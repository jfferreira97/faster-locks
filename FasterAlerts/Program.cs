using FasterAlerts.Data;
using FasterAlerts.Services;
using Microsoft.EntityFrameworkCore;

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
builder.Services.AddSingleton<StreamflowParserService>();
builder.Services.AddHttpClient<DexScreenerService>();
builder.Services.AddHttpClient<TelegramService>();
builder.Services.AddSingleton<HeliusBacktestService>();
builder.Services.AddHttpClient<HeliusBacktestService>();

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    db.Database.EnsureCreated();
}

app.MapControllers();

try
{
    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
    {
        FileName = $"http://localhost:{port}/heatmap",
        UseShellExecute = true
    });
}
catch { }

app.Run();
