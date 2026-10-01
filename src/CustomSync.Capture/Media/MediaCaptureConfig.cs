using CustomSync.Capture.Capture;
using Microsoft.Extensions.Configuration;

namespace CustomSync.Capture.Media;

public class MediaCaptureConfig
{
    public const long DefaultMaxBytes = 10485760; // 10 MiB
    public const int DefaultDownloadTimeoutSeconds = 120;
    public const int DefaultMaxAttempts = 5;

    public const string DefaultStorageDirectory = "/var/lib/customsync-capture/media";
    public const long DefaultMaxTotalBytes = 1073741824L; // 1 GiB
    public const long DefaultMinFreeBytes = 2147483648L; // 2 GiB

    public const int DefaultMaintenanceIntervalMinutes = 10;
    public const long DefaultTdlibFilesMaxBytes = 536870912L; // 512 MiB
    public const int DefaultTdlibFilesTtlHours = 24;
    public const int DefaultTdlibImmunitySeconds = 3600;
    public const int DefaultLogVerbosity = 1;

    public bool Enabled { get; init; } = false;
    public IReadOnlySet<string> PeerIds { get; init; } = new HashSet<string>();
    public long MaxBytes { get; init; } = DefaultMaxBytes;
    public int DownloadTimeoutSeconds { get; init; } = DefaultDownloadTimeoutSeconds;
    public int MaxAttempts { get; init; } = DefaultMaxAttempts;

    public string StorageDirectory { get; init; } = DefaultStorageDirectory;
    public long MaxTotalBytes { get; init; } = DefaultMaxTotalBytes;
    public long MinFreeBytes { get; init; } = DefaultMinFreeBytes;

    public int MaintenanceIntervalMinutes { get; init; } = DefaultMaintenanceIntervalMinutes;
    public long TdlibFilesMaxBytes { get; init; } = DefaultTdlibFilesMaxBytes;
    public int TdlibFilesTtlHours { get; init; } = DefaultTdlibFilesTtlHours;
    public int TdlibImmunitySeconds { get; init; } = DefaultTdlibImmunitySeconds;
    public int LogVerbosity { get; init; } = DefaultLogVerbosity;

    public static MediaCaptureConfig From(IConfiguration? config)
    {
        if (config is null)
        {
            return new MediaCaptureConfig();
        }

        bool enabled = false;
        var enabledStr = config["Capture:Media:Enabled"];
        if (!string.IsNullOrEmpty(enabledStr) && bool.TryParse(enabledStr, out var b))
        {
            enabled = b;
        }

        var peers = ScopeConfigReader.ReadPeerList(config, "Capture:Media:PeerIds")
            .Where(ScopeConfigReader.IsCanonicalPeerId);
        var peerSet = new HashSet<string>(peers, StringComparer.Ordinal);

        long maxBytes = DefaultMaxBytes;
        var maxBytesStr = config["Capture:Media:MaxBytes"];
        if (!string.IsNullOrEmpty(maxBytesStr) && long.TryParse(maxBytesStr, out var mb) && mb >= 1 && mb <= 26214400)
        {
            maxBytes = mb;
        }

        int timeout = DefaultDownloadTimeoutSeconds;
        var timeoutStr = config["Capture:Media:DownloadTimeoutSeconds"];
        if (!string.IsNullOrEmpty(timeoutStr) && int.TryParse(timeoutStr, out var to) && to > 0)
        {
            timeout = to;
        }

        int maxAttempts = DefaultMaxAttempts;
        var attemptsStr = config["Capture:Media:MaxAttempts"];
        if (!string.IsNullOrEmpty(attemptsStr) && int.TryParse(attemptsStr, out var ma) && ma > 0)
        {
            maxAttempts = ma;
        }

        var storageDir = config["Capture:Media:StorageDirectory"];
        if (string.IsNullOrWhiteSpace(storageDir))
        {
            storageDir = DefaultStorageDirectory;
        }

        long maxTotalBytes = DefaultMaxTotalBytes;
        var maxTotalStr = config["Capture:Media:MaxTotalBytes"];
        if (!string.IsNullOrEmpty(maxTotalStr) && long.TryParse(maxTotalStr, out var mtb) && mtb >= 1)
        {
            maxTotalBytes = mtb;
        }

        long minFreeBytes = DefaultMinFreeBytes;
        var minFreeStr = config["Capture:Storage:MinFreeBytes"];
        if (!string.IsNullOrEmpty(minFreeStr) && long.TryParse(minFreeStr, out var mfb) && mfb >= 0)
        {
            minFreeBytes = mfb;
        }

        int maintInterval = DefaultMaintenanceIntervalMinutes;
        var maintStr = config["Capture:Storage:MaintenanceIntervalMinutes"];
        if (!string.IsNullOrEmpty(maintStr) && int.TryParse(maintStr, out var mi) && mi >= 1 && mi <= 1440)
        {
            maintInterval = mi;
        }

        long tdlibMaxBytes = DefaultTdlibFilesMaxBytes;
        var tdlibMaxStr = config["Capture:Storage:TdlibFilesMaxBytes"];
        if (!string.IsNullOrEmpty(tdlibMaxStr) && long.TryParse(tdlibMaxStr, out var tmb) && tmb >= 16777216)
        {
            tdlibMaxBytes = tmb;
        }

        int tdlibTtlHours = DefaultTdlibFilesTtlHours;
        var tdlibTtlStr = config["Capture:Storage:TdlibFilesTtlHours"];
        if (!string.IsNullOrEmpty(tdlibTtlStr) && int.TryParse(tdlibTtlStr, out var tth) && tth >= 1 && tth <= 8760)
        {
            tdlibTtlHours = tth;
        }

        int tdlibImmunity = DefaultTdlibImmunitySeconds;
        var tdlibImmStr = config["Capture:Storage:TdlibImmunitySeconds"];
        if (!string.IsNullOrEmpty(tdlibImmStr) && int.TryParse(tdlibImmStr, out var tis) && tis >= 600)
        {
            tdlibImmunity = tis;
        }

        int logVerbosity = DefaultLogVerbosity;
        var verbStr = config["Capture:Tdlib:LogVerbosity"];
        if (!string.IsNullOrEmpty(verbStr) && int.TryParse(verbStr, out var lv) && lv >= 0 && lv <= 2)
        {
            logVerbosity = lv;
        }

        return new MediaCaptureConfig
        {
            Enabled = enabled,
            PeerIds = peerSet,
            MaxBytes = maxBytes,
            DownloadTimeoutSeconds = timeout,
            MaxAttempts = maxAttempts,
            StorageDirectory = storageDir,
            MaxTotalBytes = maxTotalBytes,
            MinFreeBytes = minFreeBytes,
            MaintenanceIntervalMinutes = maintInterval,
            TdlibFilesMaxBytes = tdlibMaxBytes,
            TdlibFilesTtlHours = tdlibTtlHours,
            TdlibImmunitySeconds = tdlibImmunity,
            LogVerbosity = logVerbosity
        };
    }
}
