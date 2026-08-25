namespace CustomSync.Core.Contracts;

/// <summary>
/// Yozuv turlari. Bu satrlar simli protokolning bir qismi —
/// o'zgartirilsa barcha klientlar buziladi.
/// </summary>
public static class RecordKind
{
    public const string Deleted       = "deleted";
    public const string Edited        = "edited";
    public const string Activity      = "activity";
    public const string GhostRead     = "ghost_read";
    public const string Setting       = "setting";
    public const string PeerDirectory = "peer_directory";

    /// <summary>Media arxivining indeksi (spec §0.4).</summary>
    public const string MediaIndex    = "media_index";

    /// <summary>
    /// Ataylab o'chirish belgisi (spec §0.3). Retention tozalashi
    /// tombstone YARATMAYDI — u lokal qaror, global emas.
    /// </summary>
    public const string Tombstone     = "tombstone";

    public static readonly IReadOnlySet<string> All = new HashSet<string>
    {
        Deleted, Edited, Activity, GhostRead, Setting, PeerDirectory,
        MediaIndex, Tombstone
    };

    public static bool IsValid(string kind) => All.Contains(kind);
}
