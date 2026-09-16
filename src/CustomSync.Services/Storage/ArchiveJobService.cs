using CustomSync.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace CustomSync.Services.Storage;

public class ArchiveJobService(
    IServiceScopeFactory scopeFactory,
    ILogger<ArchiveJobService> logger) : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMinutes(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Xost to'liq ishga tushishi va migratsiyalar o'tishini kutish
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = scopeFactory.CreateScope();
                var settings = scope.ServiceProvider.GetRequiredService<SettingsService>();
                var db = scope.ServiceProvider.GetRequiredService<SyncDbContext>();

                int hour = 3;
                int minute = 30;
                try
                {
                    hour = await settings.GetIntAsync("storage.jobs_hour", stoppingToken);
                    minute = await settings.GetIntAsync("storage.jobs_minute", stoppingToken);
                }
                catch (Exception)
                {
                    // Sozlamalar jadvali hali to'liq bo'lmasa standart qiymatda qoladi
                }

                DateOnly? lastRunDate = null;
                try
                {
                    lastRunDate = await db.ArchiveJobRuns.AsNoTracking()
                        .OrderByDescending(r => r.RunDate)
                        .Select(r => (DateOnly?)r.RunDate)
                        .FirstOrDefaultAsync(stoppingToken);
                }
                catch (Exception)
                {
                    // Migratsiya o'tmagan bo'lsa serverni yiqitmaydi
                }

                var nowUtc = DateTime.UtcNow;
                if (ArchiveSchedule.IsDue(nowUtc, hour, minute, lastRunDate))
                {
                    var runner = scope.ServiceProvider.GetRequiredService<ArchiveJobRunner>();
                    await runner.RunOnceAsync(nowUtc, stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Scheduled archive job tsiklida kutilmagan xato yuz berdi");
            }

            try
            {
                await Task.Delay(PollInterval, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }
}
