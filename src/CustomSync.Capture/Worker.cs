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
        var preflight = CapturePreflight.Check(_configuration);
        if (!preflight.Success)
        {
            foreach (var err in preflight.Errors)
            {
                _logger.LogError("Preflight error: {Error}", err);
            }

            Fail();
            return;
        }

        // ITdClient FAQAT preflight o'tgandan keyin olinadi: uni yaratish
        // native kutubxonaga P/Invoke qiladi, ya'ni kutubxona yo'q mashinada
        // konstruktor inyeksiyasi preflight'gacha xostni yiqitardi.
        var client = _services.GetRequiredService<ITdClient>();
        var authenticator = _services.GetRequiredService<TdAuthenticator>();
        var gate = new AuthorizationGate(client, authenticator,
            _services.GetService<ILogger<AuthorizationGate>>());

        var outcome = await gate.RunAsync(TimeSpan.FromMinutes(2), stoppingToken);
        if (!outcome.Ready)
        {
            _logger.LogError(
                "Capture service is not authorized: {Message} Run it once with --login on the VPS.",
                outcome.Message);

            Fail();
            return;
        }

        _logger.LogInformation("Capture service authorized and running.");

        while (!stoppingToken.IsCancellationRequested)
        {
            await Task.Delay(1000, stoppingToken);
        }
    }

    // Nol bo'lmagan chiqish kodi: systemd nol kodni "muvaffaqiyat" deb
    // biladi va `Restart=on-failure` bilan qayta ishga tushirmaydi, ya'ni
    // ishlamayotgan xizmat sog'lom bo'lib ko'rinadi.
    private void Fail()
    {
        Environment.ExitCode = 1;
        _lifetime.StopApplication();
    }
}
