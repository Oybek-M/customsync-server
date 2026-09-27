using CustomSync.Capture.Tdlib;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace CustomSync.Capture.Capture;

public static class CaptureHandlerRegistration
{
    /// <summary>
    /// Handler'ni ro'yxatga oladi va mavjud ITdClient registratsiyasini
    /// o'raydi: mijoz yaratilgan zahoti handler unga ulanadi. Ulash alohida
    /// "handler'ni resolve qilish" qatoriga bog'liq emas — u qator o'chib
    /// qolsa xizmat ishlab turgandek ko'rinib, hech narsa ushlamasdi.
    /// ITdClient'dan KEYIN chaqirilishi shart.
    /// </summary>
    public static IServiceCollection AddCaptureHandlers(this IServiceCollection services)
    {
        var clientDescriptor = services.LastOrDefault(d => d.ServiceType == typeof(ITdClient))
            ?? throw new InvalidOperationException(
                "ITdClient must be registered before AddCaptureHandlers; otherwise the handler is never attached.");

        services.TryAddSingleton<ICaptureScope, NoneCaptureScope>();
        services.AddSingleton(sp => new CaptureUpdateHandler(
            sp.GetRequiredService<MessageCache>(),
            sp.GetRequiredService<ICaptureScope>(),
            sp.GetService<TimeProvider>(),
            sp.GetService<ILogger<CaptureUpdateHandler>>()));

        services.Remove(clientDescriptor);
        services.AddSingleton<ITdClient>(sp =>
        {
            var client = CreateInner(sp, clientDescriptor);
            sp.GetRequiredService<CaptureUpdateHandler>().Attach(client);
            return client;
        });

        return services;
    }

    private static ITdClient CreateInner(IServiceProvider sp, ServiceDescriptor descriptor)
    {
        if (descriptor.ImplementationInstance is ITdClient instance)
            return instance;
        if (descriptor.ImplementationFactory is not null)
            return (ITdClient)descriptor.ImplementationFactory(sp);
        return (ITdClient)ActivatorUtilities.CreateInstance(sp, descriptor.ImplementationType!);
    }
}
