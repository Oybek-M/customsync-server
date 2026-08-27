using System.Security.Cryptography;
using System.Text;
using CustomSync.Data;
using CustomSync.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace CustomSync.Services;

public sealed record EnrolledDevice(
    string DeviceId, string RefreshToken, string Name, string Platform);

public class DeviceService(SyncDbContext db, SettingsService settings)
{
    /// <summary>
    /// Bir martalik ro'yxatdan o'tkazish kodi. Web app'da ko'rsatiladi,
    /// qurilmaga qo'lda kiritiladi. Bazada faqat hash saqlanadi.
    /// </summary>
    public async Task<string> CreateEnrollmentCodeAsync(CancellationToken ct = default)
    {
        var code = Base32(RandomNumberGenerator.GetBytes(10)); // 50 bit
        var minutes = await settings.GetIntAsync("auth.enroll_code_minutes", ct);

        db.EnrollmentCodes.Add(new EnrollmentCodeEntity
        {
            CodeHash  = Sha256Hex(code),
            CreatedAt = DateTime.UtcNow,
            ExpiresAt = DateTime.UtcNow.AddMinutes(minutes)
        });
        await db.SaveChangesAsync(ct);
        return code;
    }

    public async Task<EnrolledDevice?> RedeemAsync(
        string code, string name, string platform, CancellationToken ct = default)
    {
        var hash = Sha256Hex(code);
        var now  = DateTime.UtcNow;

        // Platform nomi faqat o'qishga qulaylik uchun -- u qisqartiriladi,
        // lekin GUID KESILMAYDI. Ilgari butun satr 24 belgiga kesilardi va
        // uzun platform nomida GUID'dan atigi 8 ta hex belgi (32 bit)
        // qolardi. deviceId -- PRIMARY KEY, va to'qnashuv tekshiruvi yo'q,
        // shuning uchun entropiyani qisqartirishga asos yo'q edi.
        var platformPrefix = platform.Length > 15 ? platform[..15] : platform;
        var deviceId       = $"{platformPrefix}-{Guid.NewGuid():N}";
        var refreshToken   = Base32(RandomNumberGenerator.GetBytes(32));

        // Kodni ATOMAR "band qilish". Shart SQL'ning o'zida bo'lgani uchun
        // ikki so'rov bir vaqtda kelsa, ikkinchisi qator qulfini kutadi va
        // keyin `used_at IS NULL` shartiga tushmay 0 qator qaytaradi.
        //
        // Avvalgi shakl (o'qish -> tekshirish -> yozish) buni bermasdi:
        // ikkala so'rov ham tekshiruvdan o'tib, ikkalasi ham qurilma
        // ro'yxatdan o'tkazardi -- bir martalik kodning butun maqsadi
        // yo'qqa chiqardi. Ketma-ket test buni ushlamaydi.
        await using var tx = await db.Database.BeginTransactionAsync(ct);

        var claimed = await db.EnrollmentCodes
            .Where(c => c.CodeHash == hash && c.UsedAt == null && c.ExpiresAt >= now)
            .ExecuteUpdateAsync(s => s
                .SetProperty(c => c.UsedAt, now)
                .SetProperty(c => c.UsedBy, deviceId), ct);

        if (claimed == 0)
        {
            await tx.RollbackAsync(ct);
            return null;
        }

        db.Devices.Add(new DeviceEntity
        {
            DeviceId    = deviceId,
            Name        = name,
            Platform    = platform,
            EnrolledAt  = now,
            LastCursor  = 0,
            RefreshHash = Sha256Hex(refreshToken)
        });
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);

        return new EnrolledDevice(deviceId, refreshToken, name, platform);
    }

    /// <summary>
    /// Refresh token har ishlatilganda almashtiriladi (rotation) — o'g'irlangan
    /// eski token qayta ishlatilmasligi uchun.
    /// </summary>
    public async Task<EnrolledDevice?> RefreshAsync(
        string deviceId, string refreshToken, CancellationToken ct = default)
    {
        var device = await db.Devices.FirstOrDefaultAsync(d => d.DeviceId == deviceId, ct);
        if (device is null || device.RevokedAt is not null) return null;
        if (!FixedTimeEquals(device.RefreshHash, Sha256Hex(refreshToken))) return null;

        var rotated = Base32(RandomNumberGenerator.GetBytes(32));
        device.RefreshHash = Sha256Hex(rotated);
        device.LastSeenAt  = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);

        return new EnrolledDevice(device.DeviceId, rotated, device.Name, device.Platform);
    }

    public async Task RevokeAsync(string deviceId, CancellationToken ct = default)
    {
        var device = await db.Devices.FirstOrDefaultAsync(d => d.DeviceId == deviceId, ct);
        if (device is null) return;
        device.RevokedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
    }

    public async Task<bool> IsActiveAsync(string deviceId, CancellationToken ct = default)
        => await db.Devices.AnyAsync(
            d => d.DeviceId == deviceId && d.RevokedAt == null, ct);

    /// <summary>Test yordamchisi: barcha amaldagi kodlarni muddati o'tgan qiladi.</summary>
    public async Task ExpireAllCodesAsync(CancellationToken ct = default)
    {
        await db.EnrollmentCodes
            .Where(c => c.UsedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(
                c => c.ExpiresAt, DateTime.UtcNow.AddMinutes(-1)), ct);
    }

    public async Task<IReadOnlyList<DeviceEntity>> ListAsync(CancellationToken ct = default)
        => await db.Devices.AsNoTracking().OrderBy(d => d.EnrolledAt).ToListAsync(ct);

    private static string Sha256Hex(string value)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)))
                  .ToLowerInvariant();

    private static bool FixedTimeEquals(string a, string b)
        => CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(a), Encoding.UTF8.GetBytes(b));

    /// <summary>Chalkashadigan belgilarsiz (0/O, 1/I) — qo'lda kiritish uchun.</summary>
    private static string Base32(byte[] bytes)
    {
        const string alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
        var sb = new StringBuilder();
        foreach (var b in bytes)
        {
            sb.Append(alphabet[b >> 3]);
            if (sb.Length % 5 == 4) sb.Append('-');
        }
        return sb.ToString().TrimEnd('-');
    }
}
