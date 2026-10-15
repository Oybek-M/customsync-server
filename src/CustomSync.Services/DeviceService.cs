using System.Security.Cryptography;
using System.Text;
using CustomSync.Data;
using CustomSync.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace CustomSync.Services;

public sealed record EnrolledDevice(
    string DeviceId, string RefreshToken, string Name, string Platform, string Role);

public class DeviceService(SyncDbContext db, SettingsService settings, DeviceRevocationCache cache)
{
    /// <summary>
    /// Ruxsat etilgan rollar. Oq ro'yxat, qora emas: noma'lum satr
    /// bazaga tushsa, `RequireRole` unga hech qachon mos kelmaydi va
    /// qurilma jimgina hech narsa qila olmaydigan holatga tushardi --
    /// sabab esa faqat DB'ga qarabgina topilardi.
    /// </summary>
    public const string RoleDevice = "device";
    public const string RoleAdmin  = "admin";

    public static bool IsValidRole(string role) =>
        role is RoleDevice or RoleAdmin;

    /// <summary>
    /// Bir martalik ro'yxatdan o'tkazish kodi. Web app'da ko'rsatiladi,
    /// qurilmaga qo'lda kiritiladi. Bazada faqat hash saqlanadi.
    /// </summary>
    public async Task<string> CreateEnrollmentCodeAsync(
        string role = RoleDevice, CancellationToken ct = default)
    {
        if (!IsValidRole(role))
            throw new ArgumentException($"Noma'lum rol: {role}", nameof(role));

        var code = Base32(RandomNumberGenerator.GetBytes(10)); // 50 bit
        var minutes = await settings.GetIntAsync("auth.enroll_code_minutes", ct);

        db.EnrollmentCodes.Add(new EnrollmentCodeEntity
        {
            CodeHash   = Sha256Hex(code),
            CreatedAt  = DateTime.UtcNow,
            ExpiresAt  = DateTime.UtcNow.AddMinutes(minutes),
            GrantsRole = role
        });
        await db.SaveChangesAsync(ct);
        return code;
    }

    public async Task<EnrolledDevice?> RedeemAsync(
        string code, string name, string platform, CancellationToken ct = default)
    {
        var hash = Sha256Hex(NormalizeCode(code));
        var now  = DateTime.UtcNow;

        // Platform nomi faqat o'qishga qulaylik uchun -- u qisqartiriladi,
        // lekin GUID KESILMAYDI.
        var platformPrefix = platform.Length > 15 ? platform[..15] : platform;
        var deviceId       = $"{platformPrefix}-{Guid.NewGuid():N}";
        var refreshToken   = Base32(RandomNumberGenerator.GetBytes(32));

        // Kodni ATOMAR "band qilish". Shart SQL'ning o'zida bo'lgani uchun
        // ikki so'rov bir vaqtda kelsa, ikkinchisi qator qulfini kutadi va
        // keyin `used_at IS NULL` shartiga tushmay 0 qator qaytaradi.
        await using var tx = await db.Database.BeginTransactionAsync(ct);

        // Kod qaysi rolni berishini aniqlab olish
        var grantsRole = await db.EnrollmentCodes
            .Where(c => c.CodeHash == hash && c.UsedAt == null && c.ExpiresAt >= now)
            .Select(c => c.GrantsRole)
            .FirstOrDefaultAsync(ct);

        if (grantsRole is null)
        {
            await tx.RollbackAsync(ct);
            return null;
        }

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
            RefreshHash = Sha256Hex(refreshToken),
            Role        = grantsRole
        });
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);

        return new EnrolledDevice(deviceId, refreshToken, name, platform, grantsRole);
    }

    /// <summary>
    /// Refresh token har ishlatilganda almashtiriladi (rotation) — o'g'irlangan
    /// eski token qayta ishlatilmasligi uchun. `auth.refresh_grace_seconds`
    /// ichida eski token yangi zanjir ochmaydi, AYNAN o'sha yangi tokenni
    /// qaytaradi: javob mijozga yetmasa ham qurilma bloklanmaydi.
    /// </summary>
    public async Task<EnrolledDevice?> RefreshAsync(
        string deviceId, string refreshToken, CancellationToken ct = default)
    {
        var device = await db.Devices.AsNoTracking().FirstOrDefaultAsync(d => d.DeviceId == deviceId, ct);
        if (device is null || device.RevokedAt is not null) return null;
        var oldHash = Sha256Hex(refreshToken);
        if (!FixedTimeEquals(device.RefreshHash, oldHash))
            return await ReplayAsync(device, refreshToken, oldHash, ct);

        // Shartli yozish (RedeemAsync kabi): o'qish-keyin-yozishda bitta
        // token bilan parallel so'rovlarning HAMMASI o'tardi, bazada esa
        // oxirgisi qolib, qolgan mijozlar bilmaydigan token bilan qolardi.
        var rotated = Base32(RandomNumberGenerator.GetBytes(32));
        var newHash = Sha256Hex(rotated);
        var replay = SealReplay(refreshToken, deviceId, rotated);
        var now = DateTime.UtcNow;
        var updated = await db.Devices
            .Where(d => d.DeviceId == deviceId && d.RevokedAt == null && d.RefreshHash == oldHash)
            .ExecuteUpdateAsync(s => s
                .SetProperty(d => d.RefreshHash, newHash)
                .SetProperty(d => d.PreviousRefreshHash, oldHash)
                .SetProperty(d => d.RefreshRotatedAt, now)
                .SetProperty(d => d.RotationReplay, replay)
                .SetProperty(d => d.LastSeenAt, now), ct);
        if (updated == 0)
        {
            // Parallel so'rov bizdan oldin almashtirdi: uning natijasini olamiz.
            var current = await db.Devices.AsNoTracking().FirstOrDefaultAsync(d => d.DeviceId == deviceId, ct);
            if (current is null || current.RevokedAt is not null) return null;
            return await ReplayAsync(current, refreshToken, oldHash, ct);
        }

        return new EnrolledDevice(device.DeviceId, rotated, device.Name, device.Platform, device.Role);
    }

    private async Task<EnrolledDevice?> ReplayAsync(
        DeviceEntity device, string refreshToken, string tokenHash, CancellationToken ct)
    {
        if (device.PreviousRefreshHash is null || device.RefreshRotatedAt is null || device.RotationReplay is null)
            return null;
        if (!FixedTimeEquals(device.PreviousRefreshHash, tokenHash)) return null;

        var grace = await settings.GetIntAsync("auth.refresh_grace_seconds", ct);
        if (DateTime.UtcNow - device.RefreshRotatedAt.Value > TimeSpan.FromSeconds(Math.Max(grace, 0)))
            return null;

        var token = OpenReplay(refreshToken, device.DeviceId, device.RotationReplay);
        return token is null
            ? null
            : new EnrolledDevice(device.DeviceId, token, device.Name, device.Platform, device.Role);
    }

    /// <summary>
    /// Yangi tokenni ESKI tokendan olingan kalit bilan shifrlaydi. Bazada faqat
    /// hash'lar turadi, shuning uchun baza o'g'irlansa ham bu nusxani ochib
    /// bo'lmaydi: kalit faqat eski tokenni bilgan mijozda bor.
    /// </summary>
    private static byte[] SealReplay(string oldToken, string deviceId, string newToken)
    {
        var plain = Encoding.UTF8.GetBytes(newToken);
        var box = new byte[ReplayNonceSize + plain.Length + ReplayTagSize];
        var nonce = box.AsSpan(0, ReplayNonceSize);
        RandomNumberGenerator.Fill(nonce);
        using var aes = new AesGcm(ReplayKey(oldToken, deviceId), ReplayTagSize);
        aes.Encrypt(nonce, plain, box.AsSpan(ReplayNonceSize, plain.Length),
                    box.AsSpan(ReplayNonceSize + plain.Length), Encoding.UTF8.GetBytes(deviceId));
        return box;
    }

    private static string? OpenReplay(string oldToken, string deviceId, byte[] box)
    {
        if (box.Length < ReplayNonceSize + ReplayTagSize) return null;
        var plain = new byte[box.Length - ReplayNonceSize - ReplayTagSize];
        try
        {
            using var aes = new AesGcm(ReplayKey(oldToken, deviceId), ReplayTagSize);
            aes.Decrypt(box.AsSpan(0, ReplayNonceSize), box.AsSpan(ReplayNonceSize, plain.Length),
                        box.AsSpan(ReplayNonceSize + plain.Length), plain, Encoding.UTF8.GetBytes(deviceId));
        }
        catch (CryptographicException)
        {
            return null;
        }
        return Encoding.UTF8.GetString(plain);
    }

    private const int ReplayNonceSize = 12;
    private const int ReplayTagSize   = 16;

    private static byte[] ReplayKey(string oldToken, string deviceId)
        => HKDF.DeriveKey(HashAlgorithmName.SHA256, Encoding.UTF8.GetBytes(oldToken), 32,
                          salt: Encoding.UTF8.GetBytes(deviceId),
                          info: "customsync/refresh-replay/v1"u8.ToArray());

    /// <summary>
    /// Qurilmani bekor qilish.
    /// Eslatma: Bekor qilingan qurilma yangi token ololmaydi (refresh qila olmaydi),
    /// biroq uning faol JWT tokeni muddati tugamaguncha (jwt_lifetime_minutes) amal qilaveradi.
    /// </summary>
    public async Task RevokeAsync(string deviceId, CancellationToken ct = default)
    {
        var device = await db.Devices.FirstOrDefaultAsync(d => d.DeviceId == deviceId, ct);
        if (device is null) return;
        device.RevokedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);

        // Keshga ham darhol qo'shiladi
        cache.Add(deviceId);
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
        return Group(bytes.Select(b => alphabet[b >> 3]));
    }

    /// <summary>
    /// Qo'lda terilgan kodni yaratilgandagi shaklga qaytaradi (katta harf,
    /// 4 talik guruhlar), shunda hash bazadagisiga mos keladi. Guruhlash
    /// `Base32` bilan bitta funksiyada -- ikkisi alohida o'zgarib ketsa,
    /// hamma kod jimgina rad etila boshlardi.
    /// </summary>
    private static string NormalizeCode(string code)
        => Group(code.Where(ch => ch != '-' && !char.IsWhiteSpace(ch))
                     .Select(char.ToUpperInvariant));

    private static string Group(IEnumerable<char> chars)
    {
        var sb = new StringBuilder();
        foreach (var ch in chars)
        {
            sb.Append(ch);
            if (sb.Length % 5 == 4) sb.Append('-');
        }
        return sb.ToString().TrimEnd('-');
    }
}
