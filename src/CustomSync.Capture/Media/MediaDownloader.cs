using System.Security.Cryptography;
using System.Text.Json;
using CustomSync.Capture.Capture;
using CustomSync.Capture.Tdlib;
using Microsoft.Extensions.Logging;

namespace CustomSync.Capture.Media;

public class MediaDownloader
{
    private readonly ITdClient _client;
    private readonly MessageCache _cache;
    private readonly MediaCaptureConfig _config;
    private readonly MediaStore _mediaStore;
    private readonly IDiskSpaceProbe _diskSpaceProbe;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<MediaDownloader>? _logger;

    private DateTimeOffset? _lastDiskGuardWarningTime;
    private bool _warnedUnknownFreeSpace;

    private CancellationTokenSource? _cts;
    private Task? _loopTask;

    public MediaDownloader(
        ITdClient client,
        MessageCache cache,
        MediaCaptureConfig config,
        MediaStore mediaStore,
        IDiskSpaceProbe diskSpaceProbe,
        TimeProvider? timeProvider = null,
        ILogger<MediaDownloader>? logger = null)
    {
        _client = client;
        _cache = cache;
        _config = config;
        _mediaStore = mediaStore;
        _diskSpaceProbe = diskSpaceProbe;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _logger = logger;
    }

    public MediaDownloader(
        ITdClient client,
        MessageCache cache,
        MediaCaptureConfig config,
        TimeProvider? timeProvider = null,
        ILogger<MediaDownloader>? logger = null)
        : this(client, cache, config, new MediaStore(config.StorageDirectory), new AmpleDiskSpaceProbe(), timeProvider, logger)
    {
    }

    private class AmpleDiskSpaceProbe : IDiskSpaceProbe
    {
        public long? GetAvailableFreeBytes(string path) => 100L * 1024 * 1024 * 1024; // 100 GB
    }

    public virtual void Start(CancellationToken ct = default)
    {
        if (!_config.Enabled)
        {
            return;
        }

        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _loopTask = Task.Run(() => RunLoopAsync(_cts.Token), _cts.Token);
    }

    private async Task RunLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                bool processed = await ProcessPendingOnceAsync(ct);
                if (!processed)
                {
                    await Task.Delay(500, ct);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // Istisno matnida fayl yo'li bo'lishi mumkin — faqat turi.
                _logger?.LogError("Unexpected error in media downloader loop ({ErrorType}).", ex.GetType().Name);
                await Task.Delay(1000, ct);
            }
        }
    }

    public virtual async Task<bool> ProcessPendingOnceAsync(CancellationToken ct = default)
    {
        if (!_config.Enabled)
            return false;

        long now = _timeProvider.GetUtcNow().ToUnixTimeSeconds();
        var row = _cache.GetNextDuePendingMedia(now);
        if (row == null)
            return false;

        await ProcessRowAsync(row, now, ct);
        return true;
    }

    private async Task ProcessRowAsync(CapturedMediaRow row, long now, CancellationToken ct)
    {
        // Disk guard, checked before any TDLib request for the row
        long rowSize = row.Size.HasValue && row.Size.Value > 0 ? row.Size.Value : 0;
        long? freeBytes = _diskSpaceProbe.GetAvailableFreeBytes(_mediaStore.StorageDirectory);
        if (!freeBytes.HasValue && !_warnedUnknownFreeSpace)
        {
            _warnedUnknownFreeSpace = true;
            _logger?.LogWarning("Cannot determine free disk space for media store filesystem.");
        }

        bool diskTooFull = freeBytes.HasValue && (freeBytes.Value - rowSize) < _config.MinFreeBytes;
        long currentDownloadedTotal = _cache.GetDownloadedMediaTotalSize();
        bool budgetExceeded = (currentDownloadedTotal + rowSize) > _config.MaxTotalBytes;

        if (diskTooFull || budgetExceeded)
        {
            // Defer the row by 300 seconds without changing attempts
            _cache.DeferMediaRow(row.PeerId, row.MsgId, now + 300);

            var nowUtc = _timeProvider.GetUtcNow();
            if (_lastDiskGuardWarningTime == null || (nowUtc - _lastDiskGuardWarningTime.Value) >= TimeSpan.FromMinutes(10))
            {
                _lastDiskGuardWarningTime = nowUtc;
                if (diskTooFull)
                {
                    _logger?.LogWarning(
                        "Deferring media download due to low disk space (free: {FreeMB:F1}MB, needed min: {MinFreeMB:F1}MB).",
                        freeBytes!.Value / (1024.0 * 1024.0), _config.MinFreeBytes / (1024.0 * 1024.0));
                }
                else
                {
                    _logger?.LogWarning(
                        "Deferring media download due to media total size budget (current: {CurrentMB:F1}MB, max: {MaxMB:F1}MB).",
                        currentDownloadedTotal / (1024.0 * 1024.0), _config.MaxTotalBytes / (1024.0 * 1024.0));
                }
            }
            return;
        }

        try
        {
            var req = JsonSerializer.Serialize(new Dictionary<string, object?>
            {
                ["@type"] = "getMessage",
                ["chat_id"] = row.ChatId,
                ["message_id"] = row.MessageId
            });

            var resp = await _client.SendAsync(req, TimeSpan.FromSeconds(15), ct);
            using var doc = JsonDocument.Parse(resp);
            var root = doc.RootElement;

            if (root.TryGetProperty("@type", out var typeProp) && typeProp.GetString() == "error")
            {
                if (root.TryGetProperty("code", out var codeProp) && codeProp.TryGetInt32(out var code) && code == 404)
                {
                    _cache.UpdateMediaFailed(row.PeerId, row.MsgId, row.Attempts + 1, null);
                    return;
                }
                int attempts = row.Attempts + 1;
                if (attempts >= _config.MaxAttempts)
                {
                    _cache.UpdateMediaFailed(row.PeerId, row.MsgId, attempts, null);
                }
                else
                {
                    long nextAttempt = now + CalculateBackoff(attempts);
                    _cache.UpdateMediaFailed(row.PeerId, row.MsgId, attempts, nextAttempt);
                }
                return;
            }

            if (!root.TryGetProperty("content", out var contentElem) ||
                !MediaExtractor.TryGetSelectedFileElement(row.ContentType, contentElem, _config.MaxBytes, out var fileElem))
            {
                _cache.UpdateMediaSkipped(row.PeerId, row.MsgId);
                return;
            }

            string? localPath = null;
            bool isCompleted = false;

            if (MediaExtractor.TryGetLocalFile(fileElem, out var path, out var comp))
            {
                localPath = path;
                isCompleted = comp;
            }

            if (!isCompleted || string.IsNullOrEmpty(localPath) || !File.Exists(localPath))
            {
                if (!fileElem.TryGetProperty("id", out var idProp) || !idProp.TryGetInt32(out var fileId) || fileId <= 0)
                {
                    _cache.UpdateMediaSkipped(row.PeerId, row.MsgId);
                    return;
                }

                var dlReq = JsonSerializer.Serialize(new Dictionary<string, object?>
                {
                    ["@type"] = "downloadFile",
                    ["file_id"] = fileId,
                    ["priority"] = 1,
                    ["offset"] = 0,
                    ["limit"] = 0,
                    ["synchronous"] = true
                });

                var dlResp = await _client.SendAsync(dlReq, TimeSpan.FromSeconds(_config.DownloadTimeoutSeconds), ct);
                using var dlDoc = JsonDocument.Parse(dlResp);
                var dlRoot = dlDoc.RootElement;

                if (dlRoot.TryGetProperty("@type", out var dlType) && dlType.GetString() == "file")
                {
                    if (MediaExtractor.TryGetLocalFile(dlRoot, out var newPath, out var newComp))
                    {
                        localPath = newPath;
                        isCompleted = newComp;
                    }
                }
            }

            if (isCompleted && !string.IsNullOrEmpty(localPath) && File.Exists(localPath))
            {
                var fi = new FileInfo(localPath);
                if (fi.Length > _config.MaxBytes)
                {
                    _cache.UpdateMediaSkipped(row.PeerId, row.MsgId);
                    return;
                }

                try
                {
                    var (storePath, sha256, copiedSize) = await _mediaStore.CopyInAsync(row.PeerId, row.MsgId, localPath, _config.MaxBytes, ct);
                    _cache.UpdateMediaDownloaded(row.PeerId, row.MsgId, storePath, sha256, copiedSize);
                }
                catch (MediaFileTooLargeException)
                {
                    _logger?.LogInformation("Media file exceeded MaxBytes during copy; marked skipped.");
                    _cache.UpdateMediaSkipped(row.PeerId, row.MsgId);
                    return;
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _logger?.LogError("Failed to copy media into store ({ErrorType}).", ex.GetType().Name);
                    int attempts = row.Attempts + 1;
                    if (attempts >= _config.MaxAttempts)
                    {
                        _cache.UpdateMediaFailed(row.PeerId, row.MsgId, attempts, null);
                    }
                    else
                    {
                        long nextAttempt = now + CalculateBackoff(attempts);
                        _cache.UpdateMediaFailed(row.PeerId, row.MsgId, attempts, nextAttempt);
                    }
                }
            }
            else
            {
                int attempts = row.Attempts + 1;
                if (attempts >= _config.MaxAttempts)
                {
                    _cache.UpdateMediaFailed(row.PeerId, row.MsgId, attempts, null);
                }
                else
                {
                    long nextAttempt = now + CalculateBackoff(attempts);
                    _cache.UpdateMediaFailed(row.PeerId, row.MsgId, attempts, nextAttempt);
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // To'xtash (restart, deploy) urinish emas: qator `pending` qoladi
            // va keyingi ishga tushishda qaytadan olinadi.
            throw;
        }
        catch (TdException ex) when (ex.Code == 404)
        {
            // Haqiqiy TdClient TDLib xatosini istisnoga aylantiradi: xabar
            // endi yo'q — qayta urinishning ma'nosi yo'q.
            _logger?.LogInformation("Message is gone; media download given up.");
            _cache.UpdateMediaFailed(row.PeerId, row.MsgId, row.Attempts + 1, null);
        }
        catch (Exception ex)
        {
            // Istisno matnida fayl yo'li (va nomi) bo'lishi mumkin — faqat turi.
            _logger?.LogError("Error processing media download ({ErrorType}).", ex.GetType().Name);
            int attempts = row.Attempts + 1;
            if (attempts >= _config.MaxAttempts)
            {
                _cache.UpdateMediaFailed(row.PeerId, row.MsgId, attempts, null);
            }
            else
            {
                long nextAttempt = now + CalculateBackoff(attempts);
                _cache.UpdateMediaFailed(row.PeerId, row.MsgId, attempts, nextAttempt);
            }
        }
    }

    private static long CalculateBackoff(int attempt)
    {
        return Math.Min(300, 1L << Math.Clamp(attempt - 1, 0, 8));
    }
}
