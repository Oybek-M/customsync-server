using CustomSync.Data;
using Microsoft.EntityFrameworkCore;

namespace CustomSync.Services;

public sealed record PeerStat(
    string PeerHash, int RecordCount, long TotalBytes, long LastOccurredAt);

public sealed record StorageStat(
    long RecordCount, long RecordBytes, long MediaCount, long MediaBytes);

public class StatsService(SyncDbContext db)
{
    /// <summary>
    /// peer_hash deterministik HMAC bo'lgani uchun guruhlash serverda
    /// ishlaydi — ism kerak emas. Web app natijaga ismlarni o'zi qo'shadi.
    /// </summary>
    public async Task<IReadOnlyList<PeerStat>> PeersAsync(
        string sort = "bytes", int limit = 50, CancellationToken ct = default)
    {
        var grouped = db.Records.AsNoTracking()
            .GroupBy(r => r.PeerHash)
            .Select(g => new
            {
                PeerHash       = g.Key,
                RecordCount    = g.Count(),
                TotalBytes     = g.Sum(r => (long)r.PayloadSize),
                LastOccurredAt = g.Max(r => r.OccurredAt)
            });

        grouped = sort switch
        {
            "count"  => grouped.OrderByDescending(p => p.RecordCount),
            "recent" => grouped.OrderByDescending(p => p.LastOccurredAt),
            _        => grouped.OrderByDescending(p => p.TotalBytes)
        };

        var rows = await grouped.Take(limit).ToListAsync(ct);
        return rows.Select(p => new PeerStat(p.PeerHash, p.RecordCount, p.TotalBytes, p.LastOccurredAt)).ToList();
    }

    public async Task<StorageStat> StorageAsync(CancellationToken ct = default)
    {
        var recordCount = await db.Records.LongCountAsync(ct);
        var recordBytes = recordCount == 0
            ? 0
            : await db.Records.SumAsync(r => (long)r.PayloadSize, ct);
        var mediaCount  = await db.MediaBlobs.LongCountAsync(ct);
        var mediaBytes  = mediaCount == 0
            ? 0
            : await db.MediaBlobs.SumAsync(m => m.Size, ct);

        return new StorageStat(recordCount, recordBytes, mediaCount, mediaBytes);
    }
}
