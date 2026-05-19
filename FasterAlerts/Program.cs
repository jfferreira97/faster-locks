using FasterAlerts.Services;

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

builder.Services.AddControllers();
builder.Services.AddSingleton<StreamflowParserService>();
builder.Services.AddHttpClient<DexScreenerService>();
builder.Services.AddHttpClient<TelegramService>();

var app = builder.Build();

app.MapControllers();

app.Run();
