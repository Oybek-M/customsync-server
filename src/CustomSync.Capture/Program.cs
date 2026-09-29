using CustomSync.Capture;
using CustomSync.Capture.Capture;
using CustomSync.Capture.Sync;
using CustomSync.Capture.Tdlib;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

var builder = Host.CreateApplicationBuilder(args);

// Configure native library resolution if specified
TdJsonInterop.ConfigureResolver(builder.Configuration["Telegram:TdJsonPath"]);

bool isInteractiveLogin = args.Contains("--login");

if (args.Contains("--set-key"))
{
    var prompt = new ConsolePrompt();
    return CustomSync.Capture.Sync.SyncCliCommands.SetKey(builder.Configuration, prompt);
}

if (args.Contains("--enroll"))
{
    var prompt = new ConsolePrompt();
    using var http = new HttpClient();
    return await CustomSync.Capture.Sync.SyncCliCommands.EnrollAsync(builder.Configuration, prompt, http);
}

builder.Services.AddSingleton<ITdTransport, NativeTdTransport>();
builder.Services.AddSingleton<ITdClient, TdClient>();
builder.Services.AddSingleton<IConsolePrompt, ConsolePrompt>();
builder.Services.AddSingleton(sp => new TdAuthenticator(
    sp.GetRequiredService<ITdClient>(),
    builder.Configuration,
    sp.GetRequiredService<IConsolePrompt>(),
    isInteractiveLogin,
    sp.GetService<ILogger<TdAuthenticator>>()));

builder.Services.AddMessageCache(builder.Configuration);
builder.Services.AddCaptureHandlers();
builder.Services.AddCaptureSyncClient(builder.Configuration);

builder.Services.AddHostedService<Worker>();

var host = builder.Build();
host.Run();
return 0;
