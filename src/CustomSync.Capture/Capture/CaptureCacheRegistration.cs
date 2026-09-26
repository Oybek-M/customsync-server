using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace CustomSync.Capture.Capture;

/// <summary>
/// Kesh xizmatlarini ro'yxatga oladi. `Program.cs` ning ichida turganda
/// buni test qilishning yo'li yo'q edi, shuning uchun testlar o'zining
/// nusxa ro'yxatini yasab, haqiqiy ro'yxatni tekshirmasdi.
/// </summary>
public static class CaptureCacheRegistration
{
    public const string DefaultCachePath = "/var/lib/customsync-capture/message-cache.db";
    public const int DefaultRetentionDays = 30;
    public const int DefaultPruneIntervalHours = 6;

    public static IServiceCollection AddMessageCache(this IServiceCollection services, IConfiguration config)
    {
        services.AddSingleton(_ => new MessageCache(
            config["Capture:CacheDatabasePath"] ?? DefaultCachePath));

        services.AddSingleton(sp =>
        {
            var retentionDays = ReadPositiveInt(config["Capture:CacheRetentionDays"], DefaultRetentionDays);
            var pruneIntervalHours = ReadPositiveInt(config["Capture:CachePruneIntervalHours"], DefaultPruneIntervalHours);

            return new PeriodicCachePruner(
                sp.GetRequiredService<MessageCache>(),
                retentionDays,
                TimeSpan.FromHours(pruneIntervalHours),
                sp.GetService<ILogger<PeriodicCachePruner>>());
        });

        return services;
    }

    private static int ReadPositiveInt(string? raw, int fallback)
        => int.TryParse(raw, out var value) && value > 0 ? value : fallback;
}
