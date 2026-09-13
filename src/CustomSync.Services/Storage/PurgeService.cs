using System.Security.Cryptography;
using CustomSync.Core.Contracts;
using CustomSync.Core.Interchange;
using CustomSync.Data;
using CustomSync.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace CustomSync.Services.Storage;

public sealed record PurgeTargetKey(string RecordId, long ObservedAt, string DeviceId);

public sealed record PurgePreview(
    string PolicyId,
    string Action,
    string? TargetId,
    int MatchedCount,
    long MatchedBytes,
    long EstimatedFreedBytes,
    long? OldestOccurredAt,
    long? NewestOccurredAt,
    int MediaCount);

public sealed record PurgeResult(
    bool Success,
    long RunId,
    string Status,
    int MatchedCount,
    int DeletedCount,
    long FreedBytes,
    int MissingMedia,
    string? ArchiveLocation,
    string? Sha256,
    string? Error);

/// <summary>
/// Ikki fazali xavfsiz o'chirish xizmati.
/// Qamrov RetentionEvaluator orqali aniqlanadi.
/// Tartib: arxivlash -> yuborish -> QAYTA O'QIB TEKSHIRISH -> o'chirish.
/// </summary>
public class PurgeService
{
    public const int DefaultBatchLimit = 5000;

    private readonly SyncDbContext _db;
    private readonly SettingsService _settings;
    private readonly MediaService _media;
    private readonly AuditService _audit;
    private readonly IEnumerable<Targets.IArchiveTarget> _targets;
    private readonly Func<DateTime> _clock;

    public PurgeService(
        SyncDbContext db,
        SettingsService settings,
        MediaService media,
        AuditService audit,
        IEnumerable<Targets.IArchiveTarget> targets,
        Func<DateTime>? clock = null)
    {
        _db = db;
        _settings = settings;
        _media = media;
        _audit = audit;
        _targets = targets;
        _clock = clock ?? (() => DateTime.UtcNow);
    }

    private sealed record ResolvedPolicy(
        string PolicyId,
        string Action,
        string? TargetId,
        string? Kind,
        string? PeerHash,
        bool MediaOnly,
        int OlderThanDays,
        bool IsEnabled);

    private async Task<ResolvedPolicy> ResolvePolicyAsync(string policyId, CancellationToken ct)
    {
        var dbPolicy = await _db.RetentionPolicies.AsNoTracking()
            .FirstOrDefaultAsync(p => p.PolicyId == policyId, ct);

        if (dbPolicy != null)
        {
            return new ResolvedPolicy(
                dbPolicy.PolicyId,
                dbPolicy.Action,
                dbPolicy.TargetId,
                dbPolicy.Kind,
                dbPolicy.PeerHash,
                dbPolicy.MediaOnly,
                dbPolicy.OlderThanDays,
                dbPolicy.Enabled);
        }

        // Implicit setting siyosati: retention.<kind>_days
        if (policyId.StartsWith("retention.", StringComparison.OrdinalIgnoreCase) &&
            policyId.EndsWith("_days", StringComparison.OrdinalIgnoreCase))
        {
            var prefixLen = "retention.".Length;
            var suffixLen = "_days".Length;
            var kind = policyId.Substring(prefixLen, policyId.Length - prefixLen - suffixLen);

            if (RecordKind.IsValid(kind))
            {
                var days = 0;
                try
                {
                    days = await _settings.GetIntAsync(policyId, ct);
                }
                catch (KeyNotFoundException)
                {
                    days = 0;
                }

                return new ResolvedPolicy(
                    policyId,
                    RetentionActions.DeleteOnly,
                    TargetId: null,
                    Kind: kind,
                    PeerHash: null,
                    MediaOnly: false,
                    OlderThanDays: days,
                    IsEnabled: days > 0);
            }
        }

        throw new ArgumentException($"Noma'lum yoki topilmagan retention siyosati: '{policyId}'");
    }

    private sealed record RetentionEvaluationContext(
        IReadOnlyList<RetentionPolicyEntity> Policies,
        IReadOnlyDictionary<string, int> SettingsDays,
        int MinDays,
        DateTime Now);

    private async Task<RetentionEvaluationContext> LoadEvaluationContextAsync(DateTime now, CancellationToken ct)
    {
        var allPolicies = await _db.RetentionPolicies.AsNoTracking()
            .Where(p => p.Enabled)
            .ToListAsync(ct);

        var minDays = 30;
        try
        {
            minDays = await _settings.GetIntAsync("retention.min_days", ct);
        }
        catch (KeyNotFoundException)
        {
            minDays = 30;
        }
        if (minDays <= 0) minDays = 30;

        var settingsDays = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var k in RecordKind.All)
        {
            try
            {
                var d = await _settings.GetIntAsync($"retention.{k}_days", ct);
                if (d > 0) settingsDays[$"retention.{k}_days"] = d;
            }
            catch (KeyNotFoundException)
            {
                // Standart sozlama hali mavjud bo'lmasa o'tkazib yuboramiz
            }
        }

        return new RetentionEvaluationContext(allPolicies, settingsDays, minDays, now);
    }

    /// <summary>
    /// K1: Nomzod yozuvlarni RetentionEvaluator orqali baholash.
    /// GetMatchedRecordsAsync va ConfirmAsync aynan shu yagona metoddan foydalanadi.
    /// </summary>
    private async Task<List<RecordEntity>> FilterMatchedRecordsAsync(
        IReadOnlyList<RecordEntity> records,
        string expectedPolicyId,
        RetentionEvaluationContext evalCtx,
        CancellationToken ct)
    {
        if (records.Count == 0) return [];

        var ids = records.Select(r => r.RecordId).ToList();
        var recordIdsWithMedia = (await _db.RecordMedia.AsNoTracking()
            .Where(rm => ids.Contains(rm.RecordId))
            .Select(rm => rm.RecordId)
            .Distinct()
            .ToListAsync(ct))
            .ToHashSet();

        var matched = new List<RecordEntity>();
        foreach (var record in records)
        {
            // 🔴 QOIDA 2: Tombstone'lar HECH QACHON o'chirilmaydi!
            if (record.Kind == RecordKind.Tombstone)
            {
                continue;
            }

            var candidate = new RetentionCandidate(
                record.Kind,
                record.PeerHash,
                recordIdsWithMedia.Contains(record.RecordId),
                record.ReceivedAt);

            var decision = RetentionEvaluator.Evaluate(
                candidate, evalCtx.Policies, evalCtx.SettingsDays, evalCtx.MinDays, evalCtx.Now);

            // 🔴 T1: Faqat ShouldAct == true VA aynan shu expectedPolicyId g'olib bo'lganlar olinadi.
            // Bu never_delete va boshqa yuqoriroq prioritetli siyosatlarni chetlab o'tishni to'liq oldini oladi.
            if (decision.ShouldAct && string.Equals(decision.PolicyId, expectedPolicyId, StringComparison.OrdinalIgnoreCase))
            {
                matched.Add(record);
            }
        }

        return matched;
    }

    private async Task<List<RecordEntity>> GetMatchedRecordsAsync(
        ResolvedPolicy resolved,
        DateTime now,
        int limit,
        CancellationToken ct)
    {
        if (!resolved.IsEnabled)
        {
            return [];
        }

        // 1. SQL bilan nomzodlarni toraytiramiz.
        // 🔴 QOIDA 2: Tombstone'lar HECH QACHON o'chirilmaydi!
        IQueryable<RecordEntity> query = _db.Records.AsNoTracking()
            .Where(r => r.Kind != RecordKind.Tombstone);

        if (!string.IsNullOrWhiteSpace(resolved.Kind))
        {
            query = query.Where(r => r.Kind == resolved.Kind);
        }

        if (!string.IsNullOrWhiteSpace(resolved.PeerHash))
        {
            query = query.Where(r => r.PeerHash == resolved.PeerHash);
        }

        // Yosh received_at bo'yicha hisoblanadi (evaluator ham received_at ishlatadi)
        if (resolved.OlderThanDays > 0)
        {
            var cutoff = now.AddDays(-resolved.OlderThanDays);
            query = query.Where(r => r.ReceivedAt <= cutoff);
        }

        var evalCtx = await LoadEvaluationContextAsync(now, ct);

        // 🔴 K3 tuzatish: Keyset sahifalash (received_at, seq).
        // Eng eski nomzodlar never_delete yoki boshqa siyosatga tegishli bo'lsa ham,
        // undan yangiroq yaroqli yozuvlar och qolib ketmaydi (starvation yo'q).
        const int MaxScanLimit = 50_000;
        // Pastki chegara 1: aks holda kichik limit'li test (limit=5, 7 yozuv)
        // bitta 100 lik sahifaga sig'ib, sahifalash sikli hech qachon
        // sinalmasdi -- 'faqat birinchi sahifa' mutatsiyasi o'tib ketgan edi.
        int pageSize = Math.Clamp(limit, 1, 1000);
        int totalScanned = 0;
        var matched = new List<RecordEntity>();

        DateTime? cursorReceivedAt = null;
        long? cursorSeq = null;

        while (matched.Count < limit && totalScanned < MaxScanLimit)
        {
            int fetchCount = Math.Min(pageSize, MaxScanLimit - totalScanned);
            IQueryable<RecordEntity> pageQuery = query;
            if (cursorReceivedAt.HasValue && cursorSeq.HasValue)
            {
                var cr = cursorReceivedAt.Value;
                var cs = cursorSeq.Value;
                pageQuery = pageQuery.Where(r => r.ReceivedAt > cr || (r.ReceivedAt == cr && r.Seq > cs));
            }

            var page = await pageQuery
                .OrderBy(r => r.ReceivedAt)
                .ThenBy(r => r.Seq)
                .Take(fetchCount)
                .ToListAsync(ct);

            if (page.Count == 0)
                break;

            totalScanned += page.Count;
            cursorReceivedAt = page[^1].ReceivedAt;
            cursorSeq = page[^1].Seq;

            var pageMatched = await FilterMatchedRecordsAsync(page, resolved.PolicyId, evalCtx, ct);
            foreach (var m in pageMatched)
            {
                matched.Add(m);
                if (matched.Count >= limit)
                    break;
            }
        }

        return matched;
    }

    public async Task<PurgePreview> PreviewAsync(
        string policyId,
        int limit = DefaultBatchLimit,
        CancellationToken ct = default)
    {
        var now = _clock();
        var resolved = await ResolvePolicyAsync(policyId, ct);
        var matched = await GetMatchedRecordsAsync(resolved, now, limit, ct);

        if (matched.Count == 0)
        {
            return new PurgePreview(
                resolved.PolicyId,
                resolved.Action,
                resolved.TargetId,
                MatchedCount: 0,
                MatchedBytes: 0,
                EstimatedFreedBytes: 0,
                OldestOccurredAt: null,
                NewestOccurredAt: null,
                MediaCount: 0);
        }

        var matchedIds = matched.Select(r => r.RecordId).ToList();
        var mediaCount = await _db.RecordMedia.AsNoTracking()
            .Where(rm => matchedIds.Contains(rm.RecordId))
            .Select(rm => rm.Hash)
            .Distinct()
            .CountAsync(ct);

        var bytes = matched.Sum(r => (long)r.PayloadSize);
        return new PurgePreview(
            resolved.PolicyId,
            resolved.Action,
            resolved.TargetId,
            MatchedCount: matched.Count,
            MatchedBytes: bytes,
            EstimatedFreedBytes: bytes,
            OldestOccurredAt: matched.Min(r => r.OccurredAt),
            NewestOccurredAt: matched.Max(r => r.OccurredAt),
            MediaCount: mediaCount);
    }

    public async Task<PurgeResult> ExecuteAsync(
        string policyId,
        Targets.IArchiveTarget? target = null,
        int limit = DefaultBatchLimit,
        CancellationToken ct = default)
    {
        var startedAt = _clock();
        var resolved = await ResolvePolicyAsync(policyId, ct);
        var matched = await GetMatchedRecordsAsync(resolved, startedAt, limit, ct);

        if (matched.Count == 0)
        {
            var noopRun = new ArchiveRunEntity
            {
                PolicyId = resolved.PolicyId,
                TargetId = target?.TargetId ?? resolved.TargetId ?? "none",
                Status = ArchiveRunStatus.NothingToDo,
                StartedAt = startedAt,
                FinishedAt = _clock(),
                MatchedCount = 0,
                DeletedCount = 0,
                FreedBytes = 0,
                MissingMedia = 0,
                ArchiveLocation = null,
                Sha256 = null,
                Error = null
            };
            _db.ArchiveRuns.Add(noopRun);
            await _db.SaveChangesAsync(ct);

            return new PurgeResult(
                Success: true,
                RunId: noopRun.RunId,
                Status: ArchiveRunStatus.NothingToDo,
                MatchedCount: 0,
                DeletedCount: 0,
                FreedBytes: 0,
                MissingMedia: 0,
                ArchiveLocation: null,
                Sha256: null,
                Error: null);
        }

        // ── 8-band: delete_only holati (arxivsiz o'chirish) ─────────────────────
        if (string.Equals(resolved.Action, RetentionActions.DeleteOnly, StringComparison.OrdinalIgnoreCase))
        {
            var targetKeys = matched.Select(r => new PurgeTargetKey(r.RecordId, r.ObservedAt, r.DeviceId)).ToList();
            var (deletedCount, freedBytes) = await DeleteRecordsAndCleanMediaAsync(targetKeys, startedAt, ct);

            var deleteRun = new ArchiveRunEntity
            {
                PolicyId = resolved.PolicyId,
                TargetId = "none",
                Status = ArchiveRunStatus.Completed,
                StartedAt = startedAt,
                FinishedAt = _clock(),
                MatchedCount = matched.Count,
                DeletedCount = deletedCount,
                FreedBytes = freedBytes,
                MissingMedia = 0,
                ArchiveLocation = null,
                Sha256 = null,
                Error = null
            };
            _db.ArchiveRuns.Add(deleteRun);
            await _db.SaveChangesAsync(ct);

            await _audit.WriteAsync("purge.completed", detail: new
            {
                policyId = resolved.PolicyId,
                action = RetentionActions.DeleteOnly,
                runId = deleteRun.RunId,
                matched = matched.Count,
                deleted = deletedCount,
                freed = freedBytes
            }, ct: ct);

            return new PurgeResult(
                Success: true,
                RunId: deleteRun.RunId,
                Status: ArchiveRunStatus.Completed,
                MatchedCount: matched.Count,
                DeletedCount: deletedCount,
                FreedBytes: freedBytes,
                MissingMedia: 0,
                ArchiveLocation: null,
                Sha256: null,
                Error: null);
        }

        // ── ArchiveThenDelete holati ───────────────────────────────────────────
        target ??= _targets.FirstOrDefault(t => t.TargetId == resolved.TargetId)
            ?? throw new InvalidOperationException($"Target '{resolved.TargetId}' topilmadi.");

        // Yozuvlarga bog'langan media ma'lumotlarini yuklaymiz
        var matchedIds = matched.Select(r => r.RecordId).ToList();
        var recordMediaList = await _db.RecordMedia.AsNoTracking()
            .Where(rm => matchedIds.Contains(rm.RecordId))
            .ToListAsync(ct);

        var hashes = recordMediaList.Select(rm => rm.Hash).Distinct().ToList();
        var blobs = await _db.MediaBlobs.AsNoTracking()
            .Where(b => hashes.Contains(b.Hash))
            .ToDictionaryAsync(b => b.Hash, ct);

        var mediaByRecordId = recordMediaList
            .GroupBy(rm => rm.RecordId)
            .ToDictionary(
                g => g.Key,
                g => g.Select(rm => blobs.TryGetValue(rm.Hash, out var b)
                    ? new MediaRef { Hash = b.Hash, Size = b.Size, Nonce = b.Nonce }
                    : null)
                    .Where(m => m != null)
                    .Select(m => m!)
                    .ToList());

        var syncRecords = new List<SyncRecord>(matched.Count);
        foreach (var r in matched)
        {
            var mediaRefs = mediaByRecordId.TryGetValue(r.RecordId, out var refs) ? refs : [];
            syncRecords.Add(new SyncRecord
            {
                RecordId = r.RecordId,
                Kind = r.Kind,
                AccountHash = r.AccountHash,
                PeerHash = r.PeerHash,
                MsgId = r.MsgId,
                OccurredAt = r.OccurredAt,
                ObservedAt = r.ObservedAt,
                DeviceId = r.DeviceId,
                Nonce = r.Nonce,
                Payload = r.Payload,
                TargetRecordId = r.TargetRecordId,
                Media = mediaRefs
            });
        }

        int missingMediaCount = 0;
        var mediaFilePaths = new Dictionary<string, string>();
        foreach (var hash in hashes)
        {
            if (blobs.TryGetValue(hash, out var blob) && File.Exists(blob.StoragePath))
            {
                mediaFilePaths[hash] = blob.StoragePath;
            }
            else
            {
                missingMediaCount++;
            }
        }

        var manifest = new CmxManifest
        {
            FormatVersion = CmxReader.SupportedFormatVersion,
            SourceApp = "customsync-server",
            DeviceId = "",
            CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            RecordCount = syncRecords.Count,
            Encrypted = true,
            KeyFingerprint = "",
            ScopeSince = syncRecords.Min(r => (long?)r.OccurredAt),
            ScopeUntil = syncRecords.Max(r => (long?)r.OccurredAt),
            ScopePeerHash = resolved.PeerHash
        };

        // 🔴 5-band: Arxiv xotiraga emas, vaqtinchalik faylga yoziladi
        var tempArchiveFile = Path.Combine(Path.GetTempPath(), $"customsync-purge-{Guid.NewGuid():N}.cmx");
        string expectedHash = "";
        try
        {
            try
            {
                await using (var fileStream = new FileStream(
                    tempArchiveFile, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 4096, useAsync: true))
                {
                    await CmxWriter.WriteAsync(fileStream, manifest, syncRecords, mediaFilePaths, ct);
                }

                // Yuborishdan OLDIN mahalliy fayl CmxReader bilan o'qib ko'riladi
                await using (var testRead = File.OpenRead(tempArchiveFile))
                {
                    var (_, readRecords, _) = await CmxReader.ReadAsync(testRead, readMedia: false, ct);
                    VerifyArchiveRecordCount(readRecords.Count, syncRecords.Count);
                }

                // Mahalliy fayldan SHA-256 hisoblanadi
                await using (var hashStream = File.OpenRead(tempArchiveFile))
                {
                    var hashBytes = await SHA256.HashDataAsync(hashStream, ct);
                    expectedHash = Convert.ToHexString(hashBytes).ToLowerInvariant();
                }
            }
            catch (Exception ex)
            {
                // 🔴 Minor 10: Arxiv yaratish yoki tekshirishda xatolik yuz bersa run ro'yxatga olinadi
                var failedRun = new ArchiveRunEntity
                {
                    PolicyId = resolved.PolicyId,
                    TargetId = target.TargetId,
                    Status = ArchiveRunStatus.FailedVerification,
                    StartedAt = startedAt,
                    FinishedAt = _clock(),
                    MatchedCount = matched.Count,
                    DeletedCount = 0,
                    FreedBytes = 0,
                    MissingMedia = missingMediaCount,
                    ArchiveLocation = null,
                    Sha256 = expectedHash,
                    Error = ex.Message
                };
                _db.ArchiveRuns.Add(failedRun);
                await _db.SaveChangesAsync(ct);

                await _audit.WriteAsync("purge.verification_failed", detail: new
                {
                    targetId = target.TargetId,
                    error = ex.Message
                }, ct: ct);

                return new PurgeResult(
                    Success: false,
                    RunId: failedRun.RunId,
                    Status: ArchiveRunStatus.FailedVerification,
                    MatchedCount: matched.Count,
                    DeletedCount: 0,
                    FreedBytes: 0,
                    MissingMedia: missingMediaCount,
                    ArchiveLocation: null,
                    Sha256: expectedHash,
                    Error: ex.Message);
            }

            // Target'ga yuborish
            var archiveFileName = $"customsync-purge-{DateTime.UtcNow:yyyyMMdd-HHmmss}.cmx";
            Targets.ArchiveUploadResult uploadResult;
            try
            {
                await using (var uploadStream = File.OpenRead(tempArchiveFile))
                {
                    uploadResult = await target.UploadAsync(archiveFileName, uploadStream, ct);
                }
            }
            catch (Exception ex)
            {
                // 🔴 Minor 10: Target istisno tashlasa run ro'yxatga olinadi
                var failedRun = new ArchiveRunEntity
                {
                    PolicyId = resolved.PolicyId,
                    TargetId = target.TargetId,
                    Status = ArchiveRunStatus.FailedUpload,
                    StartedAt = startedAt,
                    FinishedAt = _clock(),
                    MatchedCount = matched.Count,
                    DeletedCount = 0,
                    FreedBytes = 0,
                    MissingMedia = missingMediaCount,
                    ArchiveLocation = null,
                    Sha256 = expectedHash,
                    Error = $"Target exception: {ex.Message}"
                };
                _db.ArchiveRuns.Add(failedRun);
                await _db.SaveChangesAsync(ct);

                await _audit.WriteAsync("purge.upload_failed", detail: new
                {
                    targetId = target.TargetId,
                    error = ex.Message
                }, ct: ct);

                return new PurgeResult(
                    Success: false,
                    RunId: failedRun.RunId,
                    Status: ArchiveRunStatus.FailedUpload,
                    MatchedCount: matched.Count,
                    DeletedCount: 0,
                    FreedBytes: 0,
                    MissingMedia: missingMediaCount,
                    ArchiveLocation: null,
                    Sha256: expectedHash,
                    Error: failedRun.Error);
            }

            if (!uploadResult.Success)
            {
                var failedRun = new ArchiveRunEntity
                {
                    PolicyId = resolved.PolicyId,
                    TargetId = target.TargetId,
                    Status = ArchiveRunStatus.FailedUpload,
                    StartedAt = startedAt,
                    FinishedAt = _clock(),
                    MatchedCount = matched.Count,
                    DeletedCount = 0,
                    FreedBytes = 0,
                    MissingMedia = missingMediaCount,
                    ArchiveLocation = null,
                    Sha256 = expectedHash,
                    Error = $"Arxivni yuborib bo'lmadi: {uploadResult.Error}"
                };
                _db.ArchiveRuns.Add(failedRun);
                await _db.SaveChangesAsync(ct);

                await _audit.WriteAsync("purge.upload_failed", detail: new
                {
                    targetId = target.TargetId,
                    uploadResult.Error
                }, ct: ct);

                return new PurgeResult(
                    Success: false,
                    RunId: failedRun.RunId,
                    Status: ArchiveRunStatus.FailedUpload,
                    MatchedCount: matched.Count,
                    DeletedCount: 0,
                    FreedBytes: 0,
                    MissingMedia: missingMediaCount,
                    ArchiveLocation: null,
                    Sha256: expectedHash,
                    Error: failedRun.Error);
            }

            // 🔴 6-band: Target qaytargan Sha256 tekshiruvi (agar mavjud bo'lsa)
            if (!string.IsNullOrEmpty(uploadResult.Sha256) &&
                !string.Equals(uploadResult.Sha256, expectedHash, StringComparison.OrdinalIgnoreCase))
            {
                var failedRun = new ArchiveRunEntity
                {
                    PolicyId = resolved.PolicyId,
                    TargetId = target.TargetId,
                    Status = ArchiveRunStatus.FailedVerification,
                    StartedAt = startedAt,
                    FinishedAt = _clock(),
                    MatchedCount = matched.Count,
                    DeletedCount = 0,
                    FreedBytes = 0,
                    MissingMedia = missingMediaCount,
                    ArchiveLocation = uploadResult.Location,
                    Sha256 = expectedHash,
                    Error = "Verification failed: target qaytargan Sha256 mahalliy arxiv checksumiga mos kelmadi."
                };
                _db.ArchiveRuns.Add(failedRun);
                await _db.SaveChangesAsync(ct);

                await _audit.WriteAsync("purge.verification_failed", detail: new
                {
                    targetId = target.TargetId,
                    expectedHash,
                    returnedSha = uploadResult.Sha256
                }, ct: ct);

                return new PurgeResult(
                    Success: false,
                    RunId: failedRun.RunId,
                    Status: ArchiveRunStatus.FailedVerification,
                    MatchedCount: matched.Count,
                    DeletedCount: 0,
                    FreedBytes: 0,
                    MissingMedia: missingMediaCount,
                    ArchiveLocation: uploadResult.Location,
                    Sha256: expectedHash,
                    Error: failedRun.Error);
            }

            // 🔴 6-band: Target'dan qayta o'qib tekshirish
            await using var readBack = await target.DownloadAsync(uploadResult.Location!, ct);
            if (readBack is null)
            {
                var failedRun = new ArchiveRunEntity
                {
                    PolicyId = resolved.PolicyId,
                    TargetId = target.TargetId,
                    Status = ArchiveRunStatus.FailedVerification,
                    StartedAt = startedAt,
                    FinishedAt = _clock(),
                    MatchedCount = matched.Count,
                    DeletedCount = 0,
                    FreedBytes = 0,
                    MissingMedia = missingMediaCount,
                    ArchiveLocation = uploadResult.Location,
                    Sha256 = expectedHash,
                    Error = "Verification failed: target'dan arxivni qaytarib o'qib bo'lmadi (null)."
                };
                _db.ArchiveRuns.Add(failedRun);
                await _db.SaveChangesAsync(ct);

                await _audit.WriteAsync("purge.verification_failed", detail: new
                {
                    targetId = target.TargetId,
                    reason = "download_returned_null"
                }, ct: ct);

                return new PurgeResult(
                    Success: false,
                    RunId: failedRun.RunId,
                    Status: ArchiveRunStatus.FailedVerification,
                    MatchedCount: matched.Count,
                    DeletedCount: 0,
                    FreedBytes: 0,
                    MissingMedia: missingMediaCount,
                    ArchiveLocation: uploadResult.Location,
                    Sha256: expectedHash,
                    Error: failedRun.Error);
            }

            var readBackHashBytes = await SHA256.HashDataAsync(readBack, ct);
            var readBackHash = Convert.ToHexString(readBackHashBytes).ToLowerInvariant();
            if (!string.Equals(readBackHash, expectedHash, StringComparison.OrdinalIgnoreCase))
            {
                var failedRun = new ArchiveRunEntity
                {
                    PolicyId = resolved.PolicyId,
                    TargetId = target.TargetId,
                    Status = ArchiveRunStatus.FailedVerification,
                    StartedAt = startedAt,
                    FinishedAt = _clock(),
                    MatchedCount = matched.Count,
                    DeletedCount = 0,
                    FreedBytes = 0,
                    MissingMedia = missingMediaCount,
                    ArchiveLocation = uploadResult.Location,
                    Sha256 = expectedHash,
                    Error = "Verification failed: target'dan qayta o'qilgan arxiv SHA-256 mos kelmadi."
                };
                _db.ArchiveRuns.Add(failedRun);
                await _db.SaveChangesAsync(ct);

                await _audit.WriteAsync("purge.verification_failed", detail: new
                {
                    targetId = target.TargetId,
                    expectedHash,
                    readBackHash
                }, ct: ct);

                return new PurgeResult(
                    Success: false,
                    RunId: failedRun.RunId,
                    Status: ArchiveRunStatus.FailedVerification,
                    MatchedCount: matched.Count,
                    DeletedCount: 0,
                    FreedBytes: 0,
                    MissingMedia: missingMediaCount,
                    ArchiveLocation: uploadResult.Location,
                    Sha256: expectedHash,
                    Error: failedRun.Error);
            }

            // 🔴 7-band: RequiresExplicitConfirmation holati
            if (target.RequiresExplicitConfirmation)
            {
                var confirmRun = new ArchiveRunEntity
                {
                    PolicyId = resolved.PolicyId,
                    TargetId = target.TargetId,
                    Status = ArchiveRunStatus.AwaitingConfirmation,
                    StartedAt = startedAt,
                    FinishedAt = _clock(),
                    MatchedCount = matched.Count,
                    DeletedCount = 0,
                    FreedBytes = 0,
                    MissingMedia = missingMediaCount,
                    ArchiveLocation = uploadResult.Location,
                    Sha256 = expectedHash,
                    Error = null
                };
                _db.ArchiveRuns.Add(confirmRun);
                await _db.SaveChangesAsync(ct);

                await _audit.WriteAsync("purge.awaiting_confirmation", detail: new
                {
                    runId = confirmRun.RunId,
                    targetId = target.TargetId,
                    location = uploadResult.Location,
                    sha256 = expectedHash
                }, ct: ct);

                return new PurgeResult(
                    Success: true,
                    RunId: confirmRun.RunId,
                    Status: ArchiveRunStatus.AwaitingConfirmation,
                    MatchedCount: matched.Count,
                    DeletedCount: 0,
                    FreedBytes: 0,
                    MissingMedia: missingMediaCount,
                    ArchiveLocation: uploadResult.Location,
                    Sha256: expectedHash,
                    Error: null);
            }

            // Tekshiruv muvaffaqiyatli — endi o'chiramiz!
            var targetKeys = matched.Select(r => new PurgeTargetKey(r.RecordId, r.ObservedAt, r.DeviceId)).ToList();
            var (deletedCount, freedBytes) = await DeleteRecordsAndCleanMediaAsync(targetKeys, startedAt, ct);

            var completedRun = new ArchiveRunEntity
            {
                PolicyId = resolved.PolicyId,
                TargetId = target.TargetId,
                Status = ArchiveRunStatus.Completed,
                StartedAt = startedAt,
                FinishedAt = _clock(),
                MatchedCount = matched.Count,
                DeletedCount = deletedCount,
                FreedBytes = freedBytes,
                MissingMedia = missingMediaCount,
                ArchiveLocation = uploadResult.Location,
                Sha256 = expectedHash,
                Error = null
            };
            _db.ArchiveRuns.Add(completedRun);
            await _db.SaveChangesAsync(ct);

            await _audit.WriteAsync("purge.completed", detail: new
            {
                runId = completedRun.RunId,
                targetId = target.TargetId,
                location = uploadResult.Location,
                deleted = deletedCount,
                freed = freedBytes,
                sha256 = expectedHash
            }, ct: ct);

            return new PurgeResult(
                Success: true,
                RunId: completedRun.RunId,
                Status: ArchiveRunStatus.Completed,
                MatchedCount: matched.Count,
                DeletedCount: deletedCount,
                FreedBytes: freedBytes,
                MissingMedia: missingMediaCount,
                ArchiveLocation: uploadResult.Location,
                Sha256: expectedHash,
                Error: null);
        }
        finally
        {
            try
            {
                if (File.Exists(tempArchiveFile))
                {
                    File.Delete(tempArchiveFile);
                }
            }
            catch
            {
                // Tozalash xatosi e'tiborsiz qoldiriladi
            }
        }
    }

    public async Task<PurgeResult> ConfirmAsync(
        long runId,
        Targets.IArchiveTarget? target = null,
        CancellationToken ct = default)
    {
        var run = await _db.ArchiveRuns.FirstOrDefaultAsync(r => r.RunId == runId, ct)
            ?? throw new KeyNotFoundException($"Arxivlash jarayoni topilmadi: {runId}");

        if (!string.Equals(run.Status, ArchiveRunStatus.AwaitingConfirmation, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Run {runId} holati '{run.Status}', faqat '{ArchiveRunStatus.AwaitingConfirmation}' tasdiqlanishi mumkin.");
        }

        if (string.IsNullOrEmpty(run.ArchiveLocation))
        {
            throw new InvalidOperationException($"Run {runId} uchun arxiv joylashuvi (ArchiveLocation) mavjud emas.");
        }

        target ??= _targets.FirstOrDefault(t => t.TargetId == run.TargetId)
            ?? throw new InvalidOperationException($"Target '{run.TargetId}' topilmadi.");

        await using var downloadStream = await target.DownloadAsync(run.ArchiveLocation, ct);
        if (downloadStream is null)
        {
            throw new InvalidOperationException(
                $"Verification failed: target'dan arxivni o'qib bo'lmadi ({run.ArchiveLocation}).");
        }

        // 🔴 Minor 8: Agar oqim CanSeek bo'lmasa (masalan kelajakdagi tarmoq/S3 oqimlari),
        // vaqtinchalik faylga ko'chirib, o'sha fayldan hash va CmxReader bilan o'qiymiz.
        string? tempDownloadFile = null;
        Stream stream = downloadStream;
        if (!downloadStream.CanSeek)
        {
            tempDownloadFile = Path.Combine(Path.GetTempPath(), $"customsync-confirm-{Guid.NewGuid():N}.cmx");
            await using (var fs = new FileStream(tempDownloadFile, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 4096, useAsync: true))
            {
                await downloadStream.CopyToAsync(fs, ct);
            }
            stream = new FileStream(tempDownloadFile, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, useAsync: true);
        }

        try
        {
            // 🔴 7-band: SHA-256 tekshiruvi
            var hashBytes = await SHA256.HashDataAsync(stream, ct);
            var actualHash = Convert.ToHexString(hashBytes).ToLowerInvariant();
            if (!string.Equals(actualHash, run.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                await _audit.WriteAsync("purge.confirmation_failed", detail: new
                {
                    runId,
                    reason = "archive_altered",
                    expectedHash = run.Sha256,
                    actualHash
                }, ct: ct);

                return new PurgeResult(
                    Success: false,
                    RunId: run.RunId,
                    Status: run.Status,
                    MatchedCount: run.MatchedCount,
                    DeletedCount: 0,
                    FreedBytes: 0,
                    MissingMedia: run.MissingMedia,
                    ArchiveLocation: run.ArchiveLocation,
                    Sha256: run.Sha256,
                    Error: "Verification failed: arxiv o'zgartirilgan, SHA256 checksumi mos kelmadi.");
            }

            if (stream.CanSeek)
            {
                stream.Position = 0;
            }

            // Arxivdan yozuvlarni o'qiymiz
            var (_, records, _) = await CmxReader.ReadAsync(stream, readMedia: false, ct);
            var archiveKeys = records
                .Select(r => new PurgeTargetKey(r.RecordId, r.ObservedAt, r.DeviceId))
                .ToList();

            // 🔴 K1 tuzatish: Arxivdagi yozuvlarga mos keluvchi bazadagi joriy qatorlarni olib,
            // GetMatchedRecordsAsync dagi bilan AYNAN bir xil evaluator filtridan o'tkazamiz.
            // Agar tasdiqlash kutilgan oraliqda operator never_delete qo'shgan bo'lsa yoki
            // siyosat o'zgargan bo'lsa, u yozuvlar o'chirilmaydi!
            var archiveRecordIds = archiveKeys.Select(k => k.RecordId).Distinct().ToList();
            var currentDbRecords = await _db.Records.AsNoTracking()
                .Where(r => archiveRecordIds.Contains(r.RecordId))
                .ToListAsync(ct);

            var archiveKeySet = new HashSet<(string RecordId, long ObservedAt, string DeviceId)>(
                archiveKeys.Select(k => (k.RecordId, k.ObservedAt, k.DeviceId)));
            var candidateDbRecords = currentDbRecords
                .Where(r => archiveKeySet.Contains((r.RecordId, r.ObservedAt, r.DeviceId)))
                .ToList();

            var confirmEvalCtx = await LoadEvaluationContextAsync(_clock(), ct);
            var verifiedRecords = await FilterMatchedRecordsAsync(candidateDbRecords, run.PolicyId, confirmEvalCtx, ct);
            var targetKeys = verifiedRecords
                .Select(r => new PurgeTargetKey(r.RecordId, r.ObservedAt, r.DeviceId))
                .ToList();

            // 3-banddagi shart bilan o'chiramiz
            var (deletedCount, freedBytes) = await DeleteRecordsAndCleanMediaAsync(targetKeys, _clock(), ct);

            // 🔴 Minor 9: Holat o'tishini shartli va atomar qilamiz.
            // Parallel ConfirmAsync chaqiruvlari run'dagi deleted_count ni ustiga yozmasin.
            var connection = (NpgsqlConnection)_db.Database.GetDbConnection();
            if (connection.State != System.Data.ConnectionState.Open)
            {
                await connection.OpenAsync(ct);
            }

            const string updateRunSql = """
                UPDATE archive_runs
                SET status = @completedStatus,
                    deleted_count = @deletedCount,
                    freed_bytes = @freedBytes,
                    finished_at = @finishedAt
                WHERE run_id = @runId
                  AND status = @awaitingStatus;
                """;

            var finishedAt = _clock();
            await using (var cmd = new NpgsqlCommand(updateRunSql, connection))
            {
                cmd.Parameters.AddWithValue("completedStatus", ArchiveRunStatus.Completed);
                cmd.Parameters.AddWithValue("deletedCount", deletedCount);
                cmd.Parameters.AddWithValue("freedBytes", freedBytes);
                cmd.Parameters.AddWithValue("finishedAt", finishedAt);
                cmd.Parameters.AddWithValue("runId", run.RunId);
                cmd.Parameters.AddWithValue("awaitingStatus", ArchiveRunStatus.AwaitingConfirmation);

                var rowsUpdated = await cmd.ExecuteNonQueryAsync(ct);
                if (rowsUpdated == 0)
                {
                    throw new InvalidOperationException(
                        $"Run {runId} allaqachon boshqa parallel jarayon tomonidan tasdiqlangan yoki holati o'zgargan.");
                }
            }

            run.Status = ArchiveRunStatus.Completed;
            run.DeletedCount = deletedCount;
            run.FreedBytes = freedBytes;
            run.FinishedAt = finishedAt;

            await _audit.WriteAsync("purge.confirmed", detail: new
            {
                runId = run.RunId,
                deleted = deletedCount,
                freed = freedBytes
            }, ct: ct);

            return new PurgeResult(
                Success: true,
                RunId: run.RunId,
                Status: ArchiveRunStatus.Completed,
                MatchedCount: run.MatchedCount,
                DeletedCount: deletedCount,
                FreedBytes: freedBytes,
                MissingMedia: run.MissingMedia,
                ArchiveLocation: run.ArchiveLocation,
                Sha256: run.Sha256,
                Error: null);
        }
        finally
        {
            if (stream != downloadStream)
            {
                await stream.DisposeAsync();
            }

            if (tempDownloadFile != null)
            {
                try
                {
                    if (File.Exists(tempDownloadFile)) File.Delete(tempDownloadFile);
                }
                catch
                {
                    // Tozalash xatosi e'tiborsiz qoldiriladi
                }
            }
        }
    }

    /// <summary>
    /// K2 va Minor 11: 24 soatdan oshgan yetim bloblarni xavfsiz tozalash.
    ///
    /// Nega alohida tranzaksiya va nima uchun LOCK TABLE record_media IN SHARE MODE:
    /// Yozuvlarni o'chirish tranzaksiyasi uzoqroq davom etishi mumkin. Agar LOCK TABLE
    /// yozuvlar o'chirish tranzaksiyasi boshida olinsa, concurrent mijoz push'lari
    /// uzoq vaqt kutib qolardi.
    /// Shu sababli orphan sweep ALOHIDA, juda qisqa tranzaksiyada bajariladi.
    /// Boshida `LOCK TABLE record_media IN SHARE MODE` chaqiriladi -- bu faqat
    /// concurrent push'lar yangi record_media kiritishini bir necha millisoniyaga to'xtatib turadi,
    /// lekin o'qish (SELECT) so'rovlarini to'smaydi.
    /// O'chirish esa bitta atomar DELETE ... RETURNING so'rovi bilan bajariladi:
    /// agar oradagi fursatda ExistsAsync chaqirilgan bo'lsa (orphaned_at NULL) yoki
    /// yangi havola paydo bo'lsa (NOT EXISTS), blob o'chirilmaydi.
    /// </summary>
    public async Task<int> SweepOrphanedMediaAsync(DateTime now, CancellationToken ct = default)
    {
        var cutoff = now.AddHours(-24);
        var connection = (NpgsqlConnection)_db.Database.GetDbConnection();
        if (connection.State != System.Data.ConnectionState.Open)
        {
            await connection.OpenAsync(ct);
        }

        var filesToDelete = new List<string>();
        await using var tx = await connection.BeginTransactionAsync(ct);
        try
        {
            // Push'lar bilan poygani yopish uchun qisqa qulf
            const string lockSql = "LOCK TABLE record_media IN SHARE MODE;";
            await using (var cmd = new NpgsqlCommand(lockSql, connection, tx))
            {
                await cmd.ExecuteNonQueryAsync(ct);
            }

            // 🔴 K2: Bitta atomar so'rov: shartlar DELETE paytida qayta tekshiriladi
            const string deleteOldOrphansSql = """
                DELETE FROM media_blobs mb
                WHERE mb.orphaned_at IS NOT NULL
                  AND mb.orphaned_at <= @cutoff
                  AND NOT EXISTS (
                      SELECT 1 FROM record_media rm WHERE rm.hash = mb.hash
                  )
                RETURNING mb.storage_path;
                """;

            await using (var cmd = new NpgsqlCommand(deleteOldOrphansSql, connection, tx))
            {
                cmd.Parameters.AddWithValue("cutoff", cutoff);
                await using var reader = await cmd.ExecuteReaderAsync(ct);
                while (await reader.ReadAsync(ct))
                {
                    if (!reader.IsDBNull(0))
                    {
                        filesToDelete.Add(reader.GetString(0));
                    }
                }
            }

            await tx.CommitAsync(ct);
            _db.ChangeTracker.Clear();
        }
        catch
        {
            await tx.RollbackAsync(ct);
            throw;
        }

        // 🔴 4-band: Fayllar faqat tranzaksiya commit bo'lgandan KEYIN o'chiriladi
        int deletedFilesCount = 0;
        foreach (var file in filesToDelete)
        {
            try
            {
                if (File.Exists(file))
                {
                    File.Delete(file);
                    deletedFilesCount++;
                }
            }
            catch
            {
                // Fayl tizimi xatosi e'tiborsiz qoldiriladi
            }
        }

        return deletedFilesCount;
    }

    /// <summary>
    /// 3-band va 4-band: Aniq arxivlangan versiyani o'chirish va yetim media'ni xavfsiz boshqarish.
    /// </summary>
    private async Task<(int DeletedCount, long FreedBytes)> DeleteRecordsAndCleanMediaAsync(
        IReadOnlyList<PurgeTargetKey> targets,
        DateTime now,
        CancellationToken ct)
    {
        if (targets.Count == 0)
        {
            return (0, 0);
        }

        var connection = (NpgsqlConnection)_db.Database.GetDbConnection();
        if (connection.State != System.Data.ConnectionState.Open)
        {
            await connection.OpenAsync(ct);
        }

        var ids = targets.Select(t => t.RecordId).ToArray();
        var observedAts = targets.Select(t => t.ObservedAt).ToArray();
        var deviceIds = targets.Select(t => t.DeviceId).ToArray();

        var deletedIds = new List<string>();
        long freedBytes = 0;

        await using var tx = await connection.BeginTransactionAsync(ct);
        try
        {
            // 🔴 3-band: Faqat arxivlangan versiyani o'chirish (record_id, observed_at, device_id mosligi)
            const string deleteRecordsSql = """
                DELETE FROM records r
                USING (
                    SELECT * FROM UNNEST(@ids::text[], @observed_ats::bigint[], @device_ids::text[])
                ) AS v(record_id, observed_at, device_id)
                WHERE r.record_id = v.record_id
                  AND r.observed_at = v.observed_at
                  AND r.device_id = v.device_id
                RETURNING r.record_id, r.payload_size;
                """;

            await using (var cmd = new NpgsqlCommand(deleteRecordsSql, connection, tx))
            {
                cmd.Parameters.AddWithValue("ids", ids);
                cmd.Parameters.AddWithValue("observed_ats", observedAts);
                cmd.Parameters.AddWithValue("device_ids", deviceIds);

                await using var reader = await cmd.ExecuteReaderAsync(ct);
                while (await reader.ReadAsync(ct))
                {
                    deletedIds.Add(reader.GetString(0));
                    freedBytes += reader.GetInt32(1);
                }
            }

            if (deletedIds.Count > 0)
            {
                var actuallyDeletedIds = deletedIds.ToArray();

                // 🔴 4-band: Faqat shu purge'da o'chgan yozuvlarga bog'langan hash'lar nomzod
                var candidateMediaHashes = new List<string>();
                const string selectMediaSql = """
                    SELECT DISTINCT hash FROM record_media WHERE record_id = ANY(@deleted_ids::text[]);
                    """;
                await using (var cmd = new NpgsqlCommand(selectMediaSql, connection, tx))
                {
                    cmd.Parameters.AddWithValue("deleted_ids", actuallyDeletedIds);
                    await using var reader = await cmd.ExecuteReaderAsync(ct);
                    while (await reader.ReadAsync(ct))
                    {
                        candidateMediaHashes.Add(reader.GetString(0));
                    }
                }

                // record_media faqat HAQIQATAN o'chgan yozuvlar uchun o'chiriladi
                const string deleteRecordMediaSql = """
                    DELETE FROM record_media WHERE record_id = ANY(@deleted_ids::text[]);
                    """;
                await using (var cmd = new NpgsqlCommand(deleteRecordMediaSql, connection, tx))
                {
                    cmd.Parameters.AddWithValue("deleted_ids", actuallyDeletedIds);
                    await cmd.ExecuteNonQueryAsync(ct);
                }

                // 🔴 4-band: Blob darhol o'chirilmaydi: orphaned_at belgilanadi
                if (candidateMediaHashes.Count > 0)
                {
                    const string markOrphanedSql = """
                        UPDATE media_blobs mb
                        SET orphaned_at = @now
                        WHERE mb.hash = ANY(@candidate_hashes::text[])
                          AND mb.orphaned_at IS NULL
                          AND NOT EXISTS (
                              SELECT 1 FROM record_media rm WHERE rm.hash = mb.hash
                          );
                        """;
                    await using (var cmd = new NpgsqlCommand(markOrphanedSql, connection, tx))
                    {
                        cmd.Parameters.AddWithValue("candidate_hashes", candidateMediaHashes.ToArray());
                        cmd.Parameters.AddWithValue("now", now);
                        await cmd.ExecuteNonQueryAsync(ct);
                    }
                }
            }

            await tx.CommitAsync(ct);
            _db.ChangeTracker.Clear();
        }
        catch
        {
            await tx.RollbackAsync(ct);
            throw;
        }

        // 🔴 K2: 24 soatdan oshgan yetim bloblarni ALOHIDA qisqa tranzaksiyada tozalash
        await SweepOrphanedMediaAsync(now, ct);

        return (deletedIds.Count, freedBytes);
    }

    /// <summary>
    /// T3: Yuborishdan oldin mahalliy arxivdagi yozuvlar sonini solishtirish.
    /// </summary>
    public static void VerifyArchiveRecordCount(int actualCount, int expectedCount)
    {
        if (actualCount != expectedCount)
        {
            throw new InvalidDataException(
                $"Verification failed: arxiv yozuvlar soni mos kelmadi ({actualCount} != {expectedCount}).");
        }
    }
}
