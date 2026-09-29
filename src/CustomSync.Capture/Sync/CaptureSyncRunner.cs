using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using CustomSync.Capture.Capture;
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
    public int CycleBackoffSeconds { get; private set; }

    public const string PoisonedError = "poisoned: payload account_id/peer_id differ from the row (spec §0.14)";

    public CaptureSyncRunner(
        MessageCache cache,
        CaptureSyncHttpClient client,
        IConfiguration config,
        TimeProvider? timeProvider = null,
        ILogger<CaptureSyncRunner>? logger = null)
    {
        _cache = cache;
        _client = client;
        _config = config;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _logger = logger;

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

    private async Task<string?> EnsureAccessTokenAsync(bool forceRefresh = false, CancellationToken ct = default)
    {
        if (_isStopped) return null;

        var serverUrl = _config["Capture:Sync:ServerUrl"];
        if (string.IsNullOrWhiteSpace(serverUrl)) return null;

        long now = _timeProvider.GetUtcNow().ToUnixTimeSeconds();

        if (forceRefresh || _accessToken == null || now >= _tokenExpiresAt - 60)
        {
            if (_deviceState == null) return null;

            RefreshResult? refreshed;
            try
            {
                refreshed = await _client.RefreshTokenAsync(
                    serverUrl, _deviceState.DeviceId, _deviceState.RefreshToken, ct);
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Failed to refresh access token.");
                return null;
            }

            if (refreshed == null)
            {
                _isStopped = true;
                _logger?.LogError("Device authorization revoked or refresh token invalid (HTTP 401). Sync stopped.");
                return null;
            }

            // CRITICAL: Persist rotated refresh token to disk BEFORE using new access token!
            var statePath = _config["Capture:Sync:StatePath"] ?? "/var/lib/customsync-capture/device-state.json";
            _deviceState = new DeviceState(_deviceState.DeviceId, refreshed.RefreshToken);
            DeviceCredentials.SaveDeviceState(statePath, _deviceState);

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

        var validRecords = new List<SyncRecord>();
        var rowMap = new Dictionary<string, OutboxRow>();

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
            validRecords.Add(record);
            rowMap[record.RecordId] = row;
        }

        if (validRecords.Count == 0)
        {
            return true;
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

                if (result.Status == PushOutcome.Created)
                {
                    PushedCount++;
                    _cache.DeleteOutboxRowById(row.Id);
                }
                else if (result.Status == PushOutcome.Duplicate || result.Status == PushOutcome.Superseded)
                {
                    DuplicateCount++;
                    _cache.DeleteOutboxRowById(row.Id);
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
}
