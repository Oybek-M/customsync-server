using CustomSync.Capture.Tdlib;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace CustomSync.Capture.Capture;

public static class CaptureHandlerRegistration
{
    public static IServiceCollection AddCaptureHandlers(this IServiceCollection services)
    {
        services.TryAddSingleton<ICaptureScope, NoneCaptureScope>();
        services.AddSingleton(sp =>
        {
            var cache = sp.GetRequiredService<MessageCache>();
            var scope = sp.GetRequiredService<ICaptureScope>();
            var timeProvider = sp.GetService<TimeProvider>();
            var logger = sp.GetService<ILogger<CaptureUpdateHandler>>();
            var handler = new CaptureUpdateHandler(cache, scope, timeProvider, logger);

            var client = sp.GetRequiredService<ITdClient>();
            client.UpdateReceived += handler.HandleUpdate;

            return handler;
        });

        return services;
    }
}
