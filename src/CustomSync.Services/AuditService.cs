using System.Text.Json;
using CustomSync.Data;
using CustomSync.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace CustomSync.Services;

/// <summary>
/// Xavfsizlik ahamiyatiga ega hodisalar jurnali — qurilma ulanishi,
/// bekor qilinishi, sozlamalar o'zgarishi.
/// Fayl loglaridan farqi: bu jadval hech qachon rotatsiya qilinmaydi
/// va web app'dan ko'riladi.
///
/// MUHIM TARTIB: Audit yozuvi asosiy amaliyot TUGAGANDAN KEYIN
/// yoziladi. Teskari tartib — avval audit, keyin amaliyot — xavfli:
/// asosiy amaliyot muvaffaqiyatsiz bo'lsa ham audit "amalga oshirildi"
/// deb ko'rsatgan bo'lar edi. Masalan: revoke bekor bo'lib, audit
/// yozilgan bo'lsa — qurilma hali ishlab turibdi, lekin "revoked"
/// ko'rinadi.
/// </summary>
public class AuditService(SyncDbContext db)
{
    public async Task WriteAsync(
        string action,
        string? targetDeviceId = null,
        string? actorDeviceId = null,
        object? detail = null,
        CancellationToken ct = default)
    {
        db.AuditLogs.Add(new AuditLogEntity
        {
            At       = DateTime.UtcNow,
            DeviceId      = targetDeviceId,
            ActorDeviceId = actorDeviceId,
            Action        = action,
            Detail        = detail is null ? null : JsonSerializer.Serialize(detail)
        });
        await db.SaveChangesAsync(ct);
    }

    public async Task<IReadOnlyList<AuditLogEntity>> RecentAsync(
        int limit, CancellationToken ct = default)
        => await db.AuditLogs.AsNoTracking()
            .OrderByDescending(a => a.At)
            .Take(limit)
            .ToListAsync(ct);
}
