using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace CustomSync.Capture.Sync;

public static class CaptureSyncRegistration
{
    public static IServiceCollection AddCaptureSyncClient(
        this IServiceCollection services,
        IConfiguration config)
    {
        services.TryAddSingleton(TimeProvider.System);

        services.AddSingleton(sp =>
        {
            var handler = sp.GetService<HttpMessageHandler>();
            return handler != null ? new HttpClient(handler, disposeHandler: false) : new HttpClient();
        });

        services.AddSingleton<CaptureSyncHttpClient>();
        services.AddSingleton<CaptureSyncRunner>();
        services.AddSingleton<CaptureSyncLoop>();

        return services;
    }
}

public static class CaptureSyncStartup
{
    public static Task? Start(IServiceProvider services, ILogger? logger, CancellationToken stoppingToken)
    {
        var runner = services.GetRequiredService<CaptureSyncRunner>();
        if (!runner.IsEnabled)
        {
            return null;
        }

        var loop = services.GetRequiredService<CaptureSyncLoop>();
        var task = Task.Run(() => loop.RunLoopAsync(stoppingToken));

        _ = task.ContinueWith(
            t => logger?.LogError(t.Exception, "Sync push loop faulted."),
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted,
            TaskScheduler.Default);

        return task;
    }
}
