using System.Collections.Concurrent;
using System.Globalization;
using CustomSync.Data;
using CustomSync.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace CustomSync.Services;

/// <summary>
/// Runtime konfiguratsiya (qoida K1). Sozlanadigan qiymatlar kodda
/// literal bo'lmaydi — ular shu yerda yashaydi va web app'dan
/// qayta deploy qilmasdan o'zgartiriladi.
/// </summary>
public class SettingsService(SyncDbContext db)
{
    // Bitta jarayonda bir nechta baza bo'lishi mumkin -- masalan
    // testlarda har test klassi o'z vaqtinchalik bazasini oladi, xUnit
    // esa klasslarni parallel ishga tushiradi. Kesh shuning uchun
    // connection string bo'yicha ajratiladi: aks holda ikkita mustaqil
    // baza bitta static keshni bulg'ab qo'yardi.
    private static readonly ConcurrentDictionary<string, ConcurrentDictionary<string, string>> CachesByDatabase = new();

    private ConcurrentDictionary<string, string> Cache =>
        CachesByDatabase.GetOrAdd(
            db.Database.GetConnectionString() ?? string.Empty,
            _ => new ConcurrentDictionary<string, string>());

    /// <summary>
    /// Standart qiymatlar. Yangi sozlama qo'shish = shu ro'yxatga bitta
    /// satr qo'shish; migratsiya kerak emas.
    ///
    /// Bu METOD, static ro'yxat emas. Ikki sabab:
    /// 1. `UpdatedAt` chaqiruv paytida hisoblanadi -- static ro'yxatda u
    ///    klass birinchi yuklangan lahzada muzlab qolardi va seed
    ///    qilingan vaqt haqida yolg'on gapirardi.
    /// 2. Har chaqiruv YANGI instance qaytaradi -- bitta static entity
    ///    obyektini bir nechta DbContext'ga biriktirish EF'da tavsiya
    ///    etilmaydi va bir joyda mutatsiya qilinsa hamma bazaga tarqardi.
    /// </summary>
    public static IReadOnlyList<ServerSettingEntity> CreateDefaults() =>
    [
        New("sync.push_batch_size",       "500",  "int",      "sync",    "Bitta push so'rovidagi maksimal yozuvlar soni"),
        New("sync.push_max_bytes",        "5242880", "int",   "sync",    "Bitta push so'rovining maksimal hajmi (bayt)"),
        New("sync.pull_batch_size",       "500",  "int",      "sync",    "Bitta pull javobidagi maksimal yozuvlar soni"),
        New("sync.client_poll_seconds",   "30",   "int",      "sync",    "Klientlar necha soniyada bir pull qilishi"),
        New("api.default_page_size",      "50",   "int",      "api",     "Ro'yxatlar uchun standart sahifa hajmi"),
        New("api.max_page_size",          "200",  "int",      "api",     "So'ralishi mumkin bo'lgan maksimal sahifa hajmi"),
        New("auth.jwt_lifetime_minutes",  "15",   "int",      "auth",    "JWT amal qilish muddati"),
        New("auth.enroll_code_minutes",   "10",   "int",      "auth",    "Ro'yxatdan o'tkazish kodining amal qilish muddati"),
        New("auth.wrap_rate_per_hour",    "5",    "int",      "auth",    "Kalit o'ramini yuklab olish urinishlari (soatiga, qurilma bo'yicha). DIQQAT: bu yerda 0 cheksiz DEGANI EMAS -- boshqa sozlamalardan farqli, nol yoki manfiy qiymat xavfsiz standartga (5) qaytadi, chunki bu brute-force himoyasi"),
        New("media.max_upload_bytes",     "52428800", "int",  "media",   "Bitta media faylning maksimal hajmi"),

        // Spec §0.3 -- retention. 0 = cheksiz saqlash. `activity` uchun
        // standart 90 kun: mijozda 30 kun, server UZUNROQ saqlashi
        // shart -- aks holda mijoz o'chirgan yozuvni server pull orqali
        // qaytarib beradi, mijoz yana o'chiradi, cheksiz sikl yuzaga
        // keladi. Retention hech qachon tombstone yaratmaydi -- u
        // lokal tozalash, global o'chirish emas.
        New("retention.deleted_days",         "0",  "int", "retention", "`deleted` yozuvlarini saqlash muddati (0 = cheksiz)"),
        New("retention.edited_days",          "0",  "int", "retention", "`edited` yozuvlarini saqlash muddati (0 = cheksiz)"),
        New("retention.activity_days",        "90", "int", "retention", "`activity` yozuvlarini saqlash muddati. Mijozda 30 kun -- server UZUNROQ saqlashi shart, aks holda cheksiz sikl yuzaga keladi"),
        New("retention.ghost_read_days",      "0",  "int", "retention", "`ghost_read` yozuvlarini saqlash muddati (0 = cheksiz)"),
        New("retention.setting_days",         "0",  "int", "retention", "`setting` yozuvlarini saqlash muddati (0 = cheksiz)"),
        New("retention.peer_directory_days",  "0",  "int", "retention", "`peer_directory` yozuvlarini saqlash muddati (0 = cheksiz)"),
        New("retention.media_index_days",     "0",  "int", "retention", "`media_index` yozuvlarini saqlash muddati (0 = cheksiz)"),
        New("retention.tombstone_days",       "0",  "int", "retention", "`tombstone` yozuvlarini saqlash muddati (0 = cheksiz)"),

        // Spec §0.9 -- kvota. 0 = cheksiz.
        New("storage.quota_total_mb",       "0", "int", "storage", "Umumiy media hajmi chegarasi, MB (0 = cheksiz)"),
        New("storage.quota_per_device_mb",  "0", "int", "storage", "Qurilma bo'yicha media hajmi chegarasi, MB (0 = cheksiz)"),
    ];

    private static ServerSettingEntity New(
        string key, string value, string type, string category, string description)
        => new()
        {
            Key = key, Value = value, ValueType = type,
            Category = category, Description = description,
            UpdatedAt = DateTime.UtcNow
        };

    public async Task EnsureDefaultsAsync(CancellationToken ct = default)
    {
        var existing = await db.ServerSettings
            .Select(s => s.Key)
            .ToListAsync(ct);

        var missing = CreateDefaults().Where(d => !existing.Contains(d.Key)).ToList();
        if (missing.Count > 0)
        {
            db.ServerSettings.AddRange(missing);
            await db.SaveChangesAsync(ct);
        }

        await ReloadCacheAsync(ct);
    }

    public async Task ReloadCacheAsync(CancellationToken ct = default)
    {
        var all = await db.ServerSettings.AsNoTracking().ToListAsync(ct);
        Cache.Clear();
        foreach (var s in all) Cache[s.Key] = s.Value;
    }

    public async Task SetAsync(string key, string value, CancellationToken ct = default)
    {
        var entity = await db.ServerSettings.FirstOrDefaultAsync(s => s.Key == key, ct)
            ?? throw new KeyNotFoundException($"Noma'lum sozlama: {key}");

        entity.Value = value;
        entity.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
        Cache[key] = value;
    }

    public Task<string> GetStringAsync(string key, CancellationToken ct = default)
    {
        if (Cache.TryGetValue(key, out var cached)) return Task.FromResult(cached);
        throw new KeyNotFoundException($"Noma'lum sozlama: {key}");
    }

    public async Task<int> GetIntAsync(string key, CancellationToken ct = default)
        => int.Parse(await GetStringAsync(key, ct), CultureInfo.InvariantCulture);

    public async Task<bool> GetBoolAsync(string key, CancellationToken ct = default)
        => bool.Parse(await GetStringAsync(key, ct));

    public async Task<IReadOnlyList<ServerSettingEntity>> ListAsync(
        CancellationToken ct = default)
        => await db.ServerSettings
            .AsNoTracking()
            .OrderBy(s => s.Category).ThenBy(s => s.Key)
            .ToListAsync(ct);
}
