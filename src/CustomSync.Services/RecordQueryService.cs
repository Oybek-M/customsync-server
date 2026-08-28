using CustomSync.Data;
using Microsoft.EntityFrameworkCore;

namespace CustomSync.Services;

public sealed record RecordQuery
{
    /// <summary>
    /// So'rov boshlanganda olingan max(seq). Barcha sahifalar shu
    /// nuqtadan oldingi holatni ko'radi, shuning uchun sahifalash
    /// davomida kelgan yangi yozuvlar qatorlarni surib yubormaydi.
    /// </summary>
    public required long Snapshot   { get; init; }
    public int   Limit              { get; init; } = 50;
    public bool  Descending         { get; init; } = true;
    public long? AfterKey           { get; init; }
    public long? AfterSeq           { get; init; }
    public string? PeerHash         { get; init; }
    public string? Kind             { get; init; }
    public long? FromOccurredAt     { get; init; }
    public long? ToOccurredAt       { get; init; }
}

public sealed record RecordSummary
{
    public required long   Seq         { get; init; }
    public required string RecordId    { get; init; }
    public required string Kind        { get; init; }
    public required string PeerHash    { get; init; }
    public required long   MsgId       { get; init; }
    public required long   OccurredAt  { get; init; }
    public required long   ObservedAt  { get; init; }
    public required string DeviceId    { get; init; }
    public required int    PayloadSize { get; init; }
}

/// <summary>
/// Web app uchun so'rovlar. OFFSET ishlatilmaydi (qoida K3): u sync
/// paytida qatorlarni surib yuboradi va dublikat yoki tushib qolgan
/// qator beradi.
/// </summary>
public class RecordQueryService(SyncDbContext db)
{
    public async Task<long> CurrentSnapshotAsync(CancellationToken ct = default)
        => await db.Records.MaxAsync(r => (long?)r.Seq, ct) ?? 0L;

    public async Task<IReadOnlyList<RecordSummary>> QueryAsync(
        RecordQuery query, CancellationToken ct = default)
    {
        var q = db.Records.AsNoTracking().Where(r => r.Seq <= query.Snapshot);

        if (query.PeerHash is not null)       q = q.Where(r => r.PeerHash == query.PeerHash);
        if (query.Kind is not null)           q = q.Where(r => r.Kind == query.Kind);
        if (query.FromOccurredAt is not null) q = q.Where(r => r.OccurredAt >= query.FromOccurredAt);
        if (query.ToOccurredAt is not null)   q = q.Where(r => r.OccurredAt <= query.ToOccurredAt);

        // Keyset: (occurred_at, seq) juftligi bo'yicha qat'iy taqqoslash.
        // seq tiebreaker sifatida zarur — bir xil occurred_at li qatorlar
        // aks holda cheksiz siklga yoki tushib qolishga olib keladi.
        if (query.AfterKey is not null && query.AfterSeq is not null)
        {
            var key = query.AfterKey.Value;
            var seq = query.AfterSeq.Value;
            q = query.Descending
                ? q.Where(r => r.OccurredAt < key
                            || (r.OccurredAt == key && r.Seq < seq))
                : q.Where(r => r.OccurredAt > key
                            || (r.OccurredAt == key && r.Seq > seq));
        }

        q = query.Descending
            ? q.OrderByDescending(r => r.OccurredAt).ThenByDescending(r => r.Seq)
            : q.OrderBy(r => r.OccurredAt).ThenBy(r => r.Seq);

        return await q.Take(query.Limit)
            .Select(r => new RecordSummary
            {
                Seq         = r.Seq,
                RecordId    = r.RecordId,
                Kind        = r.Kind,
                PeerHash    = r.PeerHash,
                MsgId       = r.MsgId,
                OccurredAt  = r.OccurredAt,
                ObservedAt  = r.ObservedAt,
                DeviceId    = r.DeviceId,
                PayloadSize = r.PayloadSize
            })
            .ToListAsync(ct);
    }

    public async Task<byte[]?> GetPayloadAsync(
        string recordId, CancellationToken ct = default)
        => await db.Records.AsNoTracking()
            .Where(r => r.RecordId == recordId)
            .Select(r => r.Payload)
            .FirstOrDefaultAsync(ct);
}
