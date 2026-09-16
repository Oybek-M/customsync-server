using System.Globalization;
using System.Text.Json;
using CustomSync.Core.Contracts;
using CustomSync.Data;
using CustomSync.Data.Entities;
using CustomSync.Services.Storage.Targets;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace CustomSync.Services.Storage;

public sealed record ArchiveJobReport(
    string Status,
    DateOnly RunDate,
    int PoliciesRun,
    int PoliciesSkipped,
    int PoliciesFailed,
    int TotalDeletedRecords,
    long TotalFreedBytes,
    string? Message = null)
{
    public static ArchiveJobReport Skipped(DateOnly date, string message) =>
        new(ArchiveJobRunStatus.Skipped, date, 0, 0, 0, 0, 0, message);
}

public class ArchiveJobRunner(
    IServiceScopeFactory scopeFactory,
    IDiskProbe diskProbe,
    IConfiguration? configuration = null,
    ILogger<ArchiveJobRunner>? logger = null)
{
    public int BatchLimit { get; set; } = 5000;

    /// <summary>
    /// Sozlamani xom matndan o'qiydi. Qiymat buzuq bo'lsa istisno tashlamaydi:
    /// standart qiymatga qaytadi va `archive_job.config_invalid` audit yozadi.
    /// Sozlamalar admin endpoint'i orqali har qanday matn bo'lishi mumkin
    /// (`SetAsync` tip tekshirmaydi), nazoratsiz ishlaydigan job esa bitta
    /// xato harfdan butunlay to'xtab qolmasligi kerak. Standart qiymatlar
    /// xavfsiz tomonga qaraydi: `jobs_enabled` -> false, `dry_run` -> true.
    /// </summary>
    private static async Task<int> ReadIntAsync(
        SettingsService settings, AuditService audit, string key, int fallback, CancellationToken ct)
    {
        string raw;
        try { raw = await settings.GetStringAsync(key, ct); }
        catch (KeyNotFoundException) { return fallback; }

        if (int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
        {
            return value;
        }

        await audit.WriteAsync("archive_job.config_invalid",
            detail: new { key, value = raw, usedFallback = fallback }, ct: ct);
        return fallback;
    }

    private static async Task<bool> ReadBoolAsync(
        SettingsService settings, AuditService audit, string key, bool fallback, CancellationToken ct)
    {
        string raw;
        try { raw = await settings.GetStringAsync(key, ct); }
        catch (KeyNotFoundException) { return fallback; }

        if (bool.TryParse(raw, out var value))
        {
            return value;
        }

        await audit.WriteAsync("archive_job.config_invalid",
            detail: new { key, value = raw, usedFallback = fallback }, ct: ct);
        return fallback;
    }

    private async Task CheckThresholdsAsync(CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var settings = scope.ServiceProvider.GetRequiredService<SettingsService>();
        var audit = scope.ServiceProvider.GetRequiredService<AuditService>();
        var statsService = scope.ServiceProvider.GetRequiredService<StatsService>();

        var diskCapacityMb  = await ReadIntAsync(settings, audit, "storage.disk_capacity_mb", 0, ct);
        var warnPercent     = await ReadIntAsync(settings, audit, "storage.warn_percent", 80, ct);
        var criticalPercent = await ReadIntAsync(settings, audit, "storage.critical_percent", 92, ct);

        // Chegaralarni tekshirish: 1 <= warn < critical <= 100
        if (warnPercent < 1 || warnPercent >= criticalPercent || criticalPercent > 100)
        {
            await audit.WriteAsync("storage.threshold_config_invalid", detail: new
            {
                warnPercent,
                criticalPercent,
                reason = "warn_percent must be >= 1 and < critical_percent, and critical_percent <= 100"
            }, ct: ct);
            return;
        }

        long usedBytes;
        long totalBytes;
        string source;
        int? daysUntilFull;

        if (diskCapacityMb > 0)
        {
            source = "setting";
            totalBytes = diskCapacityMb * 1024L * 1024L;
            var summary = await statsService.SummaryAsync(totalBytes, ct);
            usedBytes = summary.TotalBytes;
            daysUntilFull = summary.DaysUntilFull;
        }
        else
        {
            source = "disk";
            var mediaRoot = configuration?["Storage:MediaRoot"] ?? "/var/lib/customsync/media";
            var probe = diskProbe.Probe(mediaRoot);
            if (probe == null)
            {
                await audit.WriteAsync("storage.threshold_unknown", detail: new { path = mediaRoot }, ct: ct);
                return;
            }
            totalBytes = probe.Value.TotalBytes;
            usedBytes = probe.Value.TotalBytes - probe.Value.FreeBytes;
            var summary = await statsService.SummaryAsync(totalBytes, ct);
            daysUntilFull = summary.DaysUntilFull;
        }

        double percent = totalBytes > 0 ? (double)usedBytes / totalBytes * 100.0 : 0.0;
        var roundedPercent = Math.Round(percent, 2);

        if (percent >= criticalPercent)
        {
            await audit.WriteAsync("storage.threshold_critical", detail: new
            {
                percent = roundedPercent,
                used = usedBytes,
                capacity = totalBytes,
                source,
                daysUntilFull
            }, ct: ct);
        }
        else if (percent >= warnPercent)
        {
            await audit.WriteAsync("storage.threshold_warning", detail: new
            {
                percent = roundedPercent,
                used = usedBytes,
                capacity = totalBytes,
                source,
                daysUntilFull
            }, ct: ct);
        }
    }

    public virtual async Task<ArchiveJobReport> RunOnceAsync(DateTime nowUtc, CancellationToken ct = default)
    {
        // 1. Chegaralar tekshiruvi doim (jobs_enabled o'chiq bo'lsa ham) ishlaydi
        await CheckThresholdsAsync(ct);

        var runDate = DateOnly.FromDateTime(nowUtc);
        var startedAt = nowUtc;
        logger?.LogInformation("Archive job started for date {RunDate}", runDate);

        // 2. Kunlik qatorni atomar band qilish (atomic day claim).
        // Xotiradagi "oxirgi run" bayrog'i restartdan keyin ishni qayta
        // yurgizardi; qo'lda target bilan bu har safar yangi arxiv degani.
        using var claimScope = scopeFactory.CreateScope();
        var db = claimScope.ServiceProvider.GetRequiredService<SyncDbContext>();
        var connection = (NpgsqlConnection)db.Database.GetDbConnection();
        if (connection.State != System.Data.ConnectionState.Open)
        {
            await connection.OpenAsync(ct);
        }

        int affectedRows;
        var claimSql = "INSERT INTO archive_job_runs (run_date, started_at, status) VALUES (@runDate, @startedAt, 'running') ON CONFLICT (run_date) DO NOTHING;";

        await using (var cmd = new NpgsqlCommand(claimSql, connection))
        {
            cmd.Parameters.AddWithValue("runDate", runDate);
            cmd.Parameters.AddWithValue("startedAt", startedAt);
            affectedRows = await cmd.ExecuteNonQueryAsync(ct);
        }

        if (affectedRows == 0)
        {
            // Boshqa instance bugungi kunni allaqachon olgan
            return ArchiveJobReport.Skipped(runDate, "already_claimed");
        }

        // 3. jobs_enabled sozlamasini tekshirish
        var settingsService = claimScope.ServiceProvider.GetRequiredService<SettingsService>();
        var claimAudit = claimScope.ServiceProvider.GetRequiredService<AuditService>();
        var jobsEnabled = await ReadBoolAsync(settingsService, claimAudit, "storage.jobs_enabled", false, ct);

        if (!jobsEnabled)
        {
            var updateSkippedSql = "UPDATE archive_job_runs SET finished_at = @finishedAt, status = @status, summary = @summary WHERE run_date = @runDate;";
            await using (var cmd = new NpgsqlCommand(updateSkippedSql, connection))
            {
                cmd.Parameters.AddWithValue("finishedAt", DateTime.UtcNow);
                cmd.Parameters.AddWithValue("status", ArchiveJobRunStatus.Skipped);
                cmd.Parameters.AddWithValue("summary", "jobs_disabled");
                cmd.Parameters.AddWithValue("runDate", runDate);
                await cmd.ExecuteNonQueryAsync(ct);
            }

            return ArchiveJobReport.Skipped(runDate, "jobs_disabled");
        }

        // Staging hisoboti (§5)
        var targets = claimScope.ServiceProvider.GetServices<IArchiveTarget>();
        foreach (var target in targets)
        {
            if (target is ManualDownloadTarget manual)
            {
                var staged = await manual.ListStagedAsync(ct);
                var count = staged.Count;
                var totalBytes = staged.Sum(s => s.Size);
                var audit = claimScope.ServiceProvider.GetRequiredService<AuditService>();
                await audit.WriteAsync("archive_job.staging_report", detail: new { count, totalBytes }, ct: ct);
            }
        }

        var jobsDryRun = await ReadBoolAsync(settingsService, claimAudit, "storage.jobs_dry_run", true, ct);

        var maxBatches = await ReadIntAsync(settingsService, claimAudit, "storage.jobs_max_batches", 10, ct);
        if (maxBatches < 1) maxBatches = 1;

        var minDays = await ReadIntAsync(settingsService, claimAudit, "retention.min_days", 30, ct);
        if (minDays <= 0) minDays = 30;

        // Deterministik siyosatlar ro'yxati (§3.3)
        var storedPolicies = await db.RetentionPolicies.AsNoTracking()
            .Where(p => p.Enabled)
            .ToListAsync(ct);

        storedPolicies = storedPolicies
            .OrderByDescending(p => p.Priority)
            .ThenBy(p => p.PolicyId, StringComparer.Ordinal)
            .ToList();

        var implicitPolicies = new List<RetentionPolicyEntity>();
        foreach (var kind in RecordKind.All)
        {
            var key = $"retention.{kind}_days";
            var days = await ReadIntAsync(settingsService, claimAudit, key, 0, ct);

            if (days > 0)
            {
                implicitPolicies.Add(new RetentionPolicyEntity
                {
                    PolicyId = key,
                    Name = $"Implicit {kind} retention",
                    Enabled = true,
                    Kind = kind,
                    PeerHash = null,
                    OlderThanDays = days,
                    Action = RetentionActions.DeleteOnly,
                    TargetId = null,
                    Priority = 0,
                    UpdatedAt = DateTime.UtcNow
                });
            }
        }

        var allPolicies = storedPolicies.Concat(implicitPolicies).ToList();

        int policiesRun = 0;
        int policiesSkipped = 0;
        int policiesFailed = 0;
        int totalDeletedRecords = 0;
        long totalFreedBytes = 0;

        // 4. Har bir siyosat uchun yangi toza scope (failure isolation)
        foreach (var policy in allPolicies)
        {
            ct.ThrowIfCancellationRequested();

            if (string.Equals(policy.Action, RetentionActions.NeverDelete, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var validationError = RetentionPolicy.GetValidationError(policy, minDays);
            if (validationError != null)
            {
                policiesSkipped++;
                using var skipScope = scopeFactory.CreateScope();
                var audit = skipScope.ServiceProvider.GetRequiredService<AuditService>();
                await audit.WriteAsync("archive_job.policy_skipped", detail: new
                {
                    policyId = policy.PolicyId,
                    reason = validationError
                }, ct: ct);
                continue;
            }

            try
            {
                using var policyScope = scopeFactory.CreateScope();
                var policyDb = policyScope.ServiceProvider.GetRequiredService<SyncDbContext>();
                var purgeService = policyScope.ServiceProvider.GetRequiredService<PurgeService>();
                var audit = policyScope.ServiceProvider.GetRequiredService<AuditService>();
                var availableTargets = policyScope.ServiceProvider.GetServices<IArchiveTarget>();

                // Confirmation guard
                if (!string.IsNullOrEmpty(policy.TargetId))
                {
                    var target = availableTargets.FirstOrDefault(t => t.TargetId == policy.TargetId);
                    if (target?.RequiresExplicitConfirmation == true)
                    {
                        var hasPending = await policyDb.ArchiveRuns.AsNoTracking()
                            .AnyAsync(r => r.PolicyId == policy.PolicyId && r.Status == ArchiveRunStatus.AwaitingConfirmation, ct);
                        if (hasPending)
                        {
                            policiesSkipped++;
                            await audit.WriteAsync("archive_job.policy_skipped", detail: new
                            {
                                policyId = policy.PolicyId,
                                reason = "pending_confirmation"
                            }, ct: ct);
                            continue;
                        }
                    }
                }

                var preview = await purgeService.PreviewAsync(policy.PolicyId, BatchLimit, ct);
                if (preview.MatchedCount == 0)
                {
                    await audit.WriteAsync("archive_job.policy_skipped", detail: new
                    {
                        policyId = policy.PolicyId,
                        reason = "matched_zero"
                    }, ct: ct);
                    continue;
                }

                if (jobsDryRun)
                {
                    policiesRun++;
                    await audit.WriteAsync("archive_job.dry_run", detail: new
                    {
                        policyId = policy.PolicyId,
                        matched = preview.MatchedCount,
                        matchedBytes = preview.MatchedBytes,
                        estimatedFreedBytes = preview.EstimatedFreedBytes,
                        mediaCount = preview.MediaCount,
                        at_least = (preview.MatchedCount >= BatchLimit)
                    }, ct: ct);
                    continue;
                }

                policiesRun++;
                int policyBatches = 0;
                int policyDeleted = 0;
                long policyFreed = 0;
                bool lastSuccess = true;

                while (policyBatches < maxBatches)
                {
                    var result = await purgeService.ExecuteAsync(policy.PolicyId, target: null, limit: BatchLimit, ct: ct);
                    if (ct.IsCancellationRequested)
                    {
                        throw new OperationCanceledException(ct);
                    }

                    policyBatches++;
                    policyDeleted += result.DeletedCount;
                    policyFreed += result.FreedBytes;
                    lastSuccess = result.Success;

                    await audit.WriteAsync("archive_job.policy_result", detail: new
                    {
                        policyId = policy.PolicyId,
                        status = result.Status,
                        matched = result.MatchedCount,
                        deleted = result.DeletedCount,
                        freed = result.FreedBytes,
                        runId = result.RunId
                    }, ct: ct);

                    if (!result.Success)
                    {
                        await audit.WriteAsync("archive_job.policy_failed", detail: new
                        {
                            policyId = policy.PolicyId,
                            exception = result.Status,
                            message = result.Error ?? "Policy execution failed"
                        }, ct: ct);
                        break;
                    }

                    if (result.Status != ArchiveRunStatus.Completed || result.DeletedCount == 0 || result.MatchedCount < BatchLimit)
                    {
                        break;
                    }
                }

                if (!lastSuccess)
                {
                    policiesFailed++;
                }

                totalDeletedRecords += policyDeleted;
                totalFreedBytes += policyFreed;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                policiesFailed++;
                using var errScope = scopeFactory.CreateScope();
                var audit = errScope.ServiceProvider.GetRequiredService<AuditService>();
                await audit.WriteAsync("archive_job.policy_failed", detail: new
                {
                    policyId = policy.PolicyId,
                    exception = ex.GetType().Name,
                    message = ex.Message
                }, ct: CancellationToken.None);
            }
        }

        // 5. Yetim media tozalash (dry-run emasda)
        if (!jobsDryRun)
        {
            using var sweepScope = scopeFactory.CreateScope();
            var purgeService = sweepScope.ServiceProvider.GetRequiredService<PurgeService>();
            await purgeService.SweepOrphanedMediaAsync(nowUtc, ct);
        }

        // 6. Kunlik qatorni yakunlash
        var finalStatus = policiesFailed > 0 ? ArchiveJobRunStatus.Failed : ArchiveJobRunStatus.Completed;
        var summaryObj = new
        {
            policiesRun,
            policiesSkipped,
            policiesFailed,
            totalDeletedRecords,
            totalFreedBytes,
            dryRun = jobsDryRun
        };
        var summaryJson = JsonSerializer.Serialize(summaryObj);

        using var finishScope = scopeFactory.CreateScope();
        var finishDb = finishScope.ServiceProvider.GetRequiredService<SyncDbContext>();
        var finishAudit = finishScope.ServiceProvider.GetRequiredService<AuditService>();

        var dayRow = await finishDb.ArchiveJobRuns.FirstOrDefaultAsync(r => r.RunDate == runDate, ct);
        if (dayRow != null)
        {
            dayRow.FinishedAt = DateTime.UtcNow;
            dayRow.Status = finalStatus;
            dayRow.Summary = summaryJson;
            await finishDb.SaveChangesAsync(ct);
        }

        await finishAudit.WriteAsync("archive_job.finished", detail: new
        {
            runDate,
            status = finalStatus,
            policiesRun,
            policiesSkipped,
            policiesFailed,
            totalDeletedRecords,
            totalFreedBytes
        }, ct: ct);

        return new ArchiveJobReport(
            Status: finalStatus,
            RunDate: runDate,
            PoliciesRun: policiesRun,
            PoliciesSkipped: policiesSkipped,
            PoliciesFailed: policiesFailed,
            TotalDeletedRecords: totalDeletedRecords,
            TotalFreedBytes: totalFreedBytes,
            Message: null);
    }
}
