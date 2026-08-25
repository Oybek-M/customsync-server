namespace CustomSync.Data.Entities;

public class MediaBlobEntity
{
    /// <summary>SHA256 — ochiq matn ustidan (spec §0.5). Dedup kaliti.</summary>
    public string   Hash        { get; set; } = null!;
    public long     Size        { get; set; }
    public byte[]   Nonce       { get; set; } = null!;
    public string   StoragePath { get; set; } = null!;
    public DateTime UploadedAt  { get; set; }
}
