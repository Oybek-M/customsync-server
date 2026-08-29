namespace CustomSync.Core.Interchange;

public sealed record CmxManifest
{
    public required int    FormatVersion  { get; init; }
    public required string SourceApp      { get; init; }
    public required string DeviceId       { get; init; }
    public required long   CreatedAt      { get; init; }
    public required int    RecordCount    { get; init; }
    public required bool   Encrypted      { get; init; }

    /// <summary>
    /// SHA256("customsync-fingerprint-v1" ‖ master_key)[0..8], hex.
    /// Import qilayotgan qurilma kalit mos kelishini OLDINDAN tekshiradi —
    /// aks holda foydalanuvchi yuzlab deshifrlash xatosini ko'radi.
    /// </summary>
    public required string KeyFingerprint { get; init; }

    public long?   ScopeSince    { get; init; }
    public long?   ScopeUntil    { get; init; }
    public string? ScopePeerHash { get; init; }
}
