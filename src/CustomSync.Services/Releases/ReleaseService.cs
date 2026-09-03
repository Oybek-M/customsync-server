using System.Security.Cryptography;
using System.Text.Json;
using CustomSync.Data;
using CustomSync.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace CustomSync.Services.Releases;

public class ReleaseService(SyncDbContext db, UploadSessionStore sessionStore, SettingsService settings, string storageRoot)
{
    public UploadSessionStore SessionStore => sessionStore;

    public async Task<(string Id, string State)> CreateOrGetReleaseAsync(
        string platform,
        long version,
        string channel,
        string sha256,
        long size,
        string packageName,
        string uploadedBy,
        CancellationToken ct = default)
    {
        var existing = await db.Releases
            .FirstOrDefaultAsync(r => r.Platform == platform && r.Version == version && r.Channel == channel, ct);

        if (existing is not null)
        {
            if (string.Equals(existing.Sha256, sha256, StringComparison.OrdinalIgnoreCase) &&
                File.Exists(existing.StoragePath))
            {
                return (existing.ReleaseId, "already_exists");
            }
            return (existing.ReleaseId, "created");
        }

        var releaseId = channel == "stable"
            ? $"{platform}-{version}"
            : $"{platform}-{version}-{channel}";

        var storagePath = Path.Combine(storageRoot, platform, channel, packageName);

        var release = new ReleaseEntity
        {
            ReleaseId   = releaseId,
            Platform    = platform,
            Version     = version,
            Channel     = channel,
            FileName    = packageName,
            Size        = size,
            Sha256      = sha256.ToLowerInvariant(),
            StoragePath = storagePath,
            UploadedBy  = uploadedBy,
            UploadedAt  = DateTime.UtcNow
        };

        db.Releases.Add(release);
        await db.SaveChangesAsync(ct);

        return (releaseId, "created");
    }

    public async Task<(bool Ok, string State, string? Error, int StatusCode)> FinishUploadAsync(
        string releaseId,
        string? declaredSha256,
        CancellationToken ct = default)
    {
        var release = await db.Releases.FirstOrDefaultAsync(r => r.ReleaseId == releaseId, ct);
        if (release is null)
            return (false, "failed", "release_not_found", 404);

        var session = await db.UploadSessions
            .FirstOrDefaultAsync(s => s.ReleaseId == releaseId, ct);

        if (session is null)
        {
            if (File.Exists(release.StoragePath))
                return (true, "complete", null, 200);

            return (false, "failed", "no_session", 404);
        }

        if (!File.Exists(session.TempPath))
            return (false, "failed", "temp_file_missing", 404);

        string actualSha;
        await using (var fs = File.OpenRead(session.TempPath))
        {
            var hashBytes = await SHA256.HashDataAsync(fs, ct);
            actualSha = Convert.ToHexString(hashBytes).ToLowerInvariant();
        }

        var expectedSha = (declaredSha256 ?? release.Sha256).ToLowerInvariant();
        if (!string.Equals(actualSha, expectedSha, StringComparison.OrdinalIgnoreCase))
        {
            try { File.Delete(session.TempPath); } catch { }
            db.UploadSessions.Remove(session);
            await db.SaveChangesAsync(ct);
            return (false, "failed", "checksum mismatch", 422);
        }

        Directory.CreateDirectory(Path.GetDirectoryName(release.StoragePath)!);
        File.Move(session.TempPath, release.StoragePath, overwrite: true);

        db.UploadSessions.Remove(session);

        await EnsureMirrorsCreatedAsync(release, ct);

        await db.SaveChangesAsync(ct);
        return (true, "complete", null, 200);
    }

    public async Task<List<MirrorResultDto>> PublishAsync(
        string releaseId,
        string? onlyMirror,
        CancellationToken ct = default)
    {
        var release = await db.Releases
            .Include(r => r.Mirrors)
            .FirstOrDefaultAsync(r => r.ReleaseId == releaseId, ct);

        if (release is null)
            throw new KeyNotFoundException($"Release '{releaseId}' not found");

        string mirrorsJson;
        try { mirrorsJson = await settings.GetStringAsync("releases.mirrors", ct); }
        catch { mirrorsJson = "[]"; }
        var configuredMirrors = new List<MirrorConfig>();
        try
        {
            configuredMirrors = JsonSerializer.Deserialize<List<MirrorConfig>>(mirrorsJson,
                new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower }) ?? [];
        }
        catch { }

        var standardNames = new[] { "vps-secure", "vps-pub", "github" };
        foreach (var std in standardNames)
        {
            if (!configuredMirrors.Any(m => m.Name == std))
            {
                configuredMirrors.Add(new MirrorConfig { Name = std, Type = std == "github" ? "github" : "local" });
            }
        }

        var targets = configuredMirrors.AsEnumerable();
        if (!string.IsNullOrWhiteSpace(onlyMirror))
        {
            targets = targets.Where(m => string.Equals(m.Name, onlyMirror, StringComparison.OrdinalIgnoreCase));
        }

        var results = new List<MirrorResultDto>();

        foreach (var m in targets)
        {
            var mirrorEntity = release.Mirrors.FirstOrDefault(x => x.Mirror == m.Name);
            if (mirrorEntity is null)
            {
                mirrorEntity = new ReleaseMirrorEntity
                {
                    ReleaseId = release.ReleaseId,
                    Mirror    = m.Name,
                    State     = "pending"
                };
                db.ReleaseMirrors.Add(mirrorEntity);
                release.Mirrors.Add(mirrorEntity);
            }

            try
            {
                if (m.Type == "local" && !string.IsNullOrWhiteSpace(m.Path))
                {
                    Directory.CreateDirectory(m.Path);
                    var dest = Path.Combine(m.Path, release.FileName);
                    File.Copy(release.StoragePath, dest, overwrite: true);
                }

                mirrorEntity.State = "ok";
                mirrorEntity.LastError = null;
                mirrorEntity.VerifiedAt = DateTime.UtcNow;

                results.Add(new MirrorResultDto { Mirror = m.Name, State = "ok" });
            }
            catch (Exception ex)
            {
                mirrorEntity.State = "failed";
                mirrorEntity.LastError = ex.Message;
                results.Add(new MirrorResultDto { Mirror = m.Name, State = "failed", Error = ex.Message });
            }
        }

        if (release.Mirrors.All(m => m.State == "ok"))
        {
            release.PublishedAt = DateTime.UtcNow;
        }

        await db.SaveChangesAsync(ct);
        return results;
    }

    public async Task<List<ReleaseDto>> GetReleasesAsync(CancellationToken ct = default)
    {
        var releases = await db.Releases
            .Include(r => r.Mirrors)
            .OrderByDescending(r => r.UploadedAt)
            .ToListAsync(ct);

        return releases.Select(r => new ReleaseDto
        {
            ReleaseId   = r.ReleaseId,
            Platform    = r.Platform,
            Version     = r.Version,
            Channel     = r.Channel,
            FileName    = r.FileName,
            Size        = r.Size,
            Sha256      = r.Sha256,
            UploadedBy  = r.UploadedBy,
            UploadedAt  = r.UploadedAt,
            PublishedAt = r.PublishedAt,
            Mirrors     = r.Mirrors.Select(m => new MirrorStatusDto
            {
                Mirror     = m.Mirror,
                State      = m.State,
                LastError  = m.LastError,
                VerifiedAt = m.VerifiedAt
            }).ToList()
        }).ToList();
    }

    private async Task EnsureMirrorsCreatedAsync(ReleaseEntity release, CancellationToken ct)
    {
        string mirrorsJson;
        try { mirrorsJson = await settings.GetStringAsync("releases.mirrors", ct); }
        catch { mirrorsJson = "[]"; }
        var configured = new List<MirrorConfig>();
        try
        {
            configured = JsonSerializer.Deserialize<List<MirrorConfig>>(mirrorsJson,
                new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower }) ?? [];
        }
        catch { }

        var standardNames = new[] { "vps-secure", "vps-pub", "github" };
        foreach (var std in standardNames)
        {
            if (!configured.Any(m => m.Name == std))
                configured.Add(new MirrorConfig { Name = std, Type = std == "github" ? "github" : "local" });
        }

        foreach (var m in configured)
        {
            if (!release.Mirrors.Any(x => x.Mirror == m.Name))
            {
                var mirrorEntity = new ReleaseMirrorEntity
                {
                    ReleaseId = release.ReleaseId,
                    Mirror    = m.Name,
                    State     = "pending"
                };
                db.ReleaseMirrors.Add(mirrorEntity);
                release.Mirrors.Add(mirrorEntity);
            }
        }
    }
}

public class MirrorConfig
{
    public string Name { get; set; } = null!;
    public string Type { get; set; } = "local";
    public string? Path { get; set; }
    public string? Url { get; set; }
}

public class MirrorResultDto
{
    public string Mirror { get; set; } = null!;
    public string State { get; set; } = null!;
    public string? Error { get; set; }
}

public class ReleaseDto
{
    public string ReleaseId { get; set; } = null!;
    public string Platform { get; set; } = null!;
    public long Version { get; set; }
    public string Channel { get; set; } = null!;
    public string FileName { get; set; } = null!;
    public long Size { get; set; }
    public string Sha256 { get; set; } = null!;
    public string UploadedBy { get; set; } = null!;
    public DateTime UploadedAt { get; set; }
    public DateTime? PublishedAt { get; set; }
    public List<MirrorStatusDto> Mirrors { get; set; } = [];
}

public class MirrorStatusDto
{
    public string Mirror { get; set; } = null!;
    public string State { get; set; } = null!;
    public string? LastError { get; set; }
    public DateTime? VerifiedAt { get; set; }
}
