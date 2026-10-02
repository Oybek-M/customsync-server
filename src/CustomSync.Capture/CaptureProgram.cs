using CustomSync.Capture.Capture;
using CustomSync.Capture.Sync;
using CustomSync.Capture.Tdlib;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace CustomSync.Capture;

/// <summary>
/// Kirish nuqtasining o'zi. `Program.cs` faqat shuni chaqiradi — testlar
/// jarayon qaytaradigan chiqish kodini haqiqiy yo'l orqali tekshira olishi
/// uchun alohida.
/// </summary>
public static class CaptureProgram
{
    public static async Task<int> RunAsync(string[] args)
    {
        var builder = Host.CreateApplicationBuilder(args);

        // Configure native library resolution if specified
        TdJsonInterop.ConfigureResolver(builder.Configuration["Telegram:TdJsonPath"]);

        bool isInteractiveLogin = args.Contains("--login");

        if (args.Contains("--set-key"))
        {
            var prompt = new ConsolePrompt();
            using var http = new HttpClient();
            return await SyncCliCommands.SetKeyAsync(builder.Configuration, prompt, http);
        }

        if (args.Contains("--enroll"))
        {
            var prompt = new ConsolePrompt();
            using var http = new HttpClient();
            return await SyncCliCommands.EnrollAsync(builder.Configuration, prompt, http);
        }

        builder.Services.AddTdlibClient(builder.Configuration, isInteractiveLogin);

        builder.Services.AddMessageCache(builder.Configuration);
        builder.Services.AddCaptureHandlers();
        builder.Services.AddCaptureSyncClient(builder.Configuration);

        builder.Services.AddHostedService<Worker>();

        var host = builder.Build();
        await host.RunAsync();

        // `Worker` xatoda `Environment.ExitCode` ni qo'yadi. Kirish nuqtasi
        // `int` qaytargani uchun runtime u qiymatni e'tiborsiz qoldiradi —
        // ilgari bu yerdagi `return 0` har qanday xatoni systemd'ga
        // "muvaffaqiyat" qilib ko'rsatardi.
        return Environment.ExitCode;
    }
}
