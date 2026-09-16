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

    // Buzuq jadval sozlamasi har daqiqada audit yozmasin: faqat qiymat
    // o'zgarganda bir marta yoziladi.
    private string? _lastReportedScheduleError;

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

                string? hourRaw = null;
                string? minuteRaw = null;
                var settingsLoaded = true;
                try
                {
                    hourRaw = await settings.GetStringAsync("storage.jobs_hour", stoppingToken);
                    minuteRaw = await settings.GetStringAsync("storage.jobs_minute", stoppingToken);
                }
                catch (Exception)
                {
                    // Sozlamalar jadvali hali seed qilinmagan -- standart vaqt.
                    settingsLoaded = false;
                }

                var hour = ArchiveSchedule.DefaultHour;
                var minute = ArchiveSchedule.DefaultMinute;
                if (settingsLoaded &&
                    !ArchiveSchedule.TryResolve(hourRaw, minuteRaw, out hour, out minute, out var scheduleError))
                {
                    // Buzuq jadval bilan standart 03:30 da ishlab ketish kutilmagan
                    // vaqtda o'chirish demakdir. Shuning uchun ish TO'XTATILADI va
                    // sababi audit'ga yoziladi -- aks holda tozalash sababsiz o'chiq
                    // qolardi va buni hech kim sezmasdi.
                    if (_lastReportedScheduleError != scheduleError)
                    {
                        _lastReportedScheduleError = scheduleError;
                        logger.LogError("Archive job jadvali noto'g'ri: {Error}", scheduleError);
                        var audit = scope.ServiceProvider.GetRequiredService<AuditService>();
                        await audit.WriteAsync("archive_job.schedule_invalid",
                            detail: new { error = scheduleError }, ct: stoppingToken);
                    }

                    await Task.Delay(PollInterval, stoppingToken);
                    continue;
                }

                _lastReportedScheduleError = null;

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
