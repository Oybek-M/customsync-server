using CustomSync.Core.Interchange;
using System.Security.Cryptography;
using CustomSync.Core.Contracts;
using CustomSync.Data;
using CustomSync.Data.Entities;
using CustomSync.Services;
using CustomSync.Services.Storage;
using CustomSync.Services.Storage.Targets;
using CustomSync.Tests.Fixtures;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CustomSync.Tests;

public class PurgeSafetyTests : IClassFixture<DatabaseFixture>, IDisposable
{
    private readonly DatabaseFixture _fixture;
    private readonly string _tempDir;
    private readonly string _stagingDir;
    private static long _seqCounter = 100_000;

    public PurgeSafetyTests(DatabaseFixture fixture)
    {
        _fixture = fixture;
        _tempDir = Path.Combine(Path.GetTempPath(), $"cs-purge-test-{Guid.NewGuid():N}");
        _stagingDir = Path.Combine(_tempDir, "staging");
        Directory.CreateDirectory(_stagingDir);
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
        catch
        {
            // Test cleanup
        }
    }

    private static long NextSeq() => Interlocked.Increment(ref _seqCounter);

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

    private (MediaBlobEntity Blob, string FilePath) MakeMediaBlob(
        string mediaRoot,
        DateTime uploadedAt,
        DateTime? orphanedAt = null)
    {
        var hash = Guid.NewGuid().ToString("N");
        var prefix = hash[..2];
        var dir = Path.Combine(mediaRoot, prefix);
        Directory.CreateDirectory(dir);
        var filePath = Path.Combine(dir, hash);
        File.WriteAllBytes(filePath, [1, 2, 3, 4, 5, 6, 7, 8]);

        var blob = new MediaBlobEntity
        {
            Hash               = hash,
            Size               = 8,
            Nonce              = new byte[12],
            StoragePath        = filePath,
            UploadedAt         = uploadedAt,
            UploadedByDeviceId = "dev-test-1",
            OrphanedAt         = orphanedAt
        };

        return (blob, filePath);
    }

    private class WorkingArchiveTarget : IArchiveTarget
    {
        private readonly Dictionary<string, byte[]> _storage = new();

        public string TargetId => "working_target";
        public string DisplayName => "Working Test Target";
        public bool RequiresExplicitConfirmation => false;

        public Task<bool> HealthCheckAsync(CancellationToken ct = default) => Task.FromResult(true);

        public async Task<ArchiveUploadResult> UploadAsync(string archiveName, Stream content, CancellationToken ct = default)
        {
            using var ms = new MemoryStream();
            await content.CopyToAsync(ms, ct);
            var bytes = ms.ToArray();
            _storage[archiveName] = bytes;
            var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
            return new ArchiveUploadResult(true, archiveName, hash, null);
        }

        public Task<Stream?> DownloadAsync(string location, CancellationToken ct = default)
        {
            if (_storage.TryGetValue(location, out var bytes))
            {
                return Task.FromResult<Stream?>(new MemoryStream(bytes));
            }
            return Task.FromResult<Stream?>(null);
        }
    }

    private class FailingUploadTarget : IArchiveTarget
    {
        public string TargetId => "failing_upload";
        public string DisplayName => "Failing Upload Target";
        public bool RequiresExplicitConfirmation => false;

        public Task<bool> HealthCheckAsync(CancellationToken ct = default) => Task.FromResult(true);

        public Task<ArchiveUploadResult> UploadAsync(string archiveName, Stream content, CancellationToken ct = default)
            => Task.FromResult(new ArchiveUploadResult(false, null, null, "Simulated network failure during upload"));

        public Task<Stream?> DownloadAsync(string location, CancellationToken ct = default)
            => Task.FromResult<Stream?>(null);
    }

    private class CorruptingTarget : IArchiveTarget
    {
        public string TargetId => "corrupting_target";
        public string DisplayName => "Corrupting Test Target";
        public bool RequiresExplicitConfirmation => false;

        public Task<bool> HealthCheckAsync(CancellationToken ct = default) => Task.FromResult(true);

        public Task<ArchiveUploadResult> UploadAsync(string archiveName, Stream content, CancellationToken ct = default)
            => Task.FromResult(new ArchiveUploadResult(true, "corrupted-loc", null, null));

        public Task<Stream?> DownloadAsync(string location, CancellationToken ct = default)
        {
            // Qaytarilgan oqim boshqa baytlarni qaytaradi -> SHA256 mos kelmaydi
            var corrupted = new byte[] { 0xDE, 0xAD, 0xBE, 0xEF };
            return Task.FromResult<Stream?>(new MemoryStream(corrupted));
        }
    }

    private class WrongShaTarget : IArchiveTarget
    {
        private readonly Dictionary<string, byte[]> _storage = new();

        public string TargetId => "wrong_sha_target";
        public string DisplayName => "Wrong Sha Test Target";
        public bool RequiresExplicitConfirmation => false;

        public Task<bool> HealthCheckAsync(CancellationToken ct = default) => Task.FromResult(true);

        public async Task<ArchiveUploadResult> UploadAsync(string archiveName, Stream content, CancellationToken ct = default)
        {
            using var ms = new MemoryStream();
            await content.CopyToAsync(ms, ct);
            var bytes = ms.ToArray();
            _storage[archiveName] = bytes;
            // Ataylab noto'g'ri SHA256 qaytaradi
            return new ArchiveUploadResult(true, archiveName, "0000000000000000000000000000000000000000000000000000000000000000", null);
        }

        public Task<Stream?> DownloadAsync(string location, CancellationToken ct = default)
        {
            if (_storage.TryGetValue(location, out var bytes))
            {
                return Task.FromResult<Stream?>(new MemoryStream(bytes));
            }
            return Task.FromResult<Stream?>(null);
        }
    }

    private class InterceptingUploadTarget(Func<Task> onUpload) : IArchiveTarget
    {
        private readonly Dictionary<string, byte[]> _storage = new();

        public string TargetId => "intercepting_target";
        public string DisplayName => "Intercepting Test Target";
        public bool RequiresExplicitConfirmation => false;

        public Task<bool> HealthCheckAsync(CancellationToken ct = default) => Task.FromResult(true);

        public async Task<ArchiveUploadResult> UploadAsync(string archiveName, Stream content, CancellationToken ct = default)
        {
            // Yuklash paytida tashqi amalni (masalan supersede push) bajaramiz
            await onUpload();

            using var ms = new MemoryStream();
            await content.CopyToAsync(ms, ct);
            var bytes = ms.ToArray();
            _storage[archiveName] = bytes;
            var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
            return new ArchiveUploadResult(true, archiveName, hash, null);
        }

        public Task<Stream?> DownloadAsync(string location, CancellationToken ct = default)
        {
            if (_storage.TryGetValue(location, out var bytes))
            {
                return Task.FromResult<Stream?>(new MemoryStream(bytes));
            }
            return Task.FromResult<Stream?>(null);
        }
    }

    // ─────────────────────────────────────────────────────────────────────────────
    // Plandagi 4 ta bazaviy test
    // ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Nothing_is_deleted_when_upload_fails()
    {
        await using var db = _fixture.CreateContext();
        var peerHash = Guid.NewGuid().ToString("N");
        var record = MakeRecord(peerHash, daysAgo: 60);
        db.Records.Add(record);

        var policy = new RetentionPolicyEntity
        {
            PolicyId = "pol_" + Guid.NewGuid().ToString("N")[..8],
            Name = "Purge Test Policy",
            Enabled = true,
            Kind = "activity",
            PeerHash = peerHash,
            OlderThanDays = 30,
            Action = RetentionActions.ArchiveThenDelete,
            TargetId = "failing_upload",
            Priority = 10,
            UpdatedAt = DateTime.UtcNow
        };
        db.RetentionPolicies.Add(policy);
        await db.SaveChangesAsync();

        var mediaRoot = Path.Combine(_tempDir, "media");
        var mediaService = new MediaService(db, mediaRoot);
        var settingsService = new SettingsService(db);
        var auditService = new AuditService(db);
        var target = new FailingUploadTarget();

        var purge = new PurgeService(db, settingsService, mediaService, auditService, [target]);
        var result = await purge.ExecuteAsync(policy.PolicyId, target);

        Assert.False(result.Success);
        Assert.Equal(ArchiveRunStatus.FailedUpload, result.Status);
        Assert.Equal(0, result.DeletedCount);

        var stillExists = await db.Records.AnyAsync(r => r.RecordId == record.RecordId);
        Assert.True(stillExists);
    }

    [Fact]
    public async Task Nothing_is_deleted_when_verification_fails()
    {
        await using var db = _fixture.CreateContext();
        var peerHash = Guid.NewGuid().ToString("N");
        var record = MakeRecord(peerHash, daysAgo: 60);
        db.Records.Add(record);

        var policy = new RetentionPolicyEntity
        {
            PolicyId = "pol_" + Guid.NewGuid().ToString("N")[..8],
            Name = "Purge Test Policy",
            Enabled = true,
            Kind = "activity",
            PeerHash = peerHash,
            OlderThanDays = 30,
            Action = RetentionActions.ArchiveThenDelete,
            TargetId = "corrupting_target",
            Priority = 10,
            UpdatedAt = DateTime.UtcNow
        };
        db.RetentionPolicies.Add(policy);
        await db.SaveChangesAsync();

        var mediaRoot = Path.Combine(_tempDir, "media");
        var mediaService = new MediaService(db, mediaRoot);
        var settingsService = new SettingsService(db);
        var auditService = new AuditService(db);
        var target = new CorruptingTarget();

        var purge = new PurgeService(db, settingsService, mediaService, auditService, [target]);
        var result = await purge.ExecuteAsync(policy.PolicyId, target);

        Assert.False(result.Success);
        Assert.Equal(ArchiveRunStatus.FailedVerification, result.Status);
        Assert.Contains("verification", result.Error!, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, result.DeletedCount);

        var stillExists = await db.Records.AnyAsync(r => r.RecordId == record.RecordId);
        Assert.True(stillExists);
    }

    [Fact]
    public async Task Dry_run_never_deletes_anything()
    {
        await using var db = _fixture.CreateContext();
        var peerHash = Guid.NewGuid().ToString("N");
        var record = MakeRecord(peerHash, daysAgo: 60);
        db.Records.Add(record);

        var policy = new RetentionPolicyEntity
        {
            PolicyId = "pol_" + Guid.NewGuid().ToString("N")[..8],
            Name = "Purge Test Policy",
            Enabled = true,
            Kind = "activity",
            PeerHash = peerHash,
            OlderThanDays = 30,
            Action = RetentionActions.ArchiveThenDelete,
            TargetId = "working_target",
            Priority = 10,
            UpdatedAt = DateTime.UtcNow
        };
        db.RetentionPolicies.Add(policy);
        await db.SaveChangesAsync();

        var mediaRoot = Path.Combine(_tempDir, "media");
        var mediaService = new MediaService(db, mediaRoot);
        var settingsService = new SettingsService(db);
        var auditService = new AuditService(db);
        var target = new WorkingArchiveTarget();

        var purge = new PurgeService(db, settingsService, mediaService, auditService, [target]);
        var preview = await purge.PreviewAsync(policy.PolicyId);

        Assert.True(preview.MatchedCount > 0);
        Assert.Equal(1, preview.MatchedCount);

        var stillExists = await db.Records.AnyAsync(r => r.RecordId == record.RecordId);
        Assert.True(stillExists);
    }

    [Fact]
    public async Task Records_are_deleted_only_after_verified_archive()
    {
        await using var db = _fixture.CreateContext();
        var peerHash = Guid.NewGuid().ToString("N");
        var record = MakeRecord(peerHash, daysAgo: 60);
        db.Records.Add(record);

        var policy = new RetentionPolicyEntity
        {
            PolicyId = "pol_" + Guid.NewGuid().ToString("N")[..8],
            Name = "Purge Test Policy",
            Enabled = true,
            Kind = "activity",
            PeerHash = peerHash,
            OlderThanDays = 30,
            Action = RetentionActions.ArchiveThenDelete,
            TargetId = "working_target",
            Priority = 10,
            UpdatedAt = DateTime.UtcNow
        };
        db.RetentionPolicies.Add(policy);
        await db.SaveChangesAsync();

        var mediaRoot = Path.Combine(_tempDir, "media");
        var mediaService = new MediaService(db, mediaRoot);
        var settingsService = new SettingsService(db);
        var auditService = new AuditService(db);
        var target = new WorkingArchiveTarget();

        var purge = new PurgeService(db, settingsService, mediaService, auditService, [target]);
        var result = await purge.ExecuteAsync(policy.PolicyId, target);

        Assert.True(result.Success);
        Assert.Equal(ArchiveRunStatus.Completed, result.Status);
        Assert.Equal(1, result.DeletedCount);
        Assert.Equal(record.PayloadSize, result.FreedBytes);

        var stillExists = await db.Records.AnyAsync(r => r.RecordId == record.RecordId);
        Assert.False(stillExists);
    }

    // ─────────────────────────────────────────────────────────────────────────────
    // 7 ta majburiy qo'shimcha xavfsizlik testlari
    // ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Test1_Never_delete_protected_record_remains_after_purge()
    {
        await using var db = _fixture.CreateContext();
        var peerNormal = Guid.NewGuid().ToString("N");
        var peerVip = Guid.NewGuid().ToString("N");

        var recNormal = MakeRecord(peerNormal, daysAgo: 60);
        var recVip = MakeRecord(peerVip, daysAgo: 60);
        db.Records.AddRange(recNormal, recVip);

        // Umumiy o'chirish siyosati (priority 10)
        var purgePolicy = new RetentionPolicyEntity
        {
            PolicyId = "pol_purge_all",
            Name = "Purge all activity older than 30d",
            Enabled = true,
            Kind = "activity",
            OlderThanDays = 30,
            Action = RetentionActions.ArchiveThenDelete,
            TargetId = "working_target",
            Priority = 10,
            UpdatedAt = DateTime.UtcNow
        };

        // VIP peer uchun never_delete himoyasi (priority 1 -- hatto past ustuvorlikda ham)
        var vipPolicy = new RetentionPolicyEntity
        {
            PolicyId = "pol_vip_protect",
            Name = "Protect VIP peer",
            Enabled = true,
            PeerHash = peerVip,
            Action = RetentionActions.NeverDelete,
            Priority = 1,
            UpdatedAt = DateTime.UtcNow
        };

        db.RetentionPolicies.AddRange(purgePolicy, vipPolicy);
        await db.SaveChangesAsync();

        var mediaRoot = Path.Combine(_tempDir, "media");
        var mediaService = new MediaService(db, mediaRoot);
        var settingsService = new SettingsService(db);
        var auditService = new AuditService(db);
        var target = new WorkingArchiveTarget();

        var purge = new PurgeService(db, settingsService, mediaService, auditService, [target]);
        var result = await purge.ExecuteAsync(purgePolicy.PolicyId, target);

        try
        {
            Assert.True(result.Success);
            // Oddiy yozuv o'chirilgan bo'lishi kerak
            var normalExists = await db.Records.AnyAsync(r => r.RecordId == recNormal.RecordId);
            Assert.False(normalExists);

            // 🔴 VIP yozuv never_delete himoyasi tufayli joyida qolishi SHART
            var vipExists = await db.Records.AnyAsync(r => r.RecordId == recVip.RecordId);
            Assert.True(vipExists);
        }
        finally
        {
            db.RetentionPolicies.RemoveRange(purgePolicy, vipPolicy);
            await db.SaveChangesAsync();
        }
    }

    [Fact]
    public async Task Test2_Tombstone_is_never_deleted_even_if_old()
    {
        await using var db = _fixture.CreateContext();
        var peerHash = Guid.NewGuid().ToString("N");
        var tombstone = MakeRecord(peerHash, kind: RecordKind.Tombstone, daysAgo: 120);
        tombstone.TargetRecordId = "some_target_id";
        db.Records.Add(tombstone);

        var policy = new RetentionPolicyEntity
        {
            PolicyId = "pol_purge_tombstones",
            Name = "Try to purge tombstones",
            Enabled = true,
            Kind = RecordKind.Tombstone,
            OlderThanDays = 30,
            Action = RetentionActions.DeleteOnly,
            Priority = 10,
            UpdatedAt = DateTime.UtcNow
        };
        db.RetentionPolicies.Add(policy);
        await db.SaveChangesAsync();

        var mediaRoot = Path.Combine(_tempDir, "media");
        var mediaService = new MediaService(db, mediaRoot);
        var settingsService = new SettingsService(db);
        var auditService = new AuditService(db);

        var purge = new PurgeService(db, settingsService, mediaService, auditService, []);

        var preview = await purge.PreviewAsync(policy.PolicyId);
        Assert.Equal(0, preview.MatchedCount);

        var result = await purge.ExecuteAsync(policy.PolicyId);
        Assert.Equal(ArchiveRunStatus.NothingToDo, result.Status);
        Assert.Equal(0, result.DeletedCount);

        // 🔴 Tombstone o'chmasdan saqlanib qolishi SHART
        var tombExists = await db.Records.AnyAsync(r => r.RecordId == tombstone.RecordId);
        Assert.True(tombExists);
    }

    [Fact]
    public async Task Test3_Superseded_record_during_upload_is_not_deleted()
    {
        await using var db = _fixture.CreateContext();
        var peerHash = Guid.NewGuid().ToString("N");

        var recA = MakeRecord(peerHash, daysAgo: 60, observedAt: 1000, deviceId: "dev-1");
        var recB = MakeRecord(peerHash, daysAgo: 60, observedAt: 1000, deviceId: "dev-1");
        db.Records.AddRange(recA, recB);

        var policy = new RetentionPolicyEntity
        {
            PolicyId = "pol_supersede_test",
            Name = "Supersede test policy",
            Enabled = true,
            Kind = "activity",
            PeerHash = peerHash,
            OlderThanDays = 30,
            Action = RetentionActions.ArchiveThenDelete,
            TargetId = "intercepting_target",
            Priority = 10,
            UpdatedAt = DateTime.UtcNow
        };
        db.RetentionPolicies.Add(policy);
        await db.SaveChangesAsync();

        var mediaRoot = Path.Combine(_tempDir, "media");
        var mediaService = new MediaService(db, mediaRoot);
        var settingsService = new SettingsService(db);
        var auditService = new AuditService(db);

        // UploadAsync davomida (arxiv tayyor bo'lib upload ketayotganda) recA ustiga yangiroq kuzatuv tushadi
        var target = new InterceptingUploadTarget(async () =>
        {
            await using var freshDb = _fixture.CreateContext();
            var rowToUpdate = await freshDb.Records.FirstAsync(r => r.RecordId == recA.RecordId);
            rowToUpdate.ObservedAt = 2000; // Yangi kuzatuv!
            rowToUpdate.DeviceId = "dev-2";
            rowToUpdate.Seq = NextSeq();
            await freshDb.SaveChangesAsync();
        });

        var purge = new PurgeService(db, settingsService, mediaService, auditService, [target]);
        var result = await purge.ExecuteAsync(policy.PolicyId, target);

        Assert.True(result.Success);
        // Faqat recB o'chishi kerak, recA esa o'chmasligi kerak!
        Assert.Equal(1, result.DeletedCount);

        var recAExists = await db.Records.AnyAsync(r => r.RecordId == recA.RecordId);
        var recBExists = await db.Records.AnyAsync(r => r.RecordId == recB.RecordId);

        Assert.True(recAExists, "Kuzatuv yangilangan yozuv (recA) o'chib ketmasligi shart!");
        Assert.False(recBExists, "O'zgarmagan yozuv (recB) o'chirilishi shart!");
    }

    [Fact]
    public async Task Test4_Blob_referenced_by_other_record_or_unlinked_new_blob_is_not_deleted()
    {
        await using var db = _fixture.CreateContext();
        var peerHash = Guid.NewGuid().ToString("N");
        var mediaRoot = Path.Combine(_tempDir, "media");

        // rec1 -- o'chadigan eski yozuv
        var rec1 = MakeRecord(peerHash, daysAgo: 60);
        // rec2 -- o'chmaydigan yangi yozuv
        var rec2 = MakeRecord(peerHash, daysAgo: 5);
        db.Records.AddRange(rec1, rec2);

        // blob1: rec1 va rec2 ikkalasiga ham ulangan
        var (blob1, path1) = MakeMediaBlob(mediaRoot, DateTime.UtcNow.AddDays(-60));
        // blob2: yangi yuklangan blob, hali hech qanday yozuvga ulanmagan
        var (blob2, path2) = MakeMediaBlob(mediaRoot, DateTime.UtcNow);

        db.MediaBlobs.AddRange(blob1, blob2);
        db.RecordMedia.AddRange(
            new RecordMediaEntity { RecordId = rec1.RecordId, Hash = blob1.Hash },
            new RecordMediaEntity { RecordId = rec2.RecordId, Hash = blob1.Hash });

        var policy = new RetentionPolicyEntity
        {
            PolicyId = "pol_blob_protect_test",
            Name = "Blob protection test policy",
            Enabled = true,
            Kind = "activity",
            PeerHash = peerHash,
            OlderThanDays = 30,
            Action = RetentionActions.ArchiveThenDelete,
            TargetId = "working_target",
            Priority = 10,
            UpdatedAt = DateTime.UtcNow
        };
        db.RetentionPolicies.Add(policy);
        await db.SaveChangesAsync();

        var mediaService = new MediaService(db, mediaRoot);
        var settingsService = new SettingsService(db);
        var auditService = new AuditService(db);
        var target = new WorkingArchiveTarget();

        var purge = new PurgeService(db, settingsService, mediaService, auditService, [target]);
        var result = await purge.ExecuteAsync(policy.PolicyId, target);

        Assert.True(result.Success);
        Assert.Equal(1, result.DeletedCount);

        // rec1 o'chdi, rec2 turibdi
        Assert.False(await db.Records.AnyAsync(r => r.RecordId == rec1.RecordId));
        Assert.True(await db.Records.AnyAsync(r => r.RecordId == rec2.RecordId));

        // 🔴 blob1 hali ham rec2 ga bog'langan, shuning uchun orphaned_at belgilanmasligi va fayl o'chmasligi kerak
        var b1 = await db.MediaBlobs.FirstAsync(b => b.Hash == blob1.Hash);
        Assert.Null(b1.OrphanedAt);
        Assert.True(File.Exists(path1));

        // 🔴 blob2 hech kimga ulanmagan yangi blob, u purge nomzodi emas, tegmaslik kerak
        var b2 = await db.MediaBlobs.FirstAsync(b => b.Hash == blob2.Hash);
        Assert.Null(b2.OrphanedAt);
        Assert.True(File.Exists(path2));
    }

    [Fact]
    public async Task Test5_Orphaned_blob_not_deleted_immediately_deleted_only_after_24h_and_exists_clears_flag()
    {
        await using var db = _fixture.CreateContext();
        var peerHash = Guid.NewGuid().ToString("N");
        var mediaRoot = Path.Combine(_tempDir, "media");

        var recA = MakeRecord(peerHash, daysAgo: 60);
        var (blobA, pathA) = MakeMediaBlob(mediaRoot, DateTime.UtcNow.AddDays(-60));
        db.Records.Add(recA);
        db.MediaBlobs.Add(blobA);
        db.RecordMedia.Add(new RecordMediaEntity { RecordId = recA.RecordId, Hash = blobA.Hash });

        // blobB: avvalroq (25 soat oldin) yetim bo'lgan blob
        var (blobB, pathB) = MakeMediaBlob(mediaRoot, DateTime.UtcNow.AddDays(-60), orphanedAt: DateTime.UtcNow.AddHours(-25));
        db.MediaBlobs.Add(blobB);

        var policy = new RetentionPolicyEntity
        {
            PolicyId = "pol_orphan_lifecycle_test",
            Name = "Orphan lifecycle test policy",
            Enabled = true,
            Kind = "activity",
            PeerHash = peerHash,
            OlderThanDays = 30,
            Action = RetentionActions.DeleteOnly,
            Priority = 10,
            UpdatedAt = DateTime.UtcNow
        };
        db.RetentionPolicies.Add(policy);
        await db.SaveChangesAsync();

        var mediaService = new MediaService(db, mediaRoot);
        var settingsService = new SettingsService(db);
        var auditService = new AuditService(db);

        var purge = new PurgeService(db, settingsService, mediaService, auditService, []);
        var result = await purge.ExecuteAsync(policy.PolicyId);

        Assert.True(result.Success);

        // 1. recA o'chdi. blobA ning yagona havolasi ketdi -> blobA darhol o'chmaydi, orphaned_at belgilanadi
        var freshBlobA = await db.MediaBlobs.FirstOrDefaultAsync(b => b.Hash == blobA.Hash);
        Assert.NotNull(freshBlobA);
        Assert.NotNull(freshBlobA.OrphanedAt);
        Assert.True(File.Exists(pathA), "BlobA fayli darhol o'chmasligi shart!");

        // 2. blobB esa 25 soat oldin yetim bo'lgani sababli bu purge paytida to'liq o'chishi kerak
        var freshBlobB = await db.MediaBlobs.FirstOrDefaultAsync(b => b.Hash == blobB.Hash);
        Assert.Null(freshBlobB);
        Assert.False(File.Exists(pathB), "24 soatdan oshgan yetim blobB fayli o'chirilishi shart!");

        // 3. ExistsAsync chaqirilganda blobA ning orphaned_at belgisi tozalanishini tekshiramiz
        var exists = await mediaService.ExistsAsync(blobA.Hash);
        Assert.True(exists);

        var unorphanedBlobA = await db.MediaBlobs.FirstAsync(b => b.Hash == blobA.Hash);
        Assert.Null(unorphanedBlobA.OrphanedAt);
    }

    [Fact]
    public async Task Test6_Manual_target_requires_confirmation_deletes_only_after_confirm_rejects_if_altered()
    {
        await using var db = _fixture.CreateContext();
        var peerHash = Guid.NewGuid().ToString("N");
        var record = MakeRecord(peerHash, daysAgo: 60);
        db.Records.Add(record);

        var target = new ManualDownloadTarget(_stagingDir);

        var policy = new RetentionPolicyEntity
        {
            PolicyId = "pol_manual_confirm_test",
            Name = "Manual confirm test policy",
            Enabled = true,
            Kind = "activity",
            PeerHash = peerHash,
            OlderThanDays = 30,
            Action = RetentionActions.ArchiveThenDelete,
            TargetId = target.TargetId,
            Priority = 10,
            UpdatedAt = DateTime.UtcNow
        };
        db.RetentionPolicies.Add(policy);
        await db.SaveChangesAsync();

        var mediaRoot = Path.Combine(_tempDir, "media");
        var mediaService = new MediaService(db, mediaRoot);
        var settingsService = new SettingsService(db);
        var auditService = new AuditService(db);

        var purge = new PurgeService(db, settingsService, mediaService, auditService, [target]);

        // 1-bosqich: ExecuteAsync
        var result = await purge.ExecuteAsync(policy.PolicyId, target);

        Assert.True(result.Success);
        Assert.Equal(ArchiveRunStatus.AwaitingConfirmation, result.Status);
        Assert.Equal(0, result.DeletedCount);
        Assert.NotNull(result.ArchiveLocation);

        // 🔴 Manual target bilan hech narsa darhol o'chmaydi
        var stillExists = await db.Records.AnyAsync(r => r.RecordId == record.RecordId);
        Assert.True(stillExists);

        // 2-bosqich: Arxivni o'zgartirib buzamiz (tampering)
        var originalBytes = await File.ReadAllBytesAsync(result.ArchiveLocation);
        var tamperedBytes = (byte[])originalBytes.Clone();
        tamperedBytes[^1] ^= 0xFF; // oxirgi baytni teskari qilamiz
        await File.WriteAllBytesAsync(result.ArchiveLocation, tamperedBytes);

        var tamperedConfirm = await purge.ConfirmAsync(result.RunId, target);
        Assert.False(tamperedConfirm.Success);
        Assert.Contains("verification", tamperedConfirm.Error!, StringComparison.OrdinalIgnoreCase);

        // Yozuv hali ham joyida
        Assert.True(await db.Records.AnyAsync(r => r.RecordId == record.RecordId));

        // 3-bosqich: Asl arxivni qaytaramiz va muvaffaqiyatli tasdiqlaymiz
        await File.WriteAllBytesAsync(result.ArchiveLocation, originalBytes);

        var successConfirm = await purge.ConfirmAsync(result.RunId, target);
        Assert.True(successConfirm.Success);
        Assert.Equal(ArchiveRunStatus.Completed, successConfirm.Status);
        Assert.Equal(1, successConfirm.DeletedCount);

        // 🔴 Endi yozuv haqiqatan o'chgan bo'lishi kerak
        var deletedNow = await db.Records.AnyAsync(r => r.RecordId == record.RecordId);
        Assert.False(deletedNow);
    }

    [Fact]
    public async Task Test7_Upload_with_wrong_sha256_aborts_purge_without_deleting()
    {
        await using var db = _fixture.CreateContext();
        var peerHash = Guid.NewGuid().ToString("N");
        var record = MakeRecord(peerHash, daysAgo: 60);
        db.Records.Add(record);

        var target = new WrongShaTarget();

        var policy = new RetentionPolicyEntity
        {
            PolicyId = "pol_wrong_sha_test",
            Name = "Wrong SHA test policy",
            Enabled = true,
            Kind = "activity",
            PeerHash = peerHash,
            OlderThanDays = 30,
            Action = RetentionActions.ArchiveThenDelete,
            TargetId = target.TargetId,
            Priority = 10,
            UpdatedAt = DateTime.UtcNow
        };
        db.RetentionPolicies.Add(policy);
        await db.SaveChangesAsync();

        var mediaRoot = Path.Combine(_tempDir, "media");
        var mediaService = new MediaService(db, mediaRoot);
        var settingsService = new SettingsService(db);
        var auditService = new AuditService(db);

        var purge = new PurgeService(db, settingsService, mediaService, auditService, [target]);
        var result = await purge.ExecuteAsync(policy.PolicyId, target);

        Assert.False(result.Success);
        Assert.Equal(ArchiveRunStatus.FailedVerification, result.Status);
        Assert.Equal(0, result.DeletedCount);

        // 🔴 Hech narsa o'chirilmasligi shart
        var stillExists = await db.Records.AnyAsync(r => r.RecordId == record.RecordId);
        Assert.True(stillExists);
    }
// ─────────────────────────────────────────────────────────────────────────────
    // Tekshiruvdan chiqqan qo'shimcha xavfsizlik testlari (K1–K3, T1–T3)
    // ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Test_K1_Confirm_re_evaluates_retention_protecting_newly_added_never_delete()
    {
        await using var db = _fixture.CreateContext();
        var peerHash = Guid.NewGuid().ToString("N");
        var record = MakeRecord(peerHash, daysAgo: 60);
        db.Records.Add(record);

        var policy = new RetentionPolicyEntity
        {
            PolicyId = "pol_k1_test_" + Guid.NewGuid().ToString("N")[..8],
            Name = "K1 test policy",
            Enabled = true,
            Kind = "activity",
            PeerHash = peerHash,
            OlderThanDays = 30,
            Action = RetentionActions.ArchiveThenDelete,
            TargetId = "manual_download",
            Priority = 10,
            UpdatedAt = DateTime.UtcNow
        };
        db.RetentionPolicies.Add(policy);
        await db.SaveChangesAsync();

        var mediaRoot = Path.Combine(_tempDir, "media");
        var mediaService = new MediaService(db, mediaRoot);
        var settingsService = new SettingsService(db);
        var auditService = new AuditService(db);
        var target = new ManualDownloadTarget(Path.Combine(_tempDir, "manual-download-k1"));

        var purge = new PurgeService(db, settingsService, mediaService, auditService, [target]);
        var execResult = await purge.ExecuteAsync(policy.PolicyId, target);

        Assert.True(execResult.Success);
        Assert.Equal(ArchiveRunStatus.AwaitingConfirmation, execResult.Status);
        Assert.Equal(0, execResult.DeletedCount);

        // Operator bu peer uchun never_delete siyosatini qo'shadi
        var neverDeletePolicy = new RetentionPolicyEntity
        {
            PolicyId = "pol_k1_protect_" + Guid.NewGuid().ToString("N")[..8],
            Name = "K1 protect policy",
            Enabled = true,
            PeerHash = peerHash,
            Action = RetentionActions.NeverDelete,
            Priority = 1,
            UpdatedAt = DateTime.UtcNow
        };
        db.RetentionPolicies.Add(neverDeletePolicy);
        await db.SaveChangesAsync();

        // Confirm chaqiriladi
        var confirmResult = await purge.ConfirmAsync(execResult.RunId, target);
        Assert.True(confirmResult.Success);
        Assert.Equal(ArchiveRunStatus.Completed, confirmResult.Status);
        Assert.Equal(0, confirmResult.DeletedCount); // never_delete tufayli 0 yozuv o'chadi

        var stillExists = await db.Records.AnyAsync(r => r.RecordId == record.RecordId);
        Assert.True(stillExists, "K1: never_delete qo'shilgan yozuv ConfirmAsync dan keyin ham o'chmasligi shart!");
    }

    [Fact]
    public async Task Test_K2_Sweep_orphaned_media_atomic_not_exists_check()
    {
        await using var db = _fixture.CreateContext();
        var mediaRoot = Path.Combine(_tempDir, "media");

        // 25 soat oldin orphaned bo'lgan blob
        var (blob, path) = MakeMediaBlob(mediaRoot, DateTime.UtcNow.AddDays(-60), orphanedAt: DateTime.UtcNow.AddHours(-25));
        db.MediaBlobs.Add(blob);

        // Shu blob'ga havola qiluvchi yangi yozuv
        var peerHash = Guid.NewGuid().ToString("N");
        var activeRec = MakeRecord(peerHash, daysAgo: 5);
        db.Records.Add(activeRec);
        db.RecordMedia.Add(new RecordMediaEntity { RecordId = activeRec.RecordId, Hash = blob.Hash });
        await db.SaveChangesAsync();

        var mediaService = new MediaService(db, mediaRoot);
        var settingsService = new SettingsService(db);
        var auditService = new AuditService(db);
        var purge = new PurgeService(db, settingsService, mediaService, auditService, []);

        // Sweep chaqiriladi
        var deletedBlobs = await purge.SweepOrphanedMediaAsync(DateTime.UtcNow);

        Assert.Equal(0, deletedBlobs);
        Assert.True(File.Exists(path), "Yangi havola mavjud bo'lgan blob fayli o'chmasligi shart!");
        var stillBlob = await db.MediaBlobs.FirstOrDefaultAsync(b => b.Hash == blob.Hash);
        Assert.NotNull(stillBlob);
    }

    [Fact]
    public async Task Test_K3_Candidate_starvation_keyset_skips_protected_and_deletes_eligible()
    {
        await using var db = _fixture.CreateContext();
        var peerK3 = Guid.NewGuid().ToString("N");
        var mediaRoot = Path.Combine(_tempDir, "media");

        // 6 ta himoyalangan eski yozuv (received_at: 70 kun oldin) - bular media'ga ega
        var protectedRecords = Enumerable.Range(0, 6)
            .Select(i => MakeRecord(peerK3, daysAgo: 70))
            .ToList();
        db.Records.AddRange(protectedRecords);

        foreach (var pr in protectedRecords)
        {
            var (blob, _) = MakeMediaBlob(mediaRoot, DateTime.UtcNow.AddDays(-70));
            db.MediaBlobs.Add(blob);
            db.RecordMedia.Add(new RecordMediaEntity { RecordId = pr.RecordId, Hash = blob.Hash });
        }

        // 1 ta yaroqli yangiroq yozuv (received_at: 50 kun oldin) - media'siz
        var eligibleRecord = MakeRecord(peerK3, daysAgo: 50);
        db.Records.Add(eligibleRecord);

        // Media'li yozuvlar uchun never_delete (priority 100)
        var protectPolicy = new RetentionPolicyEntity
        {
            PolicyId = "pol_protect_" + Guid.NewGuid().ToString("N")[..8],
            Name = "Protect Media on Peer",
            Enabled = true,
            PeerHash = peerK3,
            MediaOnly = true,
            Action = RetentionActions.NeverDelete,
            Priority = 100,
            UpdatedAt = DateTime.UtcNow
        };

        // Shu peer'dagi barcha 30 kundan oshgan yozuvlarni o'chirish siyosati (priority 10)
        var purgePolicy = new RetentionPolicyEntity
        {
            PolicyId = "pol_eligible_" + Guid.NewGuid().ToString("N")[..8],
            Name = "Purge Older on Peer",
            Enabled = true,
            PeerHash = peerK3,
            OlderThanDays = 30,
            Action = RetentionActions.DeleteOnly,
            Priority = 10,
            UpdatedAt = DateTime.UtcNow
        };

        db.RetentionPolicies.AddRange(protectPolicy, purgePolicy);
        await db.SaveChangesAsync();

        var mediaService = new MediaService(db, mediaRoot);
        var settingsService = new SettingsService(db);
        var auditService = new AuditService(db);
        var purge = new PurgeService(db, settingsService, mediaService, auditService, []);

        try
        {
            // limit = 5 (eng eski 6 ta nomzod protected media'ga tegishli)
            var result = await purge.ExecuteAsync(purgePolicy.PolicyId, limit: 5);

            Assert.True(result.Success);
            Assert.Equal(1, result.DeletedCount);

            var eligibleExists = await db.Records.AnyAsync(r => r.RecordId == eligibleRecord.RecordId);
            Assert.False(eligibleExists, "K3: Keyset pagination tufayli yaroqli yozuv topilib o'chirilishi shart!");
        }
        finally
        {
            db.RetentionPolicies.RemoveRange(protectPolicy, purgePolicy);
            await db.SaveChangesAsync();
        }
    }

    [Fact]
    public async Task Test_T1_Execute_policy_id_mismatch_never_deletes_record_won_by_higher_priority()
    {
        await using var db = _fixture.CreateContext();
        var peerHash = Guid.NewGuid().ToString("N");
        var record = MakeRecord(peerHash, daysAgo: 60);
        db.Records.Add(record);

        // Priority 20: archive_then_delete (A)
        var policyA = new RetentionPolicyEntity
        {
            PolicyId = "pol_high_archive_" + Guid.NewGuid().ToString("N")[..8],
            Name = "High Priority Archive Policy",
            Enabled = true,
            Kind = "activity",
            PeerHash = peerHash,
            OlderThanDays = 30,
            Action = RetentionActions.ArchiveThenDelete,
            TargetId = "working_target",
            Priority = 20,
            UpdatedAt = DateTime.UtcNow
        };

        // Priority 10: delete_only (B)
        var policyB = new RetentionPolicyEntity
        {
            PolicyId = "pol_low_delete_" + Guid.NewGuid().ToString("N")[..8],
            Name = "Low Priority Delete Policy",
            Enabled = true,
            Kind = "activity",
            PeerHash = peerHash,
            OlderThanDays = 30,
            Action = RetentionActions.DeleteOnly,
            Priority = 10,
            UpdatedAt = DateTime.UtcNow
        };

        db.RetentionPolicies.AddRange(policyA, policyB);
        await db.SaveChangesAsync();

        var mediaRoot = Path.Combine(_tempDir, "media");
        var mediaService = new MediaService(db, mediaRoot);
        var settingsService = new SettingsService(db);
        var auditService = new AuditService(db);
        var target = new WorkingArchiveTarget();
        var purge = new PurgeService(db, settingsService, mediaService, auditService, [target]);

        // Siyosat B (delete_only) ishga tushiriladi
        var result = await purge.ExecuteAsync(policyB.PolicyId);

        // Evaluator da siyosat A yutadi (priority 20 > 10). Shuning uchun siyosat B hech narsa o'chirmasligi shart!
        Assert.True(result.Success);
        Assert.Equal(0, result.DeletedCount);

        var stillExists = await db.Records.AnyAsync(r => r.RecordId == record.RecordId);
        Assert.True(stillExists, "T1: Yuqori prioritetli siyosat yutgan yozuv quyi prioritetli siyosat tomonidan o'chirilmasligi shart!");
    }

    [Fact]
    public async Task Test_T2_Archive_includes_referenced_media_blobs_and_counts_missing()
    {
        await using var db = _fixture.CreateContext();
        var peerHash = Guid.NewGuid().ToString("N");
        var record = MakeRecord(peerHash, daysAgo: 60);
        db.Records.Add(record);

        var mediaRoot = Path.Combine(_tempDir, "media");

        // 1. Diskda mavjud bo'lgan blob
        var (blob1, path1) = MakeMediaBlob(mediaRoot, DateTime.UtcNow.AddDays(-60));
        db.MediaBlobs.Add(blob1);
        db.RecordMedia.Add(new RecordMediaEntity { RecordId = record.RecordId, Hash = blob1.Hash });

        // 2. Diskda MAVJUD BO'LMAGAN blob (missing media)
        var missingHash = Guid.NewGuid().ToString("N");
        var missingBlob = new MediaBlobEntity
        {
            Hash = missingHash,
            Size = 100,
            Nonce = new byte[12],
            StoragePath = Path.Combine(mediaRoot, "non_existent_file"),
            UploadedAt = DateTime.UtcNow.AddDays(-60),
            UploadedByDeviceId = "dev-1"
        };
        db.MediaBlobs.Add(missingBlob);
        db.RecordMedia.Add(new RecordMediaEntity { RecordId = record.RecordId, Hash = missingHash });

        var policy = new RetentionPolicyEntity
        {
            PolicyId = "pol_t2_" + Guid.NewGuid().ToString("N")[..8],
            Name = "T2 Media Archive Policy",
            Enabled = true,
            Kind = "activity",
            PeerHash = peerHash,
            OlderThanDays = 30,
            Action = RetentionActions.ArchiveThenDelete,
            TargetId = "working_target",
            Priority = 10,
            UpdatedAt = DateTime.UtcNow
        };
        db.RetentionPolicies.Add(policy);
        await db.SaveChangesAsync();

        var mediaService = new MediaService(db, mediaRoot);
        var settingsService = new SettingsService(db);
        var auditService = new AuditService(db);
        var target = new WorkingArchiveTarget();
        var purge = new PurgeService(db, settingsService, mediaService, auditService, [target]);

        var result = await purge.ExecuteAsync(policy.PolicyId, target);

        Assert.True(result.Success);
        Assert.Equal(ArchiveRunStatus.Completed, result.Status);
        Assert.Equal(1, result.DeletedCount);
        Assert.Equal(1, result.MissingMedia); // Diskda yo'q blob MissingMedia = 1

        // Target'dagi arxivni yuklab olib tekshiramiz
        await using var archiveStream = await target.DownloadAsync(result.ArchiveLocation!);
        Assert.NotNull(archiveStream);

        var (manifest, syncRecords, mediaBytes) = await CmxReader.ReadAsync(archiveStream, readMedia: true);
        Assert.Single(syncRecords);

        // Arxiv ichida blob1 bo'lishi va baytlari mos kelishi shart
        Assert.True(mediaBytes.ContainsKey(blob1.Hash), "Mavjud media blob arxiv ichida bo'lishi shart!");
        var diskBytes = await File.ReadAllBytesAsync(path1);
        Assert.Equal(diskBytes, mediaBytes[blob1.Hash]);

        // RecordMedia dagi MediaRef.Size va Nonce media_blobs dan to'g'ri olingan
        var ref1 = syncRecords[0].Media.FirstOrDefault(m => m.Hash == blob1.Hash);
        Assert.NotNull(ref1);
        Assert.Equal(blob1.Size, ref1.Size);
        Assert.Equal(blob1.Nonce, ref1.Nonce);
    }

    [Fact]
    public void Test_T3_VerifyArchiveRecordCount_throws_on_mismatch_and_succeeds_on_match()
    {
        // Mos kelmasa InvalidDataException tashlashi kerak
        var ex = Assert.Throws<InvalidDataException>(
            () => PurgeService.VerifyArchiveRecordCount(actualCount: 3, expectedCount: 4));
        Assert.Contains("Verification failed: arxiv yozuvlar soni mos kelmadi", ex.Message);

        // Mos kelsa xatosiz o'tishi kerak
        PurgeService.VerifyArchiveRecordCount(actualCount: 5, expectedCount: 5);
    }
}
