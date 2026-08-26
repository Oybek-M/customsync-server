namespace CustomSync.Core.Contracts;

/// <summary>
/// Kanonik yozuv. Aynan shu shakl uch joyda ishlatiladi:
/// HTTP sync payload, .cmx faylining records.jsonl satri, DB qatori.
/// </summary>
public sealed record SyncRecord
{
    public required string RecordId    { get; init; }
    public required string Kind        { get; init; }

    /// <summary>
    /// Spec §0.12. `""` (bo'sh satr) faqat `Kind == "activity"` uchun --
    /// last-seen bypass akkauntlar bo'ylab birlashadi. Boshqa hamma
    /// kind haqiqiy account_hash oladi.
    /// </summary>
    public required string AccountHash { get; init; }

    public required string PeerHash    { get; init; }

    /// <summary>
    /// Manfiy bo'lishi mumkin (spec §0.6): avatar `-photo_id`,
    /// story `-story_id`, skaner topgan fayl `-qHash(rel_path)-1`.
    /// </summary>
    public long            MsgId       { get; init; }

    public required long   OccurredAt  { get; init; }
    public required long   ObservedAt  { get; init; }
    public required string DeviceId    { get; init; }
    public required byte[] Nonce       { get; init; }
    public required byte[] Payload     { get; init; }
    public IReadOnlyList<MediaRef> Media { get; init; } = Array.Empty<MediaRef>();
}

public sealed record MediaRef
{
    /// <summary>SHA256 — ochiq matn ustidan, shifrlashdan OLDIN (spec §0.5).</summary>
    public required string Hash  { get; init; }
    public required long   Size  { get; init; }
    public required byte[] Nonce { get; init; }
}
