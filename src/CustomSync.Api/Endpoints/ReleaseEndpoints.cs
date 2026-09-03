using System.Security.Claims;
using System.Text.RegularExpressions;
using CustomSync.Services.Releases;
using Microsoft.AspNetCore.Http;

namespace CustomSync.Api.Endpoints;

public record CreateReleaseRequest(
    string Platform,
    long Version,
    string Channel,
    string Sha256,
    long Size,
    string PackageName
);

public record UploadInitRequest(
    long Size
);

public record FinishUploadRequest(
    string? Sha256
);

public static class ReleaseEndpoints
{
    public static void MapReleaseEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/v1/releases").RequireAuthorization();

        // 1. Create release
        group.MapPost("", async (
            CreateReleaseRequest req,
            ClaimsPrincipal user,
            ReleaseService releases) =>
        {
            var deviceId = user.FindFirstValue(ClaimTypes.NameIdentifier) ?? "admin";
            var (id, state) = await releases.CreateOrGetReleaseAsync(
                req.Platform, req.Version, req.Channel, req.Sha256, req.Size, req.PackageName, deviceId);
            return Results.Ok(new { id, state });
        });

        // 2. Open upload session
        group.MapPost("/{id}/upload", async (
            string id,
            UploadInitRequest req,
            ReleaseService releases) =>
        {
            var session = await releases.SessionStore.OpenOrCreateSessionAsync(id, req.Size);
            return Results.Ok(new { sid = session.SessionId, received = session.Received });
        });

        // 3. Upload chunk
        group.MapPut("/{id}/upload/{sid}", async (
            string id,
            string sid,
            HttpRequest request,
            ReleaseService releases) =>
        {
            var session = await releases.SessionStore.GetSessionAsync(id, sid);
            if (session is null)
                return Results.NotFound(new { error = "no such session" });

            var contentRange = request.Headers["Content-Range"].ToString();
            if (string.IsNullOrEmpty(contentRange))
                return Results.BadRequest(new { error = "Content-Range required" });

            var match = Regex.Match(contentRange, @"bytes (\d+)-(\d+)/(\d+)");
            if (!match.Success)
                return Results.BadRequest(new { error = $"bad Content-Range: {contentRange}" });

            var from = long.Parse(match.Groups[1].Value);
            var to = long.Parse(match.Groups[2].Value);
            var total = long.Parse(match.Groups[3].Value);

            var (ok, received, err, statusCode) = await releases.SessionStore.WriteChunkAsync(
                session, from, to, total, request.Body);

            if (!ok)
                return Results.Json(new { error = err, received }, statusCode: statusCode);

            return Results.Ok(new { received });
        }).DisableAntiforgery();

        // 4. Get upload status
        group.MapGet("/{id}/upload/{sid}", async (
            string id,
            string sid,
            ReleaseService releases) =>
        {
            var session = await releases.SessionStore.GetSessionAsync(id, sid);
            if (session is null)
                return Results.NotFound(new { error = "no such session" });

            return Results.Ok(new { sid = session.SessionId, received = session.Received });
        });

        // 5. Finish upload
        group.MapPost("/{id}/finish", async (
            string id,
            FinishUploadRequest? req,
            ReleaseService releases) =>
        {
            var (ok, state, err, statusCode) = await releases.FinishUploadAsync(id, req?.Sha256);
            if (!ok)
                return Results.Json(new { error = err }, statusCode: statusCode);

            return Results.Ok(new { state });
        });

        // 6. Publish to mirrors
        group.MapPost("/{id}/publish", async (
            string id,
            string? only,
            ReleaseService releases) =>
        {
            var results = await releases.PublishAsync(id, only);
            return Results.Ok(new { mirrors = results });
        });

        // 7. List releases
        group.MapGet("", async (ReleaseService releases) =>
        {
            var list = await releases.GetReleasesAsync();
            return Results.Ok(list);
        });
    }
}
