using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace CustomSync.Capture.Capture;

/// <summary>
/// Keshni ishga tushiradi va davriy tozalash siklini boshlaydi.
///
/// Alohida klass, chunki `Worker` ichida bu `GetService` + `?.` bilan
/// yozilgan edi: ro'yxatdan o'tmagan xizmat xatosiz "kesh yo'q" holatiga
/// olib kelardi va hech bir test buni ushlamasdi (`TdRedactor` bilan bir
/// xil xato). `GetRequiredService` baland ovozda yiqiladi.
/// </summary>
public static class CaptureCacheStartup
{
    /// <summary>
    /// Keshni ochadi va tozalash siklini qaytaradi. Ro'yxat to'liq
    /// bo'lmasa <see cref="InvalidOperationException"/> tashlaydi.
    /// </summary>
    public static Task Start(IServiceProvider services, ILogger? logger, CancellationToken stoppingToken)
    {
        var cache = services.GetRequiredService<MessageCache>();
        var pruner = services.GetRequiredService<PeriodicCachePruner>();

        cache.Initialize();

        // Task.Run ga token berilmaydi: bekor qilingan token bilan vazifa
        // hatto boshlanmay "canceled" bo'lib qoladi va sikl o'zining
        // to'xtash yo'lini bosib o'tmaydi.
        var loop = Task.Run(() => pruner.RunLoopAsync(stoppingToken));

        // Kuzatuvsiz vazifa qoldirmaymiz: siklning xatosi hech bo'lmaganda
        // log ga tushsin, aks holda tozalash jimgina to'xtaydi.
        _ = loop.ContinueWith(
            t => logger?.LogError(t.Exception, "Cache prune loop faulted; the cache is no longer pruned."),
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted,
            TaskScheduler.Default);

        return loop;
    }
}
