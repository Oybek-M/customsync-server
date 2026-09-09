using CustomSync.Data;
using Microsoft.EntityFrameworkCore;

namespace CustomSync.Services;

public sealed record PeerStat(
    string PeerHash, int RecordCount, long TotalBytes, long LastOccurredAt);

public sealed record StorageStat(
    long RecordCount, long RecordBytes, long MediaCount, long MediaBytes);

public sealed record StorageSummary(
    long RecordCount,
    long RecordBytes,
    long MediaCount,
    long MediaBytes,
    long TotalBytes,
    long DailyGrowthBytes,
    int? DaysUntilFull);

public class StatsService(SyncDbContext db, SettingsService? settings = null)
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

    /// <summary>
    /// Standart hisoblash oynasi — 7 kun. Qisqaroq oyna (masalan 1 kun)
    /// tasodifiy sokin kunlar ta'sirida keskin o'zgaradi, uzunroq oyna esa
    /// yaqinda boshlangan tez o'sish dinamikasini yashirib qo'yadi.
    /// </summary>
    public const int DefaultGrowthWindowDays = 7;

    private async Task<int> GetGrowthWindowDaysAsync(CancellationToken ct)
    {
        if (settings != null)
        {
            try
            {
                var days = await settings.GetIntAsync("storage.growth_window_days", ct);
                if (days > 0) return days;
            }
            catch (KeyNotFoundException)
            {
                // Sozlama bazada mavjud bo'lmasa standart qiymat ishlatiladi
            }
        }
        return DefaultGrowthWindowDays;
    }

    /// <summary>
    /// So'nggi kuzatilgan oyna bo'yicha o'rtacha kunlik o'sish hajmi (baytlarda).
    /// Faqat yozuvlar emas, media hajmi ham inobatga olinadi.
    /// Qat'iy 7 ga bo'linmaydi: eng eski yozuv/media sanasidan hozirgacha
    /// bo'lgan haqiqiy kunlar soniga (1..window oralig'ida qisilgan holda) bo'linadi.
    /// </summary>
    private async Task<long> DailyGrowthAsync(CancellationToken ct)
    {
        var windowDays = await GetGrowthWindowDaysAsync(ct);
        var now = DateTime.UtcNow;
        var cutoff = now.AddDays(-windowDays);

        var recentRecordBytes = await db.Records
            .Where(r => r.ReceivedAt >= cutoff)
            .SumAsync(r => (long?)r.PayloadSize, ct) ?? 0L;

        var recentMediaBytes = await db.MediaBlobs
            .Where(m => m.UploadedAt >= cutoff)
            .SumAsync(m => (long?)m.Size, ct) ?? 0L;

        var totalRecentBytes = recentRecordBytes + recentMediaBytes;
        if (totalRecentBytes <= 0)
        {
            return 0L;
        }

        DateTime? oldestRecord = recentRecordBytes > 0
            ? await db.Records.Where(r => r.ReceivedAt >= cutoff).MinAsync(r => (DateTime?)r.ReceivedAt, ct)
            : null;

        DateTime? oldestMedia = recentMediaBytes > 0
            ? await db.MediaBlobs.Where(m => m.UploadedAt >= cutoff).MinAsync(m => (DateTime?)m.UploadedAt, ct)
            : null;

        var oldest = (oldestRecord, oldestMedia) switch
        {
            (not null, not null) => oldestRecord < oldestMedia ? oldestRecord.Value : oldestMedia.Value,
            (not null, null)     => oldestRecord.Value,
            (null, not null)     => oldestMedia.Value,
            _                    => now
        };

        var elapsedDays = (int)Math.Round((now - oldest).TotalDays);
        var observedDays = Math.Clamp(elapsedDays, 1, windowDays);

        return totalRecentBytes / observedDays;
    }

    /// <summary>
    /// Xotira xulosasi va to'lish prognozi.
    /// DaysUntilFull: agar o'sish nol bo'lsa yoki disk sig'imi berilmagan bo'lsa — null.
    /// Faqat disk allaqachon to'lgan yoki sig'imdan oshgan bo'lsa — 0.
    /// </summary>
    public async Task<StorageSummary> SummaryAsync(
        long? diskCapacityBytes = null, CancellationToken ct = default)
    {
        var recordCount = await db.Records.LongCountAsync(ct);
        var recordBytes = recordCount == 0
            ? 0L
            : await db.Records.SumAsync(r => (long)r.PayloadSize, ct);
        var mediaCount  = await db.MediaBlobs.LongCountAsync(ct);
        var mediaBytes  = mediaCount == 0
            ? 0L
            : await db.MediaBlobs.SumAsync(m => m.Size, ct);

        var totalBytes = recordBytes + mediaBytes;
        var growth = await DailyGrowthAsync(ct);

        int? daysUntilFull = null;
        if (diskCapacityBytes is > 0)
        {
            var remaining = diskCapacityBytes.Value - totalBytes;
            if (remaining <= 0)
            {
                daysUntilFull = 0;
            }
            else if (growth > 0)
            {
                daysUntilFull = (int)(remaining / growth);
            }
        }

        return new StorageSummary(
            recordCount,
            recordBytes,
            mediaCount,
            mediaBytes,
            totalBytes,
            growth,
            daysUntilFull);
    }
}
