using CustomSync.Capture.Preflight;
using CustomSync.Capture.Tdlib;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace CustomSync.Capture;

public class Worker : BackgroundService
{
    private readonly IConfiguration _configuration;
    private readonly IServiceProvider _services;
    private readonly IHostApplicationLifetime _lifetime;
    private readonly ILogger<Worker> _logger;

    public Worker(
        IConfiguration configuration,
        IServiceProvider services,
        IHostApplicationLifetime lifetime,
        ILogger<Worker> logger)
    {
        _configuration = configuration;
        _services = services;
        _lifetime = lifetime;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var probe = _services.GetService<INativeLibraryProbe>() ?? new SystemNativeLibraryProbe();
        var preflight = CapturePreflight.Check(_configuration, probe.CanLoad, checkDatabaseKey: true);
        if (!preflight.Success)
        {
            foreach (var err in preflight.Errors)
            {
                _logger.LogError("Preflight error: {Error}", err);
            }

            Fail(78);
            return;
        }

        using var loopCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);

        // Keshni ochish va tozalash sikli FAQAT preflight muvaffaqiyatli
        // o'tgandan keyin ishga tushiriladi: aks holda preflight yiqilganda ham
        // SQLite kesh va boshqa fayllar tizimda yaratilib qolardi.
        try
        {
            _ = CustomSync.Capture.Capture.CaptureCacheStartup.Start(
                _services, _services.GetService<ILogger<Worker>>(), loopCts.Token);
            _ = CustomSync.Capture.Sync.CaptureSyncStartup.Start(
                _services, _services.GetService<ILogger<Worker>>(), loopCts.Token);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Message cache or sync client could not be started.");
            Fail(1);
            return;
        }

        // ITdClient FAQAT preflight o'tgandan keyin olinadi: uni yaratish
        // native kutubxonaga P/Invoke qiladi, ya'ni kutubxona yo'q mashinada
        // konstruktor inyeksiyasi preflight'gacha xostni yiqitardi.
        var client = _services.GetRequiredService<ITdClient>();

        // Set log verbosity level before authorization
        try
        {
            int logVerbosity = _configuration.GetValue<int?>("Capture:Tdlib:LogVerbosity") ?? 1;
            var verbosityReq = System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, object?>
            {
                ["@type"] = "setLogVerbosityLevel",
                ["new_verbosity_level"] = logVerbosity
            });
            client.Execute(verbosityReq);
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Failed to set TDLib log verbosity level: {ExceptionType}", ex.GetType().Name);
        }

        var authenticator = _services.GetRequiredService<TdAuthenticator>();
        var gate = new AuthorizationGate(client, authenticator,
            _services.GetService<ILogger<AuthorizationGate>>());

        var outcome = await gate.RunAsync(TimeSpan.FromMinutes(2), stoppingToken);
        if (!outcome.Ready)
        {
            _logger.LogError(
                "Capture service is not authorized: {Message} Run it once with --login on the VPS.",
                outcome.Message);

            Fail(outcome.ExitCode);
            return;
        }

        var invisibilityTimeout = SessionInvisibility.ReadTimeout(_configuration, _logger);
        var invisibility = await SessionInvisibility.EnsureAsync(client, invisibilityTimeout, stoppingToken);
        if (!invisibility.Success)
        {
            _logger.LogError("Capture session invisibility verification failed: {Error}", invisibility.Error);
            Fail(1);
            return;
        }

        int sessionTerminated = 0;
        void OnPostReadyUpdate(string raw)
        {
            if (stoppingToken.IsCancellationRequested || _lifetime.ApplicationStopping.IsCancellationRequested)
            {
                return;
            }

            if (!TryGetAuthorizationState(raw, out var stateType))
            {
                return;
            }

            if (stateType == "authorizationStateReady")
            {
                return;
            }

            if (Interlocked.Exchange(ref sessionTerminated, 1) == 0)
            {
                _logger.LogError("Telegram authorization state changed to {State}. Session is terminated or revoked; stopping capture.", stateType);
                loopCts.Cancel();
                Fail(78);
            }
        }

        client.UpdateReceived += OnPostReadyUpdate;
        try
        {
            _services.GetRequiredService<CustomSync.Capture.Media.MediaDownloader>().Start(loopCts.Token);
            _services.GetRequiredService<CustomSync.Capture.Maintenance.StorageMaintenance>().Start(loopCts.Token);
            _services.GetRequiredService<CustomSync.Capture.Capture.ProfilePhotoLookup>().Start(loopCts.Token);

            _logger.LogInformation("Capture service authorized and running.");

            while (!stoppingToken.IsCancellationRequested && sessionTerminated == 0)
            {
                await Task.Delay(1000, stoppingToken);
            }
        }
        finally
        {
            client.UpdateReceived -= OnPostReadyUpdate;
        }
    }

    private static bool TryGetAuthorizationState(string raw, out string? stateType)
    {
        stateType = null;
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(raw);
            if (doc.RootElement.TryGetProperty("@type", out var type)
                && type.GetString() == "updateAuthorizationState"
                && doc.RootElement.TryGetProperty("authorization_state", out var authState)
                && authState.TryGetProperty("@type", out var stateTypeProp))
            {
                stateType = stateTypeProp.GetString();
                return !string.IsNullOrEmpty(stateType);
            }
            return false;
        }
        catch (System.Text.Json.JsonException)
        {
            return false;
        }
    }

    // Nol bo'lmagan chiqish kodi: systemd nol kodni "muvaffaqiyat" deb
    // biladi va `Restart=on-failure` bilan qayta ishga tushirmaydi, ya'ni
    // ishlamayotgan xizmat sog'lom bo'lib ko'rinadi.
    private void Fail(int exitCode = 1)
    {
        Environment.ExitCode = exitCode;
        _lifetime.StopApplication();
    }
}
