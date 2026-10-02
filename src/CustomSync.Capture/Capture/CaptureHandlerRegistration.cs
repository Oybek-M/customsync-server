using CustomSync.Capture.Maintenance;
using CustomSync.Capture.Media;
using CustomSync.Capture.Tdlib;
using Microsoft.Extensions.Configuration;
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

        services.TryAddSingleton<ISyncedScopeSettingsSource, NullSyncedScopeSettingsSource>();
        services.TryAddSingleton<ICaptureScope>(sp =>
        {
            var config = sp.GetService<IConfiguration>();
            var settingsSource = sp.GetService<ISyncedScopeSettingsSource>();
            return config is not null
                ? new CaptureScopeEvaluator(config, settingsSource)
                : new CaptureScopeEvaluator(null, null, false, settingsSource);
        });

        services.TryAddSingleton<ISyncedActivityScopeSettingsSource, NullSyncedActivityScopeSettingsSource>();
        services.TryAddSingleton<IActivityScope>(sp =>
        {
            var config = sp.GetService<IConfiguration>();
            var settingsSource = sp.GetService<ISyncedActivityScopeSettingsSource>();
            return config is not null
                ? new ActivityScopeEvaluator(config, settingsSource)
                : new ActivityScopeEvaluator(null, null, false, settingsSource);
        });

        services.TryAddSingleton<IDiskSpaceProbe, SystemDiskSpaceProbe>();

        services.TryAddSingleton<MediaCaptureConfig>(sp =>
        {
            var config = sp.GetService<IConfiguration>();
            return MediaCaptureConfig.From(config);
        });

        services.TryAddSingleton<MediaStore>(sp =>
        {
            var cfg = sp.GetRequiredService<MediaCaptureConfig>();
            return new MediaStore(cfg.StorageDirectory);
        });

        services.TryAddSingleton<MediaDownloader>(sp =>
        {
            return new MediaDownloader(
                sp.GetRequiredService<ITdClient>(),
                sp.GetRequiredService<MessageCache>(),
                sp.GetRequiredService<MediaCaptureConfig>(),
                sp.GetRequiredService<MediaStore>(),
                sp.GetRequiredService<IDiskSpaceProbe>(),
                sp.GetService<TimeProvider>(),
                sp.GetService<ILogger<MediaDownloader>>());
        });

        services.TryAddSingleton<ICaptureHealthReporter, NullCaptureHealthReporter>();

        services.TryAddSingleton<CustomSync.Capture.Maintenance.StorageMaintenance>(sp =>
        {
            var config = sp.GetService<IConfiguration>();
            int retentionDays = CaptureCacheRegistration.ReadPositiveInt(
                config?["Capture:CacheRetentionDays"], 30);

            return new CustomSync.Capture.Maintenance.StorageMaintenance(
                sp.GetRequiredService<MessageCache>(),
                sp.GetRequiredService<MediaStore>(),
                sp.GetRequiredService<ITdClient>(),
                sp.GetRequiredService<IDiskSpaceProbe>(),
                sp.GetRequiredService<MediaCaptureConfig>(),
                retentionDays,
                sp.GetService<ILogger<CustomSync.Capture.Maintenance.StorageMaintenance>>(),
                timeProvider: sp.GetService<TimeProvider>(),
                reporter: sp.GetRequiredService<ICaptureHealthReporter>());
        });

        services.AddSingleton(sp =>
        {
            var config = sp.GetService<IConfiguration>();
            int pairingTimeout = CaptureCacheRegistration.ReadPositiveInt(
                config?["Capture:EditPairingTimeoutSeconds"],
                CaptureCacheRegistration.DefaultEditPairingTimeoutSeconds);

            return new CaptureUpdateHandler(
                sp.GetRequiredService<MessageCache>(),
                sp.GetRequiredService<ICaptureScope>(),
                sp.GetRequiredService<IActivityScope>(),
                sp.GetService<TimeProvider>(),
                sp.GetService<ILogger<CaptureUpdateHandler>>(),
                editPairingTimeoutSeconds: pairingTimeout,
                mediaConfig: sp.GetService<MediaCaptureConfig>());
        });

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
