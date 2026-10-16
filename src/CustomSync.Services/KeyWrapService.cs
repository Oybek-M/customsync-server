using CustomSync.Data;
using CustomSync.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace CustomSync.Services;

public sealed record WrapSummary(
    string WrapId, string WrapType, string Label,
    DateTime CreatedAt, DateTime? LastUsedAt);

/// <summary>
/// Master kalitning o'ralgan nusxalari. Server o'ralgan blob'ni saqlaydi,
/// lekin uni hech qachon ocha olmaydi — KEK klientda hosil qilinadi.
///
/// Qurilma o'ramlari bu yerda saqlanmaydi: ular OS keystore ichida
/// qoladi. Bu jadval faqat KO'CHMA tiklash yo'llari uchun.
/// </summary>
public class KeyWrapService(SyncDbContext db, AuditService audit)
{
    public async Task<IReadOnlyList<WrapSummary>> ListAsync(
        CancellationToken ct = default)
        => await db.KeyWraps.AsNoTracking()
            .OrderBy(w => w.CreatedAt)
            .Select(w => new WrapSummary(
                w.WrapId, w.WrapType, w.Label, w.CreatedAt, w.LastUsedAt))
            .ToListAsync(ct);

    public async Task<KeyWrapEntity?> GetAsync(
        string wrapId, string actorDeviceId, CancellationToken ct = default)
    {
        var wrap = await db.KeyWraps.FirstOrDefaultAsync(w => w.WrapId == wrapId, ct);
        if (wrap is null) return null;

        wrap.LastUsedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
        await audit.WriteAsync("keywrap.retrieved", actorDeviceId: actorDeviceId, detail: new { wrapId, wrap.WrapType }, ct: ct);
        return wrap;
    }

    public async Task<string> CreateAsync(
        string wrapType, string label, byte[] salt, byte[] nonce,
        byte[] wrappedKey, int iterations, string actorDeviceId, CancellationToken ct = default)
    {
        var wrapId = Guid.NewGuid().ToString("N");
        db.KeyWraps.Add(new KeyWrapEntity
        {
            WrapId     = wrapId,
            WrapType   = wrapType,
            Label      = label,
            Salt       = salt,
            Nonce      = nonce,
            WrappedKey = wrappedKey,
            Iterations = iterations,
            CreatedAt  = DateTime.UtcNow
        });
        await db.SaveChangesAsync(ct);
        await audit.WriteAsync("keywrap.created", actorDeviceId: actorDeviceId, detail: new { wrapId, wrapType, label }, ct: ct);
        return wrapId;
    }

    /// Oxirgi o'ram o'chirilmaydi: usiz yangi qurilma va web panel arxivni
    /// hech qachon ocholmaydi. Buni UI emas, server hal qiladi.
    public async Task<WrapDeleteResult> DeleteAsync(string wrapId, string actorDeviceId, CancellationToken ct = default)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        // Parallel o'chirishlar navbat bilan: aks holda ikkalasi ham
        // "2 ta bor" deb ko'rib, oxirgi ikkala o'ramni o'chirib yuborardi.
        await db.Database.ExecuteSqlRawAsync("LOCK TABLE key_wraps IN SHARE ROW EXCLUSIVE MODE", ct);

        if (!await db.KeyWraps.AnyAsync(w => w.WrapId == wrapId, ct))
            return WrapDeleteResult.NotFound;
        if (await db.KeyWraps.CountAsync(ct) <= 1)
            return WrapDeleteResult.LastWrap;

        await db.KeyWraps.Where(w => w.WrapId == wrapId).ExecuteDeleteAsync(ct);
        await tx.CommitAsync(ct);
        await audit.WriteAsync("keywrap.deleted", actorDeviceId: actorDeviceId, detail: new { wrapId }, ct: ct);
        return WrapDeleteResult.Deleted;
    }
}

public enum WrapDeleteResult { Deleted, NotFound, LastWrap }
