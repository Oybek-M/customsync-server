using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CustomSync.Capture.Capture;
using CustomSync.Core;
using CustomSync.Core.Contracts;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace CustomSync.Capture.Sync;

public class CaptureSyncRunner
{
    private readonly MessageCache _cache;
    private readonly CaptureSyncHttpClient _client;
    private readonly IConfiguration _config;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<CaptureSyncRunner>? _logger;

    private readonly SyncedScopeSettingsSource? _settingsSource;

    private byte[]? _masterKey;
    private DeviceState? _deviceState;
    private string? _accessToken;
    private long _tokenExpiresAt;
    private int _currentBatchSize;
    private int _cycleBackoffStep;
    private bool _isStopped;

    public bool IsEnabled => bool.TryParse(_config["Capture:Sync:Enabled"], out var enabled) && enabled;
    public bool IsStopped => _isStopped;
    public int PushedCount { get; private set; }
    public int DuplicateCount { get; private set; }
    public int ErrorCount { get; private set; }
    public int PoisonCount { get; private set; }
    public int PulledCount { get; private set; }
    public int SkippedCount { get; private set; }
    public int CycleBackoffSeconds { get; private set; }

    public const string PoisonedError = "poisoned: payload account_id/peer_id differ from the row (spec §0.14)";

    public CaptureSyncRunner(
        MessageCache cache,
        CaptureSyncHttpClient client,
        IConfiguration config,
        TimeProvider? timeProvider = null,
        ILogger<CaptureSyncRunner>? logger = null,
        SyncedScopeSettingsSource? settingsSource = null)
    {
        _cache = cache;
        _client = client;
        _config = config;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _logger = logger;
        _settingsSource = settingsSource;

        int configuredBatch = int.TryParse(_config["Capture:Sync:PushBatchSize"], out var b) && b > 0 ? b : 500;
        _currentBatchSize = configuredBatch;
    }

    private bool EnsureCredentialsLoaded()
    {
        var keyPath = _config["Capture:Sync:MasterKeyPath"] ?? "/var/lib/customsync-capture/master.key";
        var statePath = _config["Capture:Sync:StatePath"] ?? "/var/lib/customsync-capture/device-state.json";

        if (_masterKey == null)
        {
            _masterKey = DeviceCredentials.LoadMasterKey(keyPath, _logger);
            if (_masterKey == null)
            {
                return false;
            }
        }

        if (_deviceState == null)
        {
            _deviceState = DeviceCredentials.LoadDeviceState(statePath, _logger);
            if (_deviceState == null)
            {
                return false;
            }
        }

        return true;
    }

    // Token oqimini ikki oqim chaqiradi: sync sikli va maintenance'ning
    // health hisoboti (Task 9b). Refresh token har ishlatilganda almashadi,
    // shuning uchun parallel ikki refresh bir xil eski tokenni yuboradi:
    // ikkinchisi 401 oladi va sync "bekor qilingan" deb to'xtaydi, yoki
    // xotirada server bilmaydigan token qolib qurilmani qayta enroll qilish
    // kerak bo'ladi. Bir vaqtda faqat bitta chaqiruv tokenni yangilaydi.
    private readonly SemaphoreSlim _tokenGate = new(1, 1);

    private async Task<string?> EnsureAccessTokenAsync(bool forceRefresh = false, CancellationToken ct = default)
    {
        await _tokenGate.WaitAsync(ct);
        try
        {
            return await EnsureAccessTokenCoreAsync(forceRefresh, ct);
        }
        finally
        {
            _tokenGate.Release();
        }
    }

    private async Task<string?> EnsureAccessTokenCoreAsync(bool forceRefresh, CancellationToken ct)
    {
        if (_isStopped) return null;

        var serverUrl = _config["Capture:Sync:ServerUrl"];
        if (string.IsNullOrWhiteSpace(serverUrl)) return null;

        long now = _timeProvider.GetUtcNow().ToUnixTimeSeconds();

        if (forceRefresh || _accessToken == null || now >= _tokenExpiresAt - 60)
        {
            if (_deviceState == null) return null;

            var statePath = _config["Capture:Sync:StatePath"] ?? "/var/lib/customsync-capture/device-state.json";
            RefreshResult? refreshed;
            try
            {
                refreshed = await _client.RefreshAndPersistTokenAsync(
                    serverUrl, statePath, _deviceState, ct);
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Failed to refresh access token.");
                return null;
            }

            if (refreshed == null)
            {
                // Section 3: Before stopping on refresh 401, re-read state file once.
                // If stored refresh token differs from what was used, retry.
                var storedState = DeviceCredentials.LoadDeviceState(statePath, _logger);
                if (storedState != null && storedState.RefreshToken != _deviceState.RefreshToken)
                {
                    _deviceState = storedState;
                    try
                    {
                        refreshed = await _client.RefreshAndPersistTokenAsync(
                            serverUrl, statePath, _deviceState, ct);
                    }
                    catch (Exception ex)
                    {
                        _logger?.LogError(ex, "Failed to refresh access token on retry with newer state file.");
                        return null;
                    }
                }
            }

            if (refreshed == null)
            {
                _isStopped = true;
                _logger?.LogError("Device authorization revoked or refresh token invalid (HTTP 401). Sync stopped.");
                return null;
            }

            _deviceState = new DeviceState(_deviceState.DeviceId, refreshed.RefreshToken);

            if (string.IsNullOrEmpty(refreshed.AccessToken))
            {
                _accessToken = null;
                _logger?.LogError("Refresh response had no access token; will refresh again next cycle.");
                return null;
            }

            _accessToken = refreshed.AccessToken;
            _tokenExpiresAt = refreshed.ExpiresAt;
        }

        return _accessToken;
    }

    private static long CalculateRowBackoff(int attempt)
    {
        return Math.Min(300, 1L << Math.Clamp(attempt - 1, 0, 30));
    }

    public virtual async Task<bool> PushCycleAsync(CancellationToken ct = default)
    {
        if (!IsEnabled || _isStopped)
        {
            return false;
        }

        if (!EnsureCredentialsLoaded())
        {
            return false;
        }

        long now = _timeProvider.GetUtcNow().ToUnixTimeSeconds();
        var eligibleRows = _cache.GetEligibleOutboxRows(_currentBatchSize, now);
        if (eligibleRows.Count == 0)
        {
            return true;
        }

        var batchRows = new List<OutboxRow>();
        long totalPayloadBytes = 0;
        const long maxBatchBytes = 5_000_000; // ~5 MB JSON limit

        foreach (var r in eligibleRows)
        {
            int byteCount = Encoding.UTF8.GetByteCount(r.PayloadJson);
            if (batchRows.Count > 0 && totalPayloadBytes + byteCount > maxBatchBytes)
            {
                break;
            }
            batchRows.Add(r);
            totalPayloadBytes += byteCount;
        }

        string? serverUrl = null;
        string? token = null;

        var validRecords = new List<SyncRecord>();
        var rowMap = new Dictionary<string, OutboxRow>();
        var recordMap = new Dictionary<string, SyncRecord>();

        foreach (var row in batchRows)
        {
            if (!SyncCrypto.ValidateSection014(row.PayloadJson, row.AccountId, row.PeerId))
            {
                // Qator saqlanadi (ma'lumot tashlanmaydi), lekin navbatdan
                // chiqariladi: aks holda batch boshini egallab, ortidagi
                // hamma qatorni abadiy to'sib qo'yardi.
                PoisonCount++;
                _cache.MarkOutboxRowError(row.Id, PoisonedError, long.MaxValue);
                _logger?.LogError("Outbox row {Id} failed §0.14 validation: account_id or peer_id in payload_json does not match row. Row quarantined.", row.Id);
                continue;
            }

            var record = SyncCrypto.BuildRecord(row, _masterKey!, _deviceState!.DeviceId);

            // Plan 05 Task 8: o'chirilgan xabar media'si. Blob yozuvdan OLDIN
            // serverda bo'lishi shart (spec §5.3). Yozuv o'zgarmas (record_id
            // deterministik): bir marta mediasiz ketsa, media unga hech qachon
            // ulanmaydi — shuning uchun vaqtinchalik xatoda qator ushlab
            // turiladi. Fayl o'zi yaroqsiz bo'lsa esa matn kutib qolmaydi.
            if (row.Kind == "deleted")
            {
                var mediaRow = _cache.GetCapturedMedia(row.PeerId, row.MsgId);
                if (mediaRow is { Status: "downloaded" })
                {
                    serverUrl ??= _config["Capture:Sync:ServerUrl"];
                    token ??= await EnsureAccessTokenAsync(forceRefresh: false, ct);

                    var media = await PrepareMediaAsync(mediaRow, serverUrl, token, ct);
                    if (media.HoldReason is not null)
                    {
                        _cache.MarkOutboxRowError(row.Id, media.HoldReason, now + CalculateRowBackoff(row.RetryCount + 1));
                        continue;
                    }
                    if (media.Ref is not null)
                    {
                        record = record with { Media = new[] { media.Ref } };
                    }
                }
            }

            validRecords.Add(record);
            rowMap[record.RecordId] = row;
            recordMap[record.RecordId] = record;
        }

        if (validRecords.Count == 0)
        {
            return true;
        }

        serverUrl ??= _config["Capture:Sync:ServerUrl"];
        if (string.IsNullOrWhiteSpace(serverUrl))
        {
            return false;
        }

        token ??= await EnsureAccessTokenAsync(forceRefresh: false, ct);
        if (token == null)
        {
            return false;
        }

        if (validRecords.Count == 0)
        {
            return true;
        }

        var response = await _client.PushRecordsAsync(serverUrl, token, validRecords, ct);

        if (response.Status == PushStatus.Unauthorized)
        {
            // Token may have expired: refresh once and retry push once
            token = await EnsureAccessTokenAsync(forceRefresh: true, ct);
            if (token == null)
            {
                return false;
            }

            response = await _client.PushRecordsAsync(serverUrl, token, validRecords, ct);
        }

        if (response.Status == PushStatus.BatchTooLarge)
        {
            _currentBatchSize = Math.Max(1, _currentBatchSize / 2);
            if (response.MaxBatch.HasValue && _currentBatchSize > response.MaxBatch.Value)
            {
                _currentBatchSize = response.MaxBatch.Value;
            }
            _logger?.LogWarning("Server reported batch_too_large. Halved push batch size to {BatchSize}.", _currentBatchSize);
            return true;
        }

        if (response.Status == PushStatus.NetworkError || response.Status == PushStatus.ServerError)
        {
            _cycleBackoffStep++;
            CycleBackoffSeconds = (int)Math.Min(300, 1L << Math.Clamp(_cycleBackoffStep - 1, 0, 30));
            _logger?.LogWarning("Sync push encountered {Status}. Whole cycle backoff set to {Seconds}s.", response.Status, CycleBackoffSeconds);
            return false;
        }

        if (response.Status == PushStatus.Success && response.Results != null)
        {
            _cycleBackoffStep = 0;
            CycleBackoffSeconds = 0;

            var processedRecordIds = new HashSet<string>();
            foreach (var result in response.Results)
            {
                processedRecordIds.Add(result.RecordId);
                if (!rowMap.TryGetValue(result.RecordId, out var row))
                {
                    continue;
                }

                bool hasMedia = recordMap.TryGetValue(result.RecordId, out var pushedRec) && pushedRec.Media.Count > 0;
                if (result.Status == PushOutcome.Created)
                {
                    PushedCount++;
                    _cache.DeleteOutboxRowById(row.Id);
                    if (row.Kind == "deleted" && hasMedia)
                    {
                        _cache.UpdateMediaUploaded(row.PeerId, row.MsgId);
                    }
                }
                else if (result.Status == PushOutcome.Duplicate || result.Status == PushOutcome.Superseded)
                {
                    DuplicateCount++;
                    _cache.DeleteOutboxRowById(row.Id);
                    if (row.Kind == "deleted" && hasMedia)
                    {
                        _cache.UpdateMediaUploaded(row.PeerId, row.MsgId);
                    }
                }
                else
                {
                    // `error` yoki protokolda yo'q holat: qator qoladi va
                    // backoff bilan keyinroq qayta yuboriladi.
                    ErrorCount++;
                    var delay = CalculateRowBackoff(row.RetryCount + 1);
                    _cache.MarkOutboxRowError(row.Id, result.Message ?? "Server error", now + delay);
                }
            }

            // A record missing from response counts as error
            foreach (var rec in validRecords)
            {
                if (!processedRecordIds.Contains(rec.RecordId) && rowMap.TryGetValue(rec.RecordId, out var missingRow))
                {
                    ErrorCount++;
                    var delay = CalculateRowBackoff(missingRow.RetryCount + 1);
                    _cache.MarkOutboxRowError(missingRow.Id, "Missing from server response", now + delay);
                }
            }

            return true;
        }

        return false;
    }

    public virtual async Task<bool> PullCycleAsync(CancellationToken ct = default)
    {
        if (!IsEnabled || _isStopped)
        {
            return false;
        }

        if (!EnsureCredentialsLoaded())
        {
            return false;
        }

        int pullBatchSize = int.TryParse(_config["Capture:Sync:PullBatchSize"], out var pb) && pb > 0 ? pb : 500;
        int maxPages = int.TryParse(_config["Capture:Sync:MaxPullPagesPerCycle"], out var mp) && mp > 0 ? mp : 20;

        var serverUrl = _config["Capture:Sync:ServerUrl"];
        if (string.IsNullOrWhiteSpace(serverUrl))
        {
            return false;
        }

        var token = await EnsureAccessTokenAsync(forceRefresh: false, ct);
        if (token == null)
        {
            return false;
        }

        long cursor = _cache.GetPullCursor("pull_cursor");
        bool anySettingsChanged = false;

        var contentKey = SyncCrypto.DeriveContentKey(_masterKey!);
        var peerKey = SyncCrypto.DerivePeerKey(_masterKey!);
        var accountKey = SyncCrypto.DeriveAccountKey(_masterKey!);

        for (int page = 0; page < maxPages; page++)
        {
            var pullResult = await _client.PullRecordsAsync(serverUrl, token, cursor, pullBatchSize, kind: "setting", ct);

            if (pullResult.Status == PullStatus.Unauthorized)
            {
                token = await EnsureAccessTokenAsync(forceRefresh: true, ct);
                if (token == null)
                {
                    return false;
                }

                pullResult = await _client.PullRecordsAsync(serverUrl, token, cursor, pullBatchSize, kind: "setting", ct);
            }

            if (pullResult.Status == PullStatus.NetworkError ||
                pullResult.Status == PullStatus.ServerError ||
                pullResult.Status == PullStatus.BadJson)
            {
                _cycleBackoffStep++;
                CycleBackoffSeconds = (int)Math.Min(300, 1L << Math.Clamp(_cycleBackoffStep - 1, 0, 30));
                _logger?.LogWarning("Sync pull encountered {Status}. Cycle backoff set to {Seconds}s.", pullResult.Status, CycleBackoffSeconds);
                return false;
            }

            if (pullResult.Status != PullStatus.Success || pullResult.Response == null)
            {
                return false;
            }

            _cycleBackoffStep = 0;
            CycleBackoffSeconds = 0;

            var pageResp = pullResult.Response;
            var candidates = new List<SyncedSettingRow>();

            foreach (var record in pageResp.Records)
            {
                // 0. JSON'dagi null ("required" faqat maydon borligini tekshiradi):
                // tekshirilmasa NullReferenceException butun sahifani qayta-qayta yiqitadi.
                if (record is null || record.Kind is null || record.RecordId is null ||
                    record.AccountHash is null || record.PeerHash is null ||
                    record.Nonce is null || record.Payload is null)
                {
                    SkippedCount++;
                    _logger?.LogWarning("Pull record skipped: record or required field is null.");
                    continue;
                }

                // 1. kind == "setting"
                if (record.Kind != "setting")
                {
                    SkippedCount++;
                    _logger?.LogWarning("Pull record {RecordId} skipped: kind is not setting.", record.RecordId);
                    continue;
                }

                // 2. record_id recomputed matches
                var expectedRecordId = RecordId.Compute(record.Kind, record.AccountHash, record.PeerHash, record.MsgId, record.OccurredAt);
                if (record.RecordId != expectedRecordId)
                {
                    SkippedCount++;
                    _logger?.LogWarning("Pull record {RecordId} skipped: record_id mismatch.", record.RecordId);
                    continue;
                }

                // 3. payload decrypts with content key
                byte[] plaintext;
                try
                {
                    plaintext = SyncCrypto.DecryptPayload(contentKey, record.Nonce, record.Payload);
                }
                catch (Exception ex)
                {
                    SkippedCount++;
                    _logger?.LogWarning(ex, "Pull record {RecordId} skipped: payload decryption failed.", record.RecordId);
                    continue;
                }

                // 4. payload is {key, value, account_id, peer_id}, all strings;
                // peer_id == "0"; HMAC(peer_key, "0") == peer_hash; HMAC(account_key, account_id) == account_hash
                string key, value, accountId, peerId;
                try
                {
                    using var doc = JsonDocument.Parse(plaintext);
                    if (doc.RootElement.ValueKind != JsonValueKind.Object)
                    {
                        SkippedCount++;
                        _logger?.LogWarning("Pull record {RecordId} skipped: payload is not a JSON object.", record.RecordId);
                        continue;
                    }

                    var root = doc.RootElement;
                    if (!root.TryGetProperty("key", out var keyElem) || keyElem.ValueKind != JsonValueKind.String ||
                        !root.TryGetProperty("value", out var valElem) || valElem.ValueKind != JsonValueKind.String ||
                        !root.TryGetProperty("account_id", out var accElem) || accElem.ValueKind != JsonValueKind.String ||
                        !root.TryGetProperty("peer_id", out var peerElem) || peerElem.ValueKind != JsonValueKind.String)
                    {
                        SkippedCount++;
                        _logger?.LogWarning("Pull record {RecordId} skipped: payload fields missing or not strings.", record.RecordId);
                        continue;
                    }

                    key = keyElem.GetString()!;
                    value = valElem.GetString()!;
                    accountId = accElem.GetString()!;
                    peerId = peerElem.GetString()!;
                }
                catch (Exception ex)
                {
                    SkippedCount++;
                    _logger?.LogWarning(ex, "Pull record {RecordId} skipped: payload JSON parsing failed.", record.RecordId);
                    continue;
                }

                if (peerId != "0")
                {
                    SkippedCount++;
                    _logger?.LogWarning("Pull record {RecordId} skipped: peer_id is not '0'.", record.RecordId);
                    continue;
                }

                var expectedPeerHash = CryptoPrimitives.ComputePeerHash(peerKey, "0");
                if (record.PeerHash != expectedPeerHash)
                {
                    SkippedCount++;
                    _logger?.LogWarning("Pull record {RecordId} skipped: peer_hash mismatch.", record.RecordId);
                    continue;
                }

                var expectedAccountHash = CryptoPrimitives.ComputeAccountHash(accountKey, accountId);
                if (record.AccountHash != expectedAccountHash)
                {
                    SkippedCount++;
                    _logger?.LogWarning("Pull record {RecordId} skipped: account_hash mismatch (§0.14).", record.RecordId);
                    continue;
                }

                // 5. msg_id == ActivityMapper.DiscriminatorFor(key)
                long expectedMsgId = ActivityMapper.DiscriminatorFor(key);
                if (record.MsgId != expectedMsgId)
                {
                    SkippedCount++;
                    _logger?.LogWarning("Pull record {RecordId} skipped: msg_id discriminator mismatch.", record.RecordId);
                    continue;
                }

                // 6. key is one of the 11 scope keys of §3.2.1 (other setting keys are ignored silently — not an error)
                if (!SyncedScopeSettingsSource.AllScopeKeys.Contains(key))
                {
                    continue;
                }

                candidates.Add(new SyncedSettingRow(key, value, record.OccurredAt, record.RecordId));
            }

            long newCursor = pageResp.NextSince;
            bool changed = _cache.MergeSyncedSettingsAndCommitCursor(candidates, newCursor);
            if (changed)
            {
                anySettingsChanged = true;
            }

            PulledCount += candidates.Count;
            cursor = newCursor;
            _logger?.LogInformation("Sync pull page completed: {Count} setting candidate(s), next cursor {Cursor}.", candidates.Count, newCursor);

            if (!pageResp.HasMore)
            {
                break;
            }
        }

        if (anySettingsChanged && _settingsSource != null)
        {
            _settingsSource.RebuildSnapshotsFromStore();
        }

        return true;
    }

    public virtual async Task<bool> SyncCycleAsync(CancellationToken ct = default)
    {
        var pushOk = await PushCycleAsync(ct);
        if (!pushOk && CycleBackoffSeconds > 0)
        {
            return false;
        }

        var pullOk = await PullCycleAsync(ct);
        return pushOk && pullOk;
    }

    public virtual async Task<bool> ReportHealthAsync(
        CustomSync.Capture.Maintenance.StorageSnapshot snapshot,
        CancellationToken ct = default)
    {
        if (!IsEnabled || _isStopped)
        {
            return false;
        }

        try
        {
            // Fayl o'qish xatosi ham shu yerda ushlanadi: hisobot
            // maintenance'dan faqat bekor qilish bilan chiqadi.
            if (!EnsureCredentialsLoaded())
            {
                return false;
            }

            var serverUrl = _config["Capture:Sync:ServerUrl"];
            if (string.IsNullOrWhiteSpace(serverUrl))
            {
                return false;
            }

            var token = await EnsureAccessTokenAsync(forceRefresh: false, ct);
            if (token == null)
            {
                return false;
            }

            var status = await _client.PostHealthAsync(serverUrl, token, snapshot, ct);
            if (status == PostHealthStatus.Unauthorized)
            {
                token = await EnsureAccessTokenAsync(forceRefresh: true, ct);
                if (token == null)
                {
                    return false;
                }

                status = await _client.PostHealthAsync(serverUrl, token, snapshot, ct);
            }

            return status == PostHealthStatus.Success;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger?.LogWarning("Health report failed: {ExceptionType}", ex.GetType().Name);
            return false;
        }
    }

    private sealed record PreparedMedia(MediaRef? Ref, string? HoldReason);

    /// <summary>
    /// Media'ni yozuvga ulashga tayyorlaydi. Ref — ulanadigan havola;
    /// HoldReason — qatorni keyinroq qayta urinish uchun ushlab turish sababi;
    /// ikkalasi ham null — yozuv mediasiz ketadi (fayl yaroqsiz yoki juda katta).
    /// </summary>
    private async Task<PreparedMedia> PrepareMediaAsync(
        CapturedMediaRow mediaRow, string? serverUrl, string? token, CancellationToken ct)
    {
        // Fayl BIR marta o'qiladi va xesh shu baytlardan: ikki marta o'qilsa,
        // oradagi o'zgarish e'lon qilingan xeshga mos kelmaydigan blob yuklardi.
        byte[] plaintext;
        try
        {
            if (string.IsNullOrEmpty(mediaRow.LocalPath))
                throw new FileNotFoundException();
            plaintext = await File.ReadAllBytesAsync(mediaRow.LocalPath, ct);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Istisno matnida fayl yo'li (va nomi) bor — faqat turi yoziladi.
            _logger?.LogWarning("Captured media file is unreadable ({ErrorType}); pushing the record without media.",
                ex.GetType().Name);
            _cache.UpdateMediaFailed(mediaRow.PeerId, mediaRow.MsgId, mediaRow.Attempts, null);
            return new PreparedMedia(null, null);
        }

        var sha256 = Convert.ToHexString(SHA256.HashData(plaintext)).ToLowerInvariant();
        if (!string.Equals(sha256, mediaRow.Sha256, StringComparison.Ordinal))
        {
            // Yuklab olingandan keyin o'zgargan fayl — boshqa fayl; uni
            // o'chirilgan xabarga ulash yolg'on bo'lardi.
            _logger?.LogWarning("Captured media file changed after download ({Stored} -> {Actual}); pushing the record without media.",
                HashPrefix(mediaRow.Sha256), HashPrefix(sha256));
            _cache.UpdateMediaFailed(mediaRow.PeerId, mediaRow.MsgId, mediaRow.Attempts, null);
            return new PreparedMedia(null, null);
        }

        if (string.IsNullOrWhiteSpace(serverUrl) || token is null)
            return new PreparedMedia(null, "media upload: no server URL or token");

        HeadMediaResult head;
        try
        {
            head = await _client.HeadMediaAsync(serverUrl, token, sha256, ct);
        }
        catch (HttpRequestException)
        {
            return new PreparedMedia(null, "media HEAD: network error");
        }

        if (head.Unauthorized)
            return new PreparedMedia(null, "media HEAD: 401");
        if (head.Failed)
            return new PreparedMedia(null, "media HEAD: server error");

        if (head.Exists)
        {
            // Server dedup qiladi: blob boshqa qurilma nonce'i bilan saqlangan
            // bo'lishi mumkin, shuning uchun yozuvga FAQAT serverdagi nonce.
            var stored = TryDecodeNonce(head.NonceBase64);
            return stored is null
                ? new PreparedMedia(null, "media HEAD 200 without a valid X-Nonce")
                : new PreparedMedia(new MediaRef { Hash = sha256, Size = plaintext.Length, Nonce = stored }, null);
        }

        var (wire, nonce) = SyncCrypto.EncryptMedia(SyncCrypto.DeriveMediaKey(_masterKey!), plaintext);
        PutMediaResult put;
        try
        {
            put = await _client.PutMediaAsync(serverUrl, token, sha256, wire, Convert.ToBase64String(nonce), ct);
        }
        catch (HttpRequestException)
        {
            return new PreparedMedia(null, "media PUT: network error");
        }

        switch (put.Status)
        {
            case MediaUploadStatus.Success:
                return new PreparedMedia(new MediaRef { Hash = sha256, Size = plaintext.Length, Nonce = nonce }, null);
            case MediaUploadStatus.PayloadTooLarge:
                // Doimiy: server bu hajmni hech qachon qabul qilmaydi.
                _cache.UpdateMediaSkipped(mediaRow.PeerId, mediaRow.MsgId);
                return new PreparedMedia(null, null);
            case MediaUploadStatus.InsufficientStorage:
                return new PreparedMedia(null, "media PUT: 507 quota full"); // spec §0.9
            default:
                return new PreparedMedia(null, $"media PUT: {put.Status}");
        }
    }

    private static byte[]? TryDecodeNonce(string? base64)
    {
        if (string.IsNullOrEmpty(base64)) return null;
        try
        {
            var nonce = Convert.FromBase64String(base64);
            return nonce.Length == 12 ? nonce : null;
        }
        catch (FormatException)
        {
            return null;
        }
    }

    private static string HashPrefix(string? hash)
        => string.IsNullOrEmpty(hash) ? "-" : hash[..Math.Min(8, hash.Length)];
}
