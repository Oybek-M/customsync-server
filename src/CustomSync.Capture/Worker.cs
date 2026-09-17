using CustomSync.Capture.Preflight;
using CustomSync.Capture.Tdlib;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace CustomSync.Capture;

public class Worker : BackgroundService
{
    private readonly IConfiguration _configuration;
    private readonly IHostApplicationLifetime _lifetime;
    private readonly ILogger<Worker> _logger;

    public Worker(
        IConfiguration configuration,
        IHostApplicationLifetime lifetime,
        ILogger<Worker> logger)
    {
        _configuration = configuration;
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
            _lifetime.StopApplication();
            return;
        }

        _logger.LogInformation("Capture service initialized successfully.");

        while (!stoppingToken.IsCancellationRequested)
        {
            await Task.Delay(1000, stoppingToken);
        }
    }
}
