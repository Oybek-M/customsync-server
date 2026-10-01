using System.Text.RegularExpressions;
using CustomSync.Data;
using CustomSync.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace CustomSync.Services;

/// <summary>
/// Kontent-adresli shifrlangan blob saqlash. Blob'lar diskda, metadata
/// bazada — katta ikkilik ma'lumotni PostgreSQL ichida saqlash zaxira
/// olishni ham, so'rovlarni ham sekinlashtiradi.
/// </summary>
public partial class MediaService(SyncDbContext db, string storageRoot)
{
    [GeneratedRegex("^[0-9a-f]{64}$")]
    private static partial Regex HashRegex();

    public static bool IsValidHash(string? hash) => hash is not null && HashRegex().IsMatch(hash);

    public static void ValidateHash(string hash)
    {
        if (!IsValidHash(hash))
            throw new ArgumentException("Hash must be a 64-character lowercase hex SHA-256 string.", nameof(hash));
    }

    public async Task<MediaBlobEntity?> GetBlobAsync(string hash, CancellationToken ct = default)
    {
        ValidateHash(hash);
        var blob = await db.MediaBlobs.FirstOrDefaultAsync(m => m.Hash == hash, ct);
        if (blob is null) return null;

        if (blob.OrphanedAt != null)
        {
            blob.OrphanedAt = null;
            await db.SaveChangesAsync(ct);
        }

        return blob;
    }

    public async Task<bool> ExistsAsync(string hash, CancellationToken ct = default)
    {
        return await GetBlobAsync(hash, ct) is not null;
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
        ValidateHash(hash);
        if (nonce is null || nonce.Length != 12)
            throw new ArgumentException("Nonce must be exactly 12 bytes.", nameof(nonce));

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
        ValidateHash(hash);
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
