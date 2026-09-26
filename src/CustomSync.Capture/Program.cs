using CustomSync.Capture;
using CustomSync.Capture.Tdlib;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

var builder = Host.CreateApplicationBuilder(args);

// Configure native library resolution if specified
TdJsonInterop.ConfigureResolver(builder.Configuration["Telegram:TdJsonPath"]);

bool isInteractiveLogin = args.Contains("--login");

builder.Services.AddSingleton<ITdTransport, NativeTdTransport>();
builder.Services.AddSingleton<ITdClient, TdClient>();
builder.Services.AddSingleton<IConsolePrompt, ConsolePrompt>();
builder.Services.AddSingleton(sp => new TdAuthenticator(
    sp.GetRequiredService<ITdClient>(),
    builder.Configuration,
    sp.GetRequiredService<IConsolePrompt>(),
    isInteractiveLogin,
    sp.GetService<ILogger<TdAuthenticator>>()));

builder.Services.AddSingleton(sp =>
{
    var cacheDbPath = builder.Configuration["Capture:CacheDatabasePath"] ?? "/var/lib/customsync-capture/message-cache.db";
    return new CustomSync.Capture.Capture.MessageCache(cacheDbPath);
});
builder.Services.AddSingleton(sp =>
{
    var cache = sp.GetRequiredService<CustomSync.Capture.Capture.MessageCache>();
    int retentionDays = 30;
    if (int.TryParse(builder.Configuration["Capture:CacheRetentionDays"], out var rDays) && rDays > 0)
    {
        retentionDays = rDays;
    }
    int pruneIntervalHours = 6;
    if (int.TryParse(builder.Configuration["Capture:CachePruneIntervalHours"], out var pHours) && pHours > 0)
    {
        pruneIntervalHours = pHours;
    }
    return new CustomSync.Capture.Capture.PeriodicCachePruner(
        cache,
        retentionDays,
        TimeSpan.FromHours(pruneIntervalHours),
        sp.GetService<ILogger<CustomSync.Capture.Capture.PeriodicCachePruner>>());
});

builder.Services.AddHostedService<Worker>();

var host = builder.Build();
host.Run();
