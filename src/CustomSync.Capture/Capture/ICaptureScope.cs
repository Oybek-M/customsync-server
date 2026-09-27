namespace CustomSync.Capture.Capture;

/// <summary>
/// Evaluates whether a chat should be cached or captured for deletions/edits.
/// Mirrors tdesktop's three distinct decisions:
/// - ShouldCache: ShouldBackgroundCache (storing new messages, updating cached text on edit, baseline fetch)
/// - ShouldAntiDelete: ShouldAntiDelete (emitting deleted rows)
/// - ShouldAntiEdit: ShouldAntiEdit (emitting edited rows)
/// </summary>
public interface ICaptureScope
{
    bool ShouldCache(string peerId);
    bool ShouldAntiDelete(string peerId);
    bool ShouldAntiEdit(string peerId);
}

/// <summary>
/// Fail-closed placeholder for ICaptureScope.
/// Captures and caches nothing.
/// </summary>
public class NoneCaptureScope : ICaptureScope
{
    public bool ShouldCache(string peerId) => false;
    public bool ShouldAntiDelete(string peerId) => false;
    public bool ShouldAntiEdit(string peerId) => false;
}
