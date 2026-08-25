namespace CustomSync.Data.Entities;

public class RecordEntity
{
    /// <summary>Deterministik, hech qachon o'zgarmaydi. PRIMARY KEY.</summary>
    public string   RecordId    { get; set; } = null!;

    /// <summary>
    /// Cursor manbasi. sync_counter dan olinadi va yozuv yaxshiroq
    /// kuzatuv bilan almashtirilganda YANGILANADI — shuning uchun bu
    /// PRIMARY KEY emas.
    /// </summary>
    public long     Seq         { get; set; }

    public string   Kind        { get; set; } = null!;
    public string   PeerHash    { get; set; } = null!;

    /// <summary>Manfiy bo'lishi mumkin (spec §0.6) — BIGINT muammosiz.</summary>
    public long     MsgId       { get; set; }

    public long     OccurredAt  { get; set; }
    public long     ObservedAt  { get; set; }
    public string   DeviceId    { get; set; } = null!;
    public byte[]   Nonce       { get; set; } = null!;
    public byte[]   Payload     { get; set; } = null!;
    public int      PayloadSize { get; set; }
    public DateTime ReceivedAt  { get; set; }
}
