using CustomSync.Capture;
using CustomSync.Capture.Capture;
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

builder.Services.AddMessageCache(builder.Configuration);

builder.Services.AddHostedService<Worker>();

var host = builder.Build();
host.Run();
