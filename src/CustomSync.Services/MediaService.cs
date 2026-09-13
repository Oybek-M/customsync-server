using CustomSync.Data;
using CustomSync.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace CustomSync.Services;

/// <summary>
/// Kontent-adresli shifrlangan blob saqlash. Blob'lar diskda, metadata
/// bazada — katta ikkilik ma'lumotni PostgreSQL ichida saqlash zaxira
/// olishni ham, so'rovlarni ham sekinlashtiradi.
/// </summary>
public class MediaService(SyncDbContext db, string storageRoot)
{
    public async Task<bool> ExistsAsync(string hash, CancellationToken ct = default)
    {
        var blob = await db.MediaBlobs.FirstOrDefaultAsync(m => m.Hash == hash, ct);
        if (blob is null) return false;

        if (blob.OrphanedAt != null)
        {
            blob.OrphanedAt = null;
            await db.SaveChangesAsync(ct);
        }

        return true;
    }

    public async Task<long> GetTotalStoredBytesAsync(CancellationToken ct = default)
        => await db.MediaBlobs.SumAsync(m => (long?)m.Size, ct) ?? 0L;

    public async Task<long> GetDeviceStoredBytesAsync(string deviceId, CancellationToken ct = default)
        => await db.MediaBlobs
            .Where(m => m.UploadedByDeviceId == deviceId)
            .SumAsync(m => (long?)m.Size, ct) ?? 0L;

    public async Task StoreAsync(
        string hash, byte[] encryptedContent, byte[] nonce,
        string? deviceId = null,
        CancellationToken ct = default)
    {
        if (await ExistsAsync(hash, ct)) return;

        var path = PathFor(hash);
        var dir = Path.GetDirectoryName(path)!;
        if (!Directory.Exists(dir))
            Directory.CreateDirectory(dir);

        await File.WriteAllBytesAsync(path, encryptedContent, ct);

        db.MediaBlobs.Add(new MediaBlobEntity
        {
            Hash               = hash,
            Size               = encryptedContent.LongLength,
            Nonce              = nonce,
            StoragePath        = path,
            UploadedAt         = DateTime.UtcNow,
            UploadedByDeviceId = deviceId
        });
        await db.SaveChangesAsync(ct);
    }

    public async Task<byte[]?> ReadAsync(string hash, CancellationToken ct = default)
    {
        var blob = await db.MediaBlobs.AsNoTracking()
            .FirstOrDefaultAsync(m => m.Hash == hash, ct);
        if (blob is null || !File.Exists(blob.StoragePath)) return null;
        return await File.ReadAllBytesAsync(blob.StoragePath, ct);
    }

    /// <summary>
    /// Bitta katalogda o'n minglab fayl to'planmasligi uchun hash'ning
    /// birinchi ikki belgisi bo'yicha sharding.
    /// </summary>
    private string PathFor(string hash)
    {
        var prefix = hash.Length >= 2 ? hash[..2] : "00";
        return Path.Combine(storageRoot, prefix, hash);
    }
}
