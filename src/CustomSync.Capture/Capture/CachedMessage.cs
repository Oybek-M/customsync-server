namespace CustomSync.Capture.Capture;

/// <summary>
/// Represents a message stored in the local SQLite cache.
/// message_id may be negative (avatar and story markers).
/// media_id holds TDLib's identifier only (no local filesystem paths).
/// text is nullable (media-only messages have no text).
/// </summary>
public sealed record CachedMessage(
    long ChatId,
    long MessageId,
    string? Text,
    string? SenderId,
    bool IsOut,
    bool IsMedia,
    string? MediaId,
    long Date,
    long? CachedAt = null);
