using System.Text.Json;
using CustomSync.Capture.Capture;
using CustomSync.Capture.Media;
using CustomSync.Capture.Tdlib;
using Microsoft.Extensions.Logging;

namespace CustomSync.Capture.Maintenance;

public record StorageSnapshot(
    DateTimeOffset TakenAt,
    long ProcessRssBytes,
    long? MemoryLimitBytes,
    long CacheDatabaseBytes,
    long MediaStoreBytes,
    int MediaStoreFiles,
    long? TdlibFilesBytes,
    long? TdlibDatabaseBytes,
    long? FreeDiskBytes,
    long OptimizeFreedBytes,
    int OptimizeDeletedFiles,
    int MediaRowsPruned,
    int MediaFilesDeleted
);

public class StorageMaintenance
{
    private readonly MessageCache _messageCache;
    private readonly MediaStore _mediaStore;
    private readonly ITdClient _tdClient;
    private readonly IDiskSpaceProbe _diskSpaceProbe;
    private readonly MediaCaptureConfig _config;
    private readonly int _retentionDays;
    private readonly ILogger<StorageMaintenance>? _logger;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly TimeProvider _timeProvider;
    private readonly ICaptureHealthReporter? _reporter;
    private readonly string _procCgroupPath;
    private readonly string _sysFsCgroupRoot;

    private CancellationTokenSource? _cts;
    private Task? _loopTask;

    public StorageSnapshot? LatestSnapshot { get; private set; }

    public StorageMaintenance(
        MessageCache messageCache,
        MediaStore mediaStore,
        ITdClient tdClient,
        IDiskSpaceProbe diskSpaceProbe,
        MediaCaptureConfig config,
        int retentionDays = 30,
        ILogger<StorageMaintenance>? logger = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null,
        TimeProvider? timeProvider = null,
        string procCgroupPath = "/proc/self/cgroup",
        string sysFsCgroupRoot = "/sys/fs/cgroup",
        ICaptureHealthReporter? reporter = null)
    {
        _messageCache = messageCache;
        _mediaStore = mediaStore;
        _tdClient = tdClient;
        _diskSpaceProbe = diskSpaceProbe;
        _config = config;
        _retentionDays = retentionDays > 0 ? retentionDays : 30;
        _logger = logger;
        _delay = delay ?? Task.Delay;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _procCgroupPath = procCgroupPath;
        _sysFsCgroupRoot = sysFsCgroupRoot;
        _reporter = reporter;
    }

    public virtual void Start(CancellationToken ct = default)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _loopTask = Task.Run(() => RunLoopAsync(_cts.Token), _cts.Token);
    }

    public async Task RunLoopAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunOnceAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                // To'xtash (restart, deploy) xato emas.
                break;
            }
            catch (Exception ex)
            {
                // Har qadam o'z try/catch'ida; bu kutilmagan holat uchun oxirgi
                // to'siq — aks holda fon vazifasi jimgina o'lib, tozalash to'xtardi.
                _logger?.LogError("Storage maintenance run failed: {ExceptionType}", ex.GetType().Name);
            }

            try
            {
                await _delay(TimeSpan.FromMinutes(_config.MaintenanceIntervalMinutes), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger?.LogError("Storage maintenance delay failed: {ExceptionType}", ex.GetType().Name);
                break;
            }
        }
    }

    public async Task RunOnceAsync(CancellationToken ct = default)
    {
        int mediaRowsPruned = 0;
        int mediaFilesDeleted = 0;
        int sweptOrphans = 0;

        long now = _timeProvider.GetUtcNow().ToUnixTimeSeconds();

        // Step 1: Media retention (runs whether Media:Enabled is true or false)
        try
        {
            var retentionResult = _messageCache.ApplyMediaRetention(now, _retentionDays, _mediaStore, _logger);
            mediaRowsPruned = retentionResult.RowsPruned;
            mediaFilesDeleted = retentionResult.FilesDeleted;
        }
        catch (Exception ex)
        {
            _logger?.LogError("Storage maintenance media retention failed: {ExceptionType}", ex.GetType().Name);
        }

        // Step 2: Orphan sweep (runs whether Media:Enabled is true or false)
        try
        {
            sweptOrphans = _mediaStore.SweepOrphans(_messageCache, now, _logger);
        }
        catch (Exception ex)
        {
            _logger?.LogError("Storage maintenance orphan sweep failed: {ExceptionType}", ex.GetType().Name);
        }

        // Step 3: TDLib optimizeStorage
        long optimizeFreedBytes = 0;
        int optimizeDeletedFiles = 0;
        try
        {
            var optimizeReq = JsonSerializer.Serialize(new Dictionary<string, object?>
            {
                ["@type"] = "optimizeStorage",
                ["size"] = _config.TdlibFilesMaxBytes,
                ["ttl"] = (long)_config.TdlibFilesTtlHours * 3600,
                ["count"] = -1,
                ["immunity_delay"] = _config.TdlibImmunitySeconds,
                ["file_types"] = Array.Empty<object>(),
                ["chat_ids"] = Array.Empty<long>(),
                ["exclude_chat_ids"] = Array.Empty<long>(),
                ["return_deleted_file_statistics"] = true,
                ["chat_limit"] = 0
            });

            var responseJson = await _tdClient.SendAsync(optimizeReq, TimeSpan.FromSeconds(120), ct);
            using var doc = JsonDocument.Parse(responseJson);
            var root = doc.RootElement;
            if (root.TryGetProperty("@type", out var typeEl))
            {
                var typeStr = typeEl.GetString();
                if (string.Equals(typeStr, "storageStatistics", StringComparison.Ordinal))
                {
                    if (root.TryGetProperty("size", out var sEl) && sEl.TryGetInt64(out var s))
                    {
                        optimizeFreedBytes = s;
                    }
                    if (root.TryGetProperty("count", out var cEl) && cEl.TryGetInt32(out var c))
                    {
                        optimizeDeletedFiles = c;
                    }
                }
                else if (string.Equals(typeStr, "error", StringComparison.Ordinal))
                {
                    int code = root.TryGetProperty("code", out var codeEl) ? codeEl.GetInt32() : 0;
                    _logger?.LogWarning("TDLib optimizeStorage returned error code: {ErrorCode}", code);
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (TdException ex)
        {
            // Haqiqiy TdClient `error` javobini TdException'ga aylantiradi.
            _logger?.LogWarning("TDLib optimizeStorage returned error code: {ErrorCode}", ex.Code);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning("TDLib optimizeStorage failed: {ExceptionType}", ex.GetType().Name);
        }

        // Step 4: Measurement -> StorageSnapshot
        long? tdlibFilesBytes = null;
        long? tdlibDatabaseBytes = null;
        try
        {
            var statReq = JsonSerializer.Serialize(new Dictionary<string, object?> { ["@type"] = "getStorageStatisticsFast" });
            var statJson = await _tdClient.SendAsync(statReq, TimeSpan.FromSeconds(30), ct);
            using var doc = JsonDocument.Parse(statJson);
            var root = doc.RootElement;
            if (root.TryGetProperty("@type", out var typeEl) &&
                string.Equals(typeEl.GetString(), "storageStatisticsFast", StringComparison.Ordinal))
            {
                if (root.TryGetProperty("files_size", out var fsEl) && fsEl.TryGetInt64(out var fs))
                {
                    tdlibFilesBytes = fs;
                }
                if (root.TryGetProperty("database_size", out var dbEl) && dbEl.TryGetInt64(out var db))
                {
                    tdlibDatabaseBytes = db;
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger?.LogWarning("TDLib getStorageStatisticsFast failed: {ExceptionType}", ex.GetType().Name);
        }

        long rssBytes = Environment.WorkingSet;
        long? memLimit = ReadCgroupV2MemoryLimit(_procCgroupPath, _sysFsCgroupRoot);
        long cacheDbBytes = 0;
        try
        {
            cacheDbBytes = _messageCache.Stats().FileSizeBytes;
        }
        catch (Exception ex)
        {
            _logger?.LogWarning("Failed to get cache DB stats: {ExceptionType}", ex.GetType().Name);
        }

        _mediaStore.MeasureStore(out var storeBytes, out var storeFiles);
        long? freeDiskBytes = null;
        try
        {
            freeDiskBytes = _diskSpaceProbe.GetAvailableFreeBytes(_mediaStore.StorageDirectory);
        }
        catch (Exception ex)
        {
            // Istisno matnida yo'l bo'lishi mumkin — faqat turi.
            _logger?.LogWarning("Free disk space measurement failed: {ExceptionType}", ex.GetType().Name);
        }

        var snapshot = new StorageSnapshot(
            TakenAt: _timeProvider.GetUtcNow(),
            ProcessRssBytes: rssBytes,
            MemoryLimitBytes: memLimit,
            CacheDatabaseBytes: cacheDbBytes,
            MediaStoreBytes: storeBytes,
            MediaStoreFiles: storeFiles,
            TdlibFilesBytes: tdlibFilesBytes,
            TdlibDatabaseBytes: tdlibDatabaseBytes,
            FreeDiskBytes: freeDiskBytes,
            OptimizeFreedBytes: optimizeFreedBytes,
            OptimizeDeletedFiles: optimizeDeletedFiles,
            MediaRowsPruned: mediaRowsPruned,
            MediaFilesDeleted: mediaFilesDeleted
        );
        LatestSnapshot = snapshot;

        // Step 5: Summary log line & warnings (MB and counts only, no paths/IDs/filenames)
        static double ToMb(long bytes) => bytes / (1024.0 * 1024.0);
        static double? ToMbNullable(long? bytes) => bytes.HasValue ? bytes.Value / (1024.0 * 1024.0) : null;

        _logger?.LogInformation(
            "Storage maintenance snapshot: RSS={RssMB:F1}MB, CacheDB={CacheMB:F1}MB, Store={StoreMB:F1}MB ({StoreFiles} files), " +
            "TDLibFiles={TdFilesMB}MB, TDLibDB={TdDbMB}MB, FreeDisk={FreeMB}MB, PrunedRows={PrunedRows}, DeletedFiles={DeletedFiles}, SweptOrphans={SweptOrphans}, " +
            "OptimizeFreed={OptFreedMB:F1}MB ({OptFiles} files)",
            ToMb(snapshot.ProcessRssBytes),
            ToMb(snapshot.CacheDatabaseBytes),
            ToMb(snapshot.MediaStoreBytes),
            snapshot.MediaStoreFiles,
            ToMbNullable(snapshot.TdlibFilesBytes)?.ToString("F1") ?? "n/a",
            ToMbNullable(snapshot.TdlibDatabaseBytes)?.ToString("F1") ?? "n/a",
            ToMbNullable(snapshot.FreeDiskBytes)?.ToString("F1") ?? "n/a",
            snapshot.MediaRowsPruned,
            snapshot.MediaFilesDeleted,
            sweptOrphans,
            ToMb(snapshot.OptimizeFreedBytes),
            snapshot.OptimizeDeletedFiles
        );

        if (freeDiskBytes.HasValue && freeDiskBytes.Value < _config.MinFreeBytes)
        {
            _logger?.LogWarning(
                "Low free disk space: {FreeMB:F1}MB available, below configured minimum of {MinFreeMB:F1}MB",
                ToMb(freeDiskBytes.Value),
                ToMb(_config.MinFreeBytes));
        }

        if (memLimit.HasValue && memLimit.Value > 0 && rssBytes > 0.8 * memLimit.Value)
        {
            _logger?.LogWarning(
                "Process RSS high: {RssMB:F1}MB is {Percent:F1}% of memory limit {LimitMB:F1}MB",
                ToMb(rssBytes),
                (double)rssBytes / memLimit.Value * 100.0,
                ToMb(memLimit.Value));
        }

        // Step 6: Health report to backend
        if (_reporter != null)
        {
            try
            {
                if (ct.IsCancellationRequested)
                {
                    throw new OperationCanceledException(ct);
                }
                await _reporter.ReportAsync(snapshot, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger?.LogWarning("Storage maintenance health reporting failed: {ExceptionType}", ex.GetType().Name);
            }
        }
    }

    public static long? ReadCgroupV2MemoryLimit(
        string procCgroupPath = "/proc/self/cgroup",
        string sysFsCgroupRoot = "/sys/fs/cgroup")
    {
        try
        {
            if (!File.Exists(procCgroupPath))
            {
                return null;
            }

            string? cgroupPath = null;
            foreach (var line in File.ReadLines(procCgroupPath))
            {
                if (line.StartsWith("0::", StringComparison.Ordinal))
                {
                    cgroupPath = line.Substring(3).Trim();
                    break;
                }
            }

            if (cgroupPath == null)
            {
                return null;
            }

            string memoryMaxPath;
            if (string.IsNullOrEmpty(cgroupPath) || cgroupPath == "/")
            {
                memoryMaxPath = Path.Combine(sysFsCgroupRoot, "memory.max");
            }
            else
            {
                var relative = cgroupPath.TrimStart('/', '\\');
                memoryMaxPath = Path.Combine(sysFsCgroupRoot, relative, "memory.max");
            }

            if (!File.Exists(memoryMaxPath))
            {
                return null;
            }

            var text = File.ReadAllText(memoryMaxPath).Trim();
            if (string.Equals(text, "max", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            if (long.TryParse(text, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var limitBytes))
            {
                return limitBytes;
            }

            return null;
        }
        catch
        {
            return null;
        }
    }
}
