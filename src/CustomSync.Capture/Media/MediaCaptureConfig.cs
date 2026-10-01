using CustomSync.Capture.Capture;
using Microsoft.Extensions.Configuration;

namespace CustomSync.Capture.Media;

public class MediaCaptureConfig
{
    public const long DefaultMaxBytes = 10485760; // 10 MiB
    public const int DefaultDownloadTimeoutSeconds = 120;
    public const int DefaultMaxAttempts = 5;

    public bool Enabled { get; init; } = false;
    public IReadOnlySet<string> PeerIds { get; init; } = new HashSet<string>();
    public long MaxBytes { get; init; } = DefaultMaxBytes;
    public int DownloadTimeoutSeconds { get; init; } = DefaultDownloadTimeoutSeconds;
    public int MaxAttempts { get; init; } = DefaultMaxAttempts;

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

        return new MediaCaptureConfig
        {
            Enabled = enabled,
            PeerIds = peerSet,
            MaxBytes = maxBytes,
            DownloadTimeoutSeconds = timeout,
            MaxAttempts = maxAttempts
        };
    }
}
