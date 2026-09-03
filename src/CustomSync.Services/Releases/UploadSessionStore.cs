using CustomSync.Data;
using CustomSync.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace CustomSync.Services.Releases;

public class UploadSessionStore(SyncDbContext db, string storageRoot)
{
    private readonly string _tempRoot = Path.Combine(storageRoot, "temp");

    public string TempRoot => _tempRoot;

    public async Task<UploadSessionEntity> OpenOrCreateSessionAsync(
        string releaseId,
        long totalSize,
        CancellationToken ct = default)
    {
        Directory.CreateDirectory(_tempRoot);

        // Shu reliz uchun tugallanmagan, muddati o'tmagan seans bormi?
        var existing = await db.UploadSessions
            .FirstOrDefaultAsync(s => s.ReleaseId == releaseId && s.ExpiresAt > DateTime.UtcNow, ct);

        if (existing is not null)
        {
            if (File.Exists(existing.TempPath))
            {
                existing.Received = new FileInfo(existing.TempPath).Length;
            }
            else
            {
                File.Create(existing.TempPath).Dispose();
                existing.Received = 0;
            }
            await db.SaveChangesAsync(ct);
            return existing;
        }

        var sid = Guid.NewGuid().ToString("N");
        var tempPath = Path.Combine(_tempRoot, $"{sid}.part");
        File.Create(tempPath).Dispose();

        var session = new UploadSessionEntity
        {
            SessionId = sid,
            ReleaseId = releaseId,
            TotalSize = totalSize,
            Received  = 0,
            TempPath  = tempPath,
            CreatedAt = DateTime.UtcNow,
            ExpiresAt = DateTime.UtcNow.AddHours(24)
        };

        db.UploadSessions.Add(session);
        await db.SaveChangesAsync(ct);
        return session;
    }

    public async Task<UploadSessionEntity?> GetSessionAsync(
        string releaseId,
        string sessionId,
        CancellationToken ct = default)
    {
        var session = await db.UploadSessions
            .FirstOrDefaultAsync(s => s.ReleaseId == releaseId && s.SessionId == sessionId, ct);

        if (session is not null && File.Exists(session.TempPath))
        {
            session.Received = new FileInfo(session.TempPath).Length;
        }

        return session;
    }

    public async Task<(bool Ok, long Received, string? Error, int StatusCode)> WriteChunkAsync(
        UploadSessionEntity session,
        long from,
        long to,
        long total,
        Stream body,
        CancellationToken ct = default)
    {
        if (from != session.Received)
        {
            return (false, session.Received, $"expected offset {session.Received}, got {from}", 416);
        }

        Directory.CreateDirectory(Path.GetDirectoryName(session.TempPath)!);

        try
        {
            await using (var fs = new FileStream(session.TempPath, FileMode.OpenOrCreate, FileAccess.Write, FileShare.None))
            {
                fs.Seek(from, SeekOrigin.Begin);
                await body.CopyToAsync(fs, ct);
                await fs.FlushAsync(ct);
            }
        }
        finally
        {
            // Diskda haqiqatan turgan bayt soni - hatto uzilish bo'lsa ham
            if (File.Exists(session.TempPath))
            {
                session.Received = new FileInfo(session.TempPath).Length;
            }
            await db.SaveChangesAsync(CancellationToken.None);
        }

        return (true, session.Received, null, 200);
    }
}
