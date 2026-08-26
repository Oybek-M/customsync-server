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
        
        // EF tracker muammosini chetlab o'tish uchun AsNoTracking() ishlatamiz.
        // ExpireAllCodesAsync ExecuteUpdate yuborganidan keyin ham tracker xotirada eski qiymatni saqlab qolmaydi.
        var entry = await db.EnrollmentCodes
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.CodeHash == hash, ct);

        if (entry is null || entry.UsedAt is not null || entry.ExpiresAt < DateTime.UtcNow)
            return null;

        // Platform nomi juda uzun bo'lsa GUID qismi kesilib ketmasligi uchun 15 belgidan cheklanadi.
        // Natijada GUID qismining kamida 8 ta belgisi saqlanib, to'qnashuv ehtimoli yo'qotiladi.
        var platformPrefix = platform.Length > 15 ? platform[..15] : platform;
        var deviceId    = $"{platformPrefix}-{Guid.NewGuid():N}"[..24];
        var refreshToken = Base32(RandomNumberGenerator.GetBytes(32));

        db.Devices.Add(new DeviceEntity
        {
            DeviceId    = deviceId,
            Name        = name,
            Platform    = platform,
            EnrolledAt  = DateTime.UtcNow,
            LastCursor  = 0,
            RefreshHash = Sha256Hex(refreshToken)
        });

        // Agar xotirada xuddi shu kalit bilan boshqa ob'ekt tracked bo'lsa, uni detach qilamiz
        var tracked = db.ChangeTracker.Entries<EnrollmentCodeEntity>()
            .FirstOrDefault(e => e.Entity.CodeHash == hash);
        if (tracked is not null)
        {
            tracked.State = EntityState.Detached;
        }

        // O'zgartirishlarni yozish uchun entity tracker'ga bog'lanadi va maydonlar o'zgartiriladi
        db.EnrollmentCodes.Attach(entry);
        entry.UsedAt = DateTime.UtcNow;
        entry.UsedBy = deviceId;
        await db.SaveChangesAsync(ct);

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
