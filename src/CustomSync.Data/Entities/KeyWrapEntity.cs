namespace CustomSync.Data.Entities;

public class KeyWrapEntity
{
    public string    WrapId     { get; set; } = null!;
    /// <summary>"passphrase" | "recovery" | "email"</summary>
    public string    WrapType   { get; set; } = null!;
    public string    Label      { get; set; } = null!;
    public byte[]    Salt       { get; set; } = null!;
    public byte[]    Nonce      { get; set; } = null!;
    public byte[]    WrappedKey { get; set; } = null!;
    public int       Iterations { get; set; }
    public DateTime  CreatedAt  { get; set; }
    public DateTime? LastUsedAt { get; set; }
}
