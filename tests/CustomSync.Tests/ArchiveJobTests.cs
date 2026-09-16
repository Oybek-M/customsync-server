using CustomSync.Core.Contracts;
using CustomSync.Core.Interchange;
using CustomSync.Data;
using CustomSync.Data.Entities;
using CustomSync.Services;
using CustomSync.Services.Storage;
using CustomSync.Services.Storage.Targets;
using CustomSync.Tests.Fixtures;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.Security.Cryptography;
using Xunit;

namespace CustomSync.Tests;

public class ArchiveJobTests : IClassFixture<DatabaseFixture>, IDisposable
{
    private readonly DatabaseFixture _fixture;
    private readonly string _tempDir;
    private readonly string _stagingDir;
    private readonly string _mediaDir;
    private static long _seqCounter = 200_000;

    public ArchiveJobTests(DatabaseFixture fixture)
    {
        _fixture = fixture;
        _tempDir = Path.Combine(Path.GetTempPath(), $"cs-job-test-{Guid.NewGuid():N}");
        _stagingDir = Path.Combine(_tempDir, "staging");
        _mediaDir = Path.Combine(_tempDir, "media");
        Directory.CreateDirectory(_stagingDir);
        Directory.CreateDirectory(_mediaDir);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDir))
            {
                Directory.Delete(_tempDir, recursive: true);
            }
        }
        catch { }
    }

    private static long NextSeq() => Interlocked.Increment(ref _seqCounter);

    private async Task ResetStateAsync()
    {
        await using var db = _fixture.CreateContext();
        await db.RetentionPolicies.ExecuteDeleteAsync();
        await db.ArchiveRuns.ExecuteDeleteAsync();
        await db.ArchiveJobRuns.ExecuteDeleteAsync();

        // Audit ham tozalanadi: aks holda test oldingi testning `dry_run` yoki
        // `threshold_*` yozuvini ko'rib "o'tdi" deb xulosa qiladi (bu yerdagi
        // testlar bitta bazani bo'lishadi).
        await db.AuditLogs.ExecuteDeleteAsync();
    }

    private RecordEntity MakeRecord(
        string peerHash,
        string kind = "activity",
        int daysAgo = 60,
        int payloadSize = 256,
        string deviceId = "dev-test-1",
        long observedAt = 5000)
    {
        var recordId = Guid.NewGuid().ToString("N");
        return new RecordEntity
        {
            RecordId    = recordId,
            Seq         = NextSeq(),
            Kind        = kind,
            AccountHash = "acc_" + Guid.NewGuid().ToString("N")[..8],
            PeerHash    = peerHash,
            MsgId       = Random.Shared.Next(1, 100000),
            OccurredAt  = DateTimeOffset.UtcNow.AddDays(-daysAgo).ToUnixTimeSeconds(),
            ObservedAt  = observedAt,
            DeviceId    = deviceId,
            Nonce       = new byte[12],
            Payload     = new byte[payloadSize],
            PayloadSize = payloadSize,
            ReceivedAt  = DateTime.UtcNow.AddDays(-daysAgo)
        };
    }

    public class FakeDiskProbe : IDiskProbe
    {
        public (long TotalBytes, long FreeBytes)? Result { get; set; }
        public string? LastProbedPath { get; private set; }

        public (long TotalBytes, long FreeBytes)? Probe(string path)
        {
            LastProbedPath = path;
            return Result;
        }
    }

    public class PoisoningTarget(IServiceProvider sp) : IArchiveTarget
    {
        public string TargetId => "poisoning_target";
        public string DisplayName => "Poisoning Target";
        public bool RequiresExplicitConfirmation => false;
        public Task<bool> HealthCheckAsync(CancellationToken ct = default) => Task.FromResult(true);
        public async Task<ArchiveUploadResult> UploadAsync(string archiveName, Stream content, CancellationToken ct = default)
        {
            var db = sp.GetRequiredService<SyncDbContext>();
            var conn = (Npgsql.NpgsqlConnection)db.Database.GetDbConnection();
            if (conn.State != System.Data.ConnectionState.Open)
            {
                await conn.OpenAsync(ct);
            }
            // Leave an open transaction on the connection
            await conn.BeginTransactionAsync(ct);
            throw new InvalidOperationException("Simulated upload failure that leaves transaction open");
        }
        public Task<Stream?> DownloadAsync(string location, CancellationToken ct = default) => Task.FromResult<Stream?>(null);
    }

    public class ThrowingUploadTarget : IArchiveTarget
    {
        public string TargetId => "throwing_upload";
        public string DisplayName => "Throwing Upload Target";
        public bool RequiresExplicitConfirmation => false;
        public Task<bool> HealthCheckAsync(CancellationToken ct = default) => Task.FromResult(true);
        public Task<ArchiveUploadResult> UploadAsync(string archiveName, Stream content, CancellationToken ct = default)
            => throw new HttpRequestException("Simulated fatal target connection error");
        public Task<Stream?> DownloadAsync(string location, CancellationToken ct = default)
            => Task.FromResult<Stream?>(null);
    }

    public class MemoryArchiveTarget : IArchiveTarget
    {
        public readonly Dictionary<string, byte[]> Storage = new();
        public string TargetId => "working_memory";
        public string DisplayName => "Memory Test Target";
        public bool RequiresExplicitConfirmation => false;
        public Task<bool> HealthCheckAsync(CancellationToken ct = default) => Task.FromResult(true);
        public async Task<ArchiveUploadResult> UploadAsync(string archiveName, Stream content, CancellationToken ct = default)
        {
            using var ms = new MemoryStream();
            await content.CopyToAsync(ms, ct);
            var bytes = ms.ToArray();
            Storage[archiveName] = bytes;
            var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
            return new ArchiveUploadResult(true, archiveName, hash, null);
        }
        public Task<Stream?> DownloadAsync(string location, CancellationToken ct = default)
        {
            if (Storage.TryGetValue(location, out var bytes))
            {
                return Task.FromResult<Stream?>(new MemoryStream(bytes));
            }
            return Task.FromResult<Stream?>(null);
        }
    }

    private (IServiceProvider Provider, IServiceScopeFactory ScopeFactory, FakeDiskProbe Probe) BuildServices(
        IDiskProbe? probe = null,
        IEnumerable<IArchiveTarget>? extraTargets = null,
        Func<DateTime>? clock = null,
        Action<IServiceCollection>? configure = null)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Storage:MediaRoot"] = _mediaDir,
            ["Storage:ArchiveStagingRoot"] = _stagingDir
        }).Build());
        services.AddScoped(_ => _fixture.CreateContext());
        services.AddScoped<SettingsService>();
        services.AddScoped<AuditService>();
        services.AddScoped<StatsService>();
        services.AddScoped(sp => new MediaService(sp.GetRequiredService<SyncDbContext>(), _mediaDir));

        var manualTarget = new ManualDownloadTarget(_stagingDir);
        services.AddSingleton(manualTarget);
        services.AddSingleton<IArchiveTarget>(manualTarget);
        services.AddScoped<IArchiveTarget>(sp => new PoisoningTarget(sp));

        if (extraTargets != null)
        {
            foreach (var t in extraTargets)
            {
                services.AddSingleton<IArchiveTarget>(t);
            }
        }

        services.AddScoped(sp => new PurgeService(
            sp.GetRequiredService<SyncDbContext>(),
            sp.GetRequiredService<SettingsService>(),
            sp.GetRequiredService<MediaService>(),
            sp.GetRequiredService<AuditService>(),
            sp.GetServices<IArchiveTarget>(),
            clock));

        var fakeProbe = (probe as FakeDiskProbe) ?? new FakeDiskProbe { Result = (100L * 1024 * 1024 * 1024, 50L * 1024 * 1024 * 1024) };
        services.AddSingleton<IDiskProbe>(probe ?? fakeProbe);
        services.AddScoped<ArchiveJobRunner>();
        configure?.Invoke(services);

        var provider = services.BuildServiceProvider();
        return (provider, provider.GetRequiredService<IServiceScopeFactory>(), fakeProbe);
    }

    // 1. ArchiveSchedule tests
    [Fact]
    public void Test1_ArchiveSchedule_is_due_logic()
    {
        var date = new DateTime(2026, 9, 15, 3, 29, 59, DateTimeKind.Utc);
        var lastRun = new DateOnly(2026, 9, 14);

        // Before 03:30 -> not due
        Assert.False(ArchiveSchedule.IsDue(date, 3, 30, lastRun));

        // At 03:30 -> due
        var dueTime = new DateTime(2026, 9, 15, 3, 30, 0, DateTimeKind.Utc);
        Assert.True(ArchiveSchedule.IsDue(dueTime, 3, 30, lastRun));

        // Ran today -> not due
        var ranToday = new DateOnly(2026, 9, 15);
        Assert.False(ArchiveSchedule.IsDue(dueTime, 3, 30, ranToday));

        // Next day -> due
        var nextDay = new DateTime(2026, 9, 16, 3, 35, 0, DateTimeKind.Utc);
        Assert.True(ArchiveSchedule.IsDue(nextDay, 3, 30, ranToday));

        // First check at 10:00 with no last run -> due
        var lateFirstCheck = new DateTime(2026, 9, 15, 10, 0, 0, DateTimeKind.Utc);
        Assert.True(ArchiveSchedule.IsDue(lateFirstCheck, 3, 30, null));

        // Changed hour picked up
        Assert.False(ArchiveSchedule.IsDue(lateFirstCheck, 11, 0, null));
        Assert.True(ArchiveSchedule.IsDue(lateFirstCheck, 9, 0, null));

        // Invalid hour / minute -> not due
        Assert.False(ArchiveSchedule.IsDue(dueTime, 24, 30, null));
        Assert.False(ArchiveSchedule.IsDue(dueTime, 3, 60, null));
        Assert.False(ArchiveSchedule.IsDue(dueTime, -1, 30, null));
        Assert.False(ArchiveSchedule.IsDue(dueTime, 3, -1, null));
    }

    // 2. Day claim: two RunOnceAsync same day
    [Fact]
    public async Task Test2_Day_claim_skips_second_run_on_same_day()
    {
        await ResetStateAsync();
        var (_, scopeFactory, _) = BuildServices();
        using var scope = scopeFactory.CreateScope();
        var settings = scope.ServiceProvider.GetRequiredService<SettingsService>();
        await settings.EnsureDefaultsAsync();
        await settings.SetAsync("storage.jobs_enabled", "true");
        await settings.SetAsync("storage.jobs_dry_run", "true");

        var now = new DateTime(2026, 9, 15, 4, 0, 0, DateTimeKind.Utc);
        var runner1 = scope.ServiceProvider.GetRequiredService<ArchiveJobRunner>();
        var report1 = await runner1.RunOnceAsync(now);

        using var scope2 = scopeFactory.CreateScope();
        var runner2 = scope2.ServiceProvider.GetRequiredService<ArchiveJobRunner>();
        var report2 = await runner2.RunOnceAsync(now);

        Assert.Equal(ArchiveJobRunStatus.Skipped, report2.Status);

        await using var db = _fixture.CreateContext();
        var runs = await db.ArchiveJobRuns.Where(r => r.RunDate == DateOnly.FromDateTime(now)).ToListAsync();
        Assert.Single(runs);
    }

    // 3. jobs_enabled = false
    [Fact]
    public async Task Test3_Jobs_enabled_false_skips_purge_but_evaluates_thresholds()
    {
        await ResetStateAsync();
        var peer = Guid.NewGuid().ToString("N");
        await using (var db = _fixture.CreateContext())
        {
            db.Records.Add(MakeRecord(peer, daysAgo: 60));
            await db.SaveChangesAsync();
        }

        var probe = new FakeDiskProbe { Result = (100L * 1024 * 1024 * 1024, 15L * 1024 * 1024 * 1024) }; // 85% used -> warn
        var (_, scopeFactory, _) = BuildServices(probe);
        using var scope = scopeFactory.CreateScope();
        var settings = scope.ServiceProvider.GetRequiredService<SettingsService>();
        await settings.EnsureDefaultsAsync();
        await settings.SetAsync("storage.jobs_enabled", "false");
        await settings.SetAsync("storage.warn_percent", "80");
        await settings.SetAsync("storage.critical_percent", "92");

        var now = new DateTime(2026, 9, 16, 4, 0, 0, DateTimeKind.Utc);
        var runner = scope.ServiceProvider.GetRequiredService<ArchiveJobRunner>();
        var report = await runner.RunOnceAsync(now);

        Assert.Equal(ArchiveJobRunStatus.Skipped, report.Status);

        await using (var db = _fixture.CreateContext())
        {
            var recordStillExists = await db.Records.AnyAsync(r => r.PeerHash == peer);
            Assert.True(recordStillExists);

            var archiveRuns = await db.ArchiveRuns.ToListAsync();
            Assert.Empty(archiveRuns);

            var auditLogs = await db.AuditLogs
                .Where(a => a.Action == "storage.threshold_warning")
                .ToListAsync();
            Assert.NotEmpty(auditLogs);
        }
    }

    // 4. Dry-run
    [Fact]
    public async Task Test4_Dry_run_previews_and_audits_without_deleting()
    {
        await ResetStateAsync();
        var peer = Guid.NewGuid().ToString("N");
        var record = MakeRecord(peer, daysAgo: 60);

        var orphanHash = Guid.NewGuid().ToString("N");
        var orphanPath = Path.Combine(_mediaDir, orphanHash[..2], orphanHash);
        Directory.CreateDirectory(Path.GetDirectoryName(orphanPath)!);
        File.WriteAllBytes(orphanPath, [1, 2, 3]);

        var orphanBlob = new MediaBlobEntity
        {
            Hash = orphanHash,
            Size = 3,
            Nonce = new byte[12],
            StoragePath = orphanPath,
            UploadedAt = DateTime.UtcNow.AddDays(-2),
            UploadedByDeviceId = "dev-1",
            OrphanedAt = DateTime.UtcNow.AddHours(-25)
        };

        var policy = new RetentionPolicyEntity
        {
            PolicyId = "pol_" + Guid.NewGuid().ToString("N")[..8],
            Name = "DryRun Test",
            Enabled = true,
            Kind = "activity",
            PeerHash = peer,
            OlderThanDays = 30,
            Action = RetentionActions.DeleteOnly,
            Priority = 10,
            UpdatedAt = DateTime.UtcNow
        };

        await using (var db = _fixture.CreateContext())
        {
            db.Records.Add(record);
            db.MediaBlobs.Add(orphanBlob);
            db.RetentionPolicies.Add(policy);
            await db.SaveChangesAsync();
        }

        var (_, scopeFactory, _) = BuildServices();
        using (var scope = scopeFactory.CreateScope())
        {
            var settings = scope.ServiceProvider.GetRequiredService<SettingsService>();
            await settings.EnsureDefaultsAsync();
            await settings.SetAsync("storage.jobs_enabled", "true");
            await settings.SetAsync("storage.jobs_dry_run", "true");

            var now = new DateTime(2026, 9, 17, 4, 0, 0, DateTimeKind.Utc);
            var runner = scope.ServiceProvider.GetRequiredService<ArchiveJobRunner>();
            var report = await runner.RunOnceAsync(now);
            Assert.Equal(ArchiveJobRunStatus.Completed, report.Status);
        }

        await using (var db = _fixture.CreateContext())
        {
            Assert.True(await db.Records.AnyAsync(r => r.RecordId == record.RecordId));
            Assert.True(await db.MediaBlobs.AnyAsync(b => b.Hash == orphanHash));
            Assert.True(File.Exists(orphanPath));

            var archiveRuns = await db.ArchiveRuns.Where(r => r.PolicyId == policy.PolicyId).ToListAsync();
            Assert.Empty(archiveRuns);

            var audits = await db.AuditLogs.Where(a => a.Action == "archive_job.dry_run").ToListAsync();
            Assert.Contains(audits, a => a.Detail != null && a.Detail.Contains(policy.PolicyId));
        }
    }

    // 5. Real run, delete_only implicit retention.activity_days
    [Fact]
    public async Task Test5_Real_run_delete_only_implicit_activity_retention()
    {
        await ResetStateAsync();
        var peer = Guid.NewGuid().ToString("N");
        var oldActivity = MakeRecord(peer, kind: "activity", daysAgo: 60);
        var youngActivity = MakeRecord(peer, kind: "activity", daysAgo: 10);
        var oldTombstone = MakeRecord(peer, kind: RecordKind.Tombstone, daysAgo: 60);

        await using (var db = _fixture.CreateContext())
        {
            db.Records.AddRange(oldActivity, youngActivity, oldTombstone);
            await db.SaveChangesAsync();
        }

        var (_, scopeFactory, _) = BuildServices();
        using (var scope = scopeFactory.CreateScope())
        {
            var settings = scope.ServiceProvider.GetRequiredService<SettingsService>();
            await settings.EnsureDefaultsAsync();
            await settings.SetAsync("storage.jobs_enabled", "true");
            await settings.SetAsync("storage.jobs_dry_run", "false");
            await settings.SetAsync("retention.activity_days", "30");

            var now = new DateTime(2026, 9, 18, 4, 0, 0, DateTimeKind.Utc);
            var runner = scope.ServiceProvider.GetRequiredService<ArchiveJobRunner>();
            var report = await runner.RunOnceAsync(now);
            Assert.Equal(ArchiveJobRunStatus.Completed, report.Status);
        }

        await using (var db = _fixture.CreateContext())
        {
            Assert.False(await db.Records.AnyAsync(r => r.RecordId == oldActivity.RecordId));
            Assert.True(await db.Records.AnyAsync(r => r.RecordId == youngActivity.RecordId));
            Assert.True(await db.Records.AnyAsync(r => r.RecordId == oldTombstone.RecordId));
        }
    }

    // 6. never_delete protecting a record
    [Fact]
    public async Task Test6_Never_delete_protecting_a_record()
    {
        await ResetStateAsync();
        var peer = Guid.NewGuid().ToString("N");
        var record = MakeRecord(peer, kind: "activity", daysAgo: 60);

        var protectPolicy = new RetentionPolicyEntity
        {
            PolicyId = "pol_protect_" + Guid.NewGuid().ToString("N")[..6],
            Name = "Protect Policy",
            Enabled = true,
            PeerHash = peer,
            Action = RetentionActions.NeverDelete,
            Priority = 100,
            UpdatedAt = DateTime.UtcNow
        };

        await using (var db = _fixture.CreateContext())
        {
            db.Records.Add(record);
            db.RetentionPolicies.Add(protectPolicy);
            await db.SaveChangesAsync();
        }

        var (_, scopeFactory, _) = BuildServices();
        using (var scope = scopeFactory.CreateScope())
        {
            var settings = scope.ServiceProvider.GetRequiredService<SettingsService>();
            await settings.EnsureDefaultsAsync();
            await settings.SetAsync("storage.jobs_enabled", "true");
            await settings.SetAsync("storage.jobs_dry_run", "false");
            await settings.SetAsync("retention.activity_days", "30");

            var now = new DateTime(2026, 9, 19, 4, 0, 0, DateTimeKind.Utc);
            var runner = scope.ServiceProvider.GetRequiredService<ArchiveJobRunner>();
            await runner.RunOnceAsync(now);
        }

        await using (var db = _fixture.CreateContext())
        {
            Assert.True(await db.Records.AnyAsync(r => r.RecordId == record.RecordId));
        }
    }

    // 7. Isolation: policy A throws in UploadAsync, policy B still deletes
    [Fact]
    public async Task Test7_Isolation_target_exception_does_not_abort_subsequent_policies()
    {
        await ResetStateAsync();
        var peerA = Guid.NewGuid().ToString("N");
        var peerB = Guid.NewGuid().ToString("N");
        var recordA = MakeRecord(peerA, kind: "activity", daysAgo: 60);
        var recordB = MakeRecord(peerB, kind: "activity", daysAgo: 60);

        var throwingTarget = new ThrowingUploadTarget();

        var policyA = new RetentionPolicyEntity
        {
            PolicyId = "pol_A_" + Guid.NewGuid().ToString("N")[..6],
            Name = "Throwing Policy A",
            Enabled = true,
            PeerHash = peerA,
            OlderThanDays = 30,
            Action = RetentionActions.ArchiveThenDelete,
            TargetId = throwingTarget.TargetId,
            Priority = 50,
            UpdatedAt = DateTime.UtcNow
        };

        var policyB = new RetentionPolicyEntity
        {
            PolicyId = "pol_B_" + Guid.NewGuid().ToString("N")[..6],
            Name = "Working Policy B",
            Enabled = true,
            PeerHash = peerB,
            OlderThanDays = 30,
            Action = RetentionActions.DeleteOnly,
            Priority = 40,
            UpdatedAt = DateTime.UtcNow
        };

        await using (var db = _fixture.CreateContext())
        {
            db.Records.AddRange(recordA, recordB);
            db.RetentionPolicies.AddRange(policyA, policyB);
            await db.SaveChangesAsync();
        }

        var (_, scopeFactory, _) = BuildServices(extraTargets: [throwingTarget]);
        using (var scope = scopeFactory.CreateScope())
        {
            var settings = scope.ServiceProvider.GetRequiredService<SettingsService>();
            await settings.EnsureDefaultsAsync();
            await settings.SetAsync("storage.jobs_enabled", "true");
            await settings.SetAsync("storage.jobs_dry_run", "false");

            var now = new DateTime(2026, 9, 20, 4, 0, 0, DateTimeKind.Utc);
            var runner = scope.ServiceProvider.GetRequiredService<ArchiveJobRunner>();
            var report = await runner.RunOnceAsync(now);

            Assert.Equal(ArchiveJobRunStatus.Failed, report.Status);
        }

        await using (var db = _fixture.CreateContext())
        {
            // Record A was not deleted
            Assert.True(await db.Records.AnyAsync(r => r.RecordId == recordA.RecordId));
            // Record B WAS deleted
            Assert.False(await db.Records.AnyAsync(r => r.RecordId == recordB.RecordId));

            var failedAudits = await db.AuditLogs
                .Where(a => a.Action == "archive_job.policy_failed")
                .ToListAsync();
            Assert.Contains(failedAudits, a => a.Detail != null && a.Detail.Contains(policyA.PolicyId));
        }
    }

    // 8. Poisoned context: DB failure in policy A does not corrupt fresh scope of policy B
    [Fact]
    public async Task Test8_Poisoned_context_database_failure_does_not_prevent_next_policy()
    {
        await ResetStateAsync();
        var peerA = Guid.NewGuid().ToString("N");
        var peerB = Guid.NewGuid().ToString("N");
        var recordA = MakeRecord(peerA, kind: "activity", daysAgo: 60);
        var recordB = MakeRecord(peerB, kind: "activity", daysAgo: 60);

        // Policy A uses PoisoningTarget which corrupts context before failing
        var policyA = new RetentionPolicyEntity
        {
            PolicyId = "pol_fail_" + Guid.NewGuid().ToString("N")[..6],
            Name = "Failing DB Policy",
            Enabled = true,
            PeerHash = peerA,
            OlderThanDays = 30,
            Action = RetentionActions.ArchiveThenDelete,
            TargetId = "poisoning_target",
            Priority = 60,
            UpdatedAt = DateTime.UtcNow
        };

        var policyB = new RetentionPolicyEntity
        {
            PolicyId = "pol_succ_" + Guid.NewGuid().ToString("N")[..6],
            Name = "Succeeding Policy",
            Enabled = true,
            PeerHash = peerB,
            OlderThanDays = 30,
            Action = RetentionActions.DeleteOnly,
            Priority = 50,
            UpdatedAt = DateTime.UtcNow
        };

        await using (var db = _fixture.CreateContext())
        {
            db.Records.AddRange(recordA, recordB);
            db.RetentionPolicies.AddRange(policyA, policyB);
            await db.SaveChangesAsync();
        }

        var (_, scopeFactory, _) = BuildServices();
        using (var scope = scopeFactory.CreateScope())
        {
            var settings = scope.ServiceProvider.GetRequiredService<SettingsService>();
            await settings.EnsureDefaultsAsync();
            await settings.SetAsync("storage.jobs_enabled", "true");
            await settings.SetAsync("storage.jobs_dry_run", "false");

            var now = new DateTime(2026, 9, 21, 4, 0, 0, DateTimeKind.Utc);
            var runner = scope.ServiceProvider.GetRequiredService<ArchiveJobRunner>();
            var report = await runner.RunOnceAsync(now);
        }

        await using (var db = _fixture.CreateContext())
        {
            Assert.True(await db.Records.AnyAsync(r => r.RecordId == recordA.RecordId));
            Assert.False(await db.Records.AnyAsync(r => r.RecordId == recordB.RecordId));
        }
    }

    // 9. Manual target over two days -> exactly one awaiting_confirmation, skip on day 2
    [Fact]
    public async Task Test9_Manual_target_over_two_days_stages_once_and_skips_day_two()
    {
        await ResetStateAsync();
        var peer = Guid.NewGuid().ToString("N");
        var record = MakeRecord(peer, kind: "activity", daysAgo: 60);

        var policy = new RetentionPolicyEntity
        {
            PolicyId = "pol_manual_" + Guid.NewGuid().ToString("N")[..6],
            Name = "Manual Policy",
            Enabled = true,
            PeerHash = peer,
            OlderThanDays = 30,
            Action = RetentionActions.ArchiveThenDelete,
            TargetId = "manual",
            Priority = 50,
            UpdatedAt = DateTime.UtcNow
        };

        await using (var db = _fixture.CreateContext())
        {
            db.Records.Add(record);
            db.RetentionPolicies.Add(policy);
            await db.SaveChangesAsync();
        }

        var (_, scopeFactory, _) = BuildServices();
        using (var scope = scopeFactory.CreateScope())
        {
            var settings = scope.ServiceProvider.GetRequiredService<SettingsService>();
            await settings.EnsureDefaultsAsync();
            await settings.SetAsync("storage.jobs_enabled", "true");
            await settings.SetAsync("storage.jobs_dry_run", "false");

            // Day 1
            var day1 = new DateTime(2026, 9, 22, 4, 0, 0, DateTimeKind.Utc);
            var runner1 = scope.ServiceProvider.GetRequiredService<ArchiveJobRunner>();
            await runner1.RunOnceAsync(day1);

            // Day 2
            var day2 = new DateTime(2026, 9, 23, 4, 0, 0, DateTimeKind.Utc);
            var runner2 = scope.ServiceProvider.GetRequiredService<ArchiveJobRunner>();
            await runner2.RunOnceAsync(day2);
        }

        await using (var db = _fixture.CreateContext())
        {
            // Exactly one awaiting_confirmation run
            var runs = await db.ArchiveRuns.Where(r => r.PolicyId == policy.PolicyId).ToListAsync();
            Assert.Single(runs);
            Assert.Equal(ArchiveRunStatus.AwaitingConfirmation, runs[0].Status);

            // Record still exists (never deleted before human confirms)
            Assert.True(await db.Records.AnyAsync(r => r.RecordId == record.RecordId));

            // Day 2 skipped with reason pending_confirmation
            var skips = await db.AuditLogs
                .Where(a => a.Action == "archive_job.policy_skipped" && a.Detail != null && a.Detail.Contains("pending_confirmation"))
                .ToListAsync();
            Assert.NotEmpty(skips);
        }

        // Exactly one staged file
        var stagedFiles = Directory.GetFiles(_stagingDir, "*.cmx");
        Assert.Single(stagedFiles);
    }

    // 10. Batching: backlog > limit -> multiple rounds capped by jobs_max_batches
    [Fact]
    public async Task Test10_Batching_caps_backlog_execution_at_max_batches()
    {
        await ResetStateAsync();
        var peer = Guid.NewGuid().ToString("N");
        var records = Enumerable.Range(0, 15).Select(_ => MakeRecord(peer, kind: "activity", daysAgo: 60)).ToList();

        var policy = new RetentionPolicyEntity
        {
            PolicyId = "pol_batch_" + Guid.NewGuid().ToString("N")[..6],
            Name = "Batch Policy",
            Enabled = true,
            PeerHash = peer,
            OlderThanDays = 30,
            Action = RetentionActions.DeleteOnly,
            Priority = 50,
            UpdatedAt = DateTime.UtcNow
        };

        await using (var db = _fixture.CreateContext())
        {
            db.Records.AddRange(records);
            db.RetentionPolicies.Add(policy);
            await db.SaveChangesAsync();
        }

        var (_, scopeFactory, _) = BuildServices();
        using (var scope = scopeFactory.CreateScope())
        {
            var settings = scope.ServiceProvider.GetRequiredService<SettingsService>();
            await settings.EnsureDefaultsAsync();
            await settings.SetAsync("storage.jobs_enabled", "true");
            await settings.SetAsync("storage.jobs_dry_run", "false");
            await settings.SetAsync("storage.jobs_max_batches", "2");

            var now = new DateTime(2026, 9, 24, 4, 0, 0, DateTimeKind.Utc);
            var runner = scope.ServiceProvider.GetRequiredService<ArchiveJobRunner>();
            runner.BatchLimit = 5;
            await runner.RunOnceAsync(now);
        }

        await using (var db = _fixture.CreateContext())
        {
            var runs = await db.ArchiveRuns.Where(r => r.PolicyId == policy.PolicyId).ToListAsync();

            // AYNAN ikkita partiya: pastki chegara sikl haqiqatan takrorlanishini
            // ushlaydi (`<= 2` sikl butunlay yo'q bo'lsa ham o'tib ketardi),
            // yuqori chegara esa jobs_max_batches hurmat qilinishini ushlaydi.
            Assert.Equal(2, runs.Count);

            // 15 yozuv, partiya 5, ikki partiya -> 10 o'chdi, 5 qoldi.
            Assert.Equal(5, await db.Records.CountAsync(r => r.PeerHash == peer));
        }
    }

    public class CancellingUploadTarget(CancellationTokenSource cts) : IArchiveTarget
    {
        public string TargetId => "cancelling_upload";
        public string DisplayName => "Cancelling Target";
        public bool RequiresExplicitConfirmation => false;
        public Task<bool> HealthCheckAsync(CancellationToken ct = default) => Task.FromResult(true);
        public Task<ArchiveUploadResult> UploadAsync(string archiveName, Stream content, CancellationToken ct = default)
        {
            cts.Cancel();
            throw new OperationCanceledException(cts.Token);
        }
        public Task<Stream?> DownloadAsync(string location, CancellationToken ct = default) => Task.FromResult<Stream?>(null);
    }

    // 11. Cancellation during policy loop
    [Fact]
    public async Task Test11_Cancellation_token_aborts_policy_loop_and_propagates()
    {
        await ResetStateAsync();
        var peerA = Guid.NewGuid().ToString("N");
        var peerB = Guid.NewGuid().ToString("N");
        var recordA = MakeRecord(peerA, kind: "activity", daysAgo: 60);
        var recordB = MakeRecord(peerB, kind: "activity", daysAgo: 60);

        using var cts = new CancellationTokenSource();
        var cancellingTarget = new CancellingUploadTarget(cts);

        var policyA = new RetentionPolicyEntity
        {
            PolicyId = "pol_cancel_A_" + Guid.NewGuid().ToString("N")[..6],
            Name = "Cancelling Policy A",
            Enabled = true,
            PeerHash = peerA,
            OlderThanDays = 30,
            Action = RetentionActions.ArchiveThenDelete,
            TargetId = cancellingTarget.TargetId,
            Priority = 60,
            UpdatedAt = DateTime.UtcNow
        };

        var policyB = new RetentionPolicyEntity
        {
            PolicyId = "pol_cancel_B_" + Guid.NewGuid().ToString("N")[..6],
            Name = "Policy B",
            Enabled = true,
            PeerHash = peerB,
            OlderThanDays = 30,
            Action = RetentionActions.DeleteOnly,
            Priority = 40,
            UpdatedAt = DateTime.UtcNow
        };

        await using (var db = _fixture.CreateContext())
        {
            db.Records.AddRange(recordA, recordB);
            db.RetentionPolicies.AddRange(policyA, policyB);
            await db.SaveChangesAsync();
        }

        var (_, scopeFactory, _) = BuildServices(extraTargets: [cancellingTarget]);
        using var scope = scopeFactory.CreateScope();
        var settings = scope.ServiceProvider.GetRequiredService<SettingsService>();
        await settings.EnsureDefaultsAsync();
        await settings.SetAsync("storage.jobs_enabled", "true");
        await settings.SetAsync("storage.jobs_dry_run", "false");

        var now = new DateTime(2026, 9, 25, 4, 0, 0, DateTimeKind.Utc);
        var runner = scope.ServiceProvider.GetRequiredService<ArchiveJobRunner>();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await runner.RunOnceAsync(now, cts.Token);
        });

        // Later policy B was not run
        await using (var db = _fixture.CreateContext())
        {
            Assert.True(await db.Records.AnyAsync(r => r.RecordId == recordB.RecordId));

            // Cancellation must not be logged as policy_failed
            var failedAudits = await db.AuditLogs
                .Where(a => a.Action == "archive_job.policy_failed" && a.Detail != null && (a.Detail.Contains(policyA.PolicyId) || a.Detail.Contains(policyB.PolicyId)))
                .ToListAsync();
            Assert.Empty(failedAudits);
        }
    }

    // 12. Thresholds with fake probe
    [Fact]
    public async Task Test12_Thresholds_evaluation_and_audit()
    {
        await ResetStateAsync();
        // 85% used -> warning
        var probe = new FakeDiskProbe { Result = (100L * 1024 * 1024 * 1024, 15L * 1024 * 1024 * 1024) };
        var (_, scopeFactory, _) = BuildServices(probe);

        using (var scope = scopeFactory.CreateScope())
        {
            var settings = scope.ServiceProvider.GetRequiredService<SettingsService>();
            await settings.EnsureDefaultsAsync();
            await settings.SetAsync("storage.jobs_enabled", "false");
            await settings.SetAsync("storage.disk_capacity_mb", "0");
            await settings.SetAsync("storage.warn_percent", "80");
            await settings.SetAsync("storage.critical_percent", "92");

            var runner = scope.ServiceProvider.GetRequiredService<ArchiveJobRunner>();
            await runner.RunOnceAsync(new DateTime(2026, 9, 26, 4, 0, 0, DateTimeKind.Utc));
        }

        await using (var db = _fixture.CreateContext())
        {
            Assert.True(await db.AuditLogs.AnyAsync(a => a.Action == "storage.threshold_warning"));
            Assert.False(await db.AuditLogs.AnyAsync(a => a.Action == "storage.threshold_critical"));
        }

        // 95% used -> critical ONLY
        probe.Result = (100L * 1024 * 1024 * 1024, 5L * 1024 * 1024 * 1024);
        using (var scope = scopeFactory.CreateScope())
        {
            var runner = scope.ServiceProvider.GetRequiredService<ArchiveJobRunner>();
            await runner.RunOnceAsync(new DateTime(2026, 9, 27, 4, 0, 0, DateTimeKind.Utc));
        }

        await using (var db = _fixture.CreateContext())
        {
            Assert.True(await db.AuditLogs.AnyAsync(a => a.Action == "storage.threshold_critical"));
        }

        // probe null -> threshold_unknown
        probe.Result = null;
        using (var scope = scopeFactory.CreateScope())
        {
            var runner = scope.ServiceProvider.GetRequiredService<ArchiveJobRunner>();
            await runner.RunOnceAsync(new DateTime(2026, 9, 28, 4, 0, 0, DateTimeKind.Utc));
        }

        await using (var db = _fixture.CreateContext())
        {
            Assert.True(await db.AuditLogs.AnyAsync(a => a.Action == "storage.threshold_unknown"));
        }

        // warn >= critical -> config error, no throw
        using (var scope = scopeFactory.CreateScope())
        {
            var settings = scope.ServiceProvider.GetRequiredService<SettingsService>();
            await settings.SetAsync("storage.warn_percent", "95");
            await settings.SetAsync("storage.critical_percent", "80");

            var runner = scope.ServiceProvider.GetRequiredService<ArchiveJobRunner>();
            // Must not throw
            await runner.RunOnceAsync(new DateTime(2026, 9, 29, 4, 0, 0, DateTimeKind.Utc));
        }

        await using (var db = _fixture.CreateContext())
        {
            Assert.True(await db.AuditLogs.AnyAsync(a => a.Action == "storage.threshold_config_invalid"));
        }
    }

    // 13. Settings re-read between two days
    [Fact]
    public async Task Test13_Settings_re_read_picks_up_changes_between_days()
    {
        await ResetStateAsync();
        var peer = Guid.NewGuid().ToString("N");
        var record = MakeRecord(peer, kind: "activity", daysAgo: 60);

        var policy = new RetentionPolicyEntity
        {
            PolicyId = "pol_reread_" + Guid.NewGuid().ToString("N")[..6],
            Name = "ReRead Policy",
            Enabled = true,
            PeerHash = peer,
            OlderThanDays = 30,
            Action = RetentionActions.DeleteOnly,
            Priority = 50,
            UpdatedAt = DateTime.UtcNow
        };

        await using (var db = _fixture.CreateContext())
        {
            db.Records.Add(record);
            db.RetentionPolicies.Add(policy);
            await db.SaveChangesAsync();
        }

        var (_, scopeFactory, _) = BuildServices();
        using (var scope = scopeFactory.CreateScope())
        {
            var settings = scope.ServiceProvider.GetRequiredService<SettingsService>();
            await settings.EnsureDefaultsAsync();
            await settings.SetAsync("storage.jobs_enabled", "true");
            await settings.SetAsync("storage.jobs_dry_run", "true");

            var runner = scope.ServiceProvider.GetRequiredService<ArchiveJobRunner>();

            // Day 1: dry run -> not deleted
            var day1 = new DateTime(2026, 9, 29, 4, 0, 0, DateTimeKind.Utc);
            await runner.RunOnceAsync(day1);

            await using (var db = _fixture.CreateContext())
            {
                Assert.True(await db.Records.AnyAsync(r => r.RecordId == record.RecordId));
            }

            // Change setting
            await settings.SetAsync("storage.jobs_dry_run", "false");

            // Day 2: real run -> deleted
            var day2 = new DateTime(2026, 9, 30, 4, 0, 0, DateTimeKind.Utc);
            await runner.RunOnceAsync(day2);

            await using (var db = _fixture.CreateContext())
            {
                Assert.False(await db.Records.AnyAsync(r => r.RecordId == record.RecordId));
            }
        }
    }

    // 14. Sozlamada buzuq qiymat: job to'xtamaydi, standart qiymatga qaytadi va
    // buni audit'ga yozadi. Sozlama qiymatlari admin endpoint'i orqali HAR QANDAY
    // matn bo'lishi mumkin (tip tekshiruvi yo'q), shuning uchun `int.Parse`
    // butun fon xizmatini har daqiqada yiqitardi va retention jimgina to'xtardi.
    [Fact]
    public async Task Test14_Invalid_setting_value_falls_back_and_audits_instead_of_throwing()
    {
        await ResetStateAsync();
        var peer = Guid.NewGuid().ToString("N");
        await using (var db = _fixture.CreateContext())
        {
            db.Records.Add(MakeRecord(peer, kind: "activity", daysAgo: 60));
            await db.SaveChangesAsync();
        }

        var (_, scopeFactory, _) = BuildServices();
        using var scope = scopeFactory.CreateScope();
        var settings = scope.ServiceProvider.GetRequiredService<SettingsService>();
        await settings.EnsureDefaultsAsync();
        await settings.SetAsync("storage.jobs_enabled", "true");
        await settings.SetAsync("storage.jobs_dry_run", "false");
        await settings.SetAsync("storage.warn_percent", "eighty");
        await settings.SetAsync("storage.jobs_max_batches", "ko'p");
        await settings.SetAsync("retention.activity_days", "30");

        var runner = scope.ServiceProvider.GetRequiredService<ArchiveJobRunner>();
        var report = await runner.RunOnceAsync(new DateTime(2026, 10, 1, 4, 0, 0, DateTimeKind.Utc));

        Assert.Equal(ArchiveJobRunStatus.Completed, report.Status);

        await using (var db = _fixture.CreateContext())
        {
            var invalid = await db.AuditLogs
                .Where(a => a.Action == "archive_job.config_invalid")
                .ToListAsync();
            Assert.NotEmpty(invalid);
            Assert.Contains(invalid, a => a.Detail != null && a.Detail.Contains("storage.warn_percent"));
            Assert.Contains(invalid, a => a.Detail != null && a.Detail.Contains("storage.jobs_max_batches"));

            // Buzuq sozlama tozalashni to'xtatmasligi kerak.
            Assert.False(await db.Records.AnyAsync(r => r.PeerHash == peer));
        }
    }

    // 15. Chegaralar AYNAN chegara qiymatida. 85/95 bilan sinash `>=` ni `>` ga
    // almashtirishni sezmaydi.
    [Fact]
    public async Task Test15_Thresholds_fire_exactly_at_the_configured_percent()
    {
        await ResetStateAsync();
        const long total = 100L * 1024 * 1024 * 1024;
        var probe = new FakeDiskProbe { Result = (total, total / 5) }; // aynan 80%
        var (_, scopeFactory, _) = BuildServices(probe);

        using var scope = scopeFactory.CreateScope();
        var settings = scope.ServiceProvider.GetRequiredService<SettingsService>();
        await settings.EnsureDefaultsAsync();
        await settings.SetAsync("storage.jobs_enabled", "false");
        await settings.SetAsync("storage.disk_capacity_mb", "0");
        await settings.SetAsync("storage.warn_percent", "80");
        await settings.SetAsync("storage.critical_percent", "92");

        var runner = scope.ServiceProvider.GetRequiredService<ArchiveJobRunner>();
        await runner.RunOnceAsync(new DateTime(2026, 10, 2, 4, 0, 0, DateTimeKind.Utc));

        await using (var db = _fixture.CreateContext())
        {
            Assert.True(await db.AuditLogs.AnyAsync(a => a.Action == "storage.threshold_warning"));
            Assert.False(await db.AuditLogs.AnyAsync(a => a.Action == "storage.threshold_critical"));
        }

        // Aynan 92% -> kritik.
        probe.Result = (total, (long)(total * 0.08));
        using (var scope2 = scopeFactory.CreateScope())
        {
            var runner2 = scope2.ServiceProvider.GetRequiredService<ArchiveJobRunner>();
            await runner2.RunOnceAsync(new DateTime(2026, 10, 3, 4, 0, 0, DateTimeKind.Utc));
        }

        await using (var db = _fixture.CreateContext())
        {
            Assert.True(await db.AuditLogs.AnyAsync(a => a.Action == "storage.threshold_critical"));
        }

        // Chegaradan past -> hech qanday ogohlantirish yo'q.
        await using (var db = _fixture.CreateContext())
        {
            await db.AuditLogs.ExecuteDeleteAsync();
        }
        probe.Result = (total, (long)(total * 0.21)); // 79%
        using (var scope3 = scopeFactory.CreateScope())
        {
            var runner3 = scope3.ServiceProvider.GetRequiredService<ArchiveJobRunner>();
            await runner3.RunOnceAsync(new DateTime(2026, 10, 4, 4, 0, 0, DateTimeKind.Utc));
        }

        await using (var db = _fixture.CreateContext())
        {
            Assert.False(await db.AuditLogs.AnyAsync(a => a.Action.StartsWith("storage.threshold_w")));
            Assert.False(await db.AuditLogs.AnyAsync(a => a.Action == "storage.threshold_critical"));
        }
    }

    // 16. Dry-run hisobotidagi `at_least` bayrog'i va staging hisoboti.
    // Ikkalasi ham UI va operator uchun yagona ko'rinish manbayi.
    [Fact]
    public async Task Test16_Dry_run_marks_truncated_preview_and_reports_staging()
    {
        await ResetStateAsync();
        var peer = Guid.NewGuid().ToString("N");
        await using (var db = _fixture.CreateContext())
        {
            db.Records.AddRange(Enumerable.Range(0, 3)
                .Select(_ => MakeRecord(peer, kind: "activity", daysAgo: 60)));
            await db.SaveChangesAsync();
        }

        // Staging papkasida bitta "arxiv" bo'lsin.
        await File.WriteAllTextAsync(Path.Combine(_stagingDir, "existing.cmx"), "xxxxx");

        var (_, scopeFactory, _) = BuildServices();
        using var scope = scopeFactory.CreateScope();
        var settings = scope.ServiceProvider.GetRequiredService<SettingsService>();
        await settings.EnsureDefaultsAsync();
        await settings.SetAsync("storage.jobs_enabled", "true");
        await settings.SetAsync("storage.jobs_dry_run", "true");
        await settings.SetAsync("retention.activity_days", "30");

        var runner = scope.ServiceProvider.GetRequiredService<ArchiveJobRunner>();
        runner.BatchLimit = 2; // 3 yozuv -> preview kesiladi
        await runner.RunOnceAsync(new DateTime(2026, 10, 5, 4, 0, 0, DateTimeKind.Utc));

        await using (var db = _fixture.CreateContext())
        {
            var dryRun = await db.AuditLogs.FirstOrDefaultAsync(a => a.Action == "archive_job.dry_run");
            Assert.NotNull(dryRun);
            Assert.Contains("\"at_least\":true", dryRun!.Detail);

            var staging = await db.AuditLogs.FirstOrDefaultAsync(a => a.Action == "archive_job.staging_report");
            Assert.NotNull(staging);
            Assert.Contains("\"count\":1", staging!.Detail);

            // Dry-run hech narsa o'chirmaydi.
            Assert.Equal(3, await db.Records.CountAsync(r => r.PeerHash == peer));
        }
    }

    // 17. Jadval sozlamasini o'qish: buzuq yoki oraliqdan tashqari qiymat
    // sababini aytib beradi (avval `IsDue` jimgina false qaytarardi va
    // rejalashtirilgan tozalash sababsiz o'chib qolardi).
    [Fact]
    public void Test17_Schedule_resolution_reports_invalid_configuration()
    {
        Assert.True(ArchiveSchedule.TryResolve("3", "30", out var hour, out var minute, out var error));
        Assert.Equal(3, hour);
        Assert.Equal(30, minute);
        Assert.Null(error);

        Assert.False(ArchiveSchedule.TryResolve("25", "30", out _, out _, out error));
        Assert.Contains("storage.jobs_hour", error);

        Assert.False(ArchiveSchedule.TryResolve("3", "60", out _, out _, out error));
        Assert.Contains("storage.jobs_minute", error);

        Assert.False(ArchiveSchedule.TryResolve("uch", "30", out _, out _, out error));
        Assert.Contains("storage.jobs_hour", error);

        Assert.False(ArchiveSchedule.TryResolve(null, null, out _, out _, out error));
        Assert.NotNull(error);
    }
}
