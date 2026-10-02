using System.Security.Claims;
using System.Text.Json;
using CustomSync.Api.Auth;
using CustomSync.Data;
using CustomSync.Data.Entities;
using CustomSync.Services;
using Microsoft.EntityFrameworkCore;

namespace CustomSync.Api.Endpoints;

public static class DeviceEndpoints
{
    public record RedeemRequest(string Code, string Name, string Platform);
    public record RefreshRequest(string DeviceId, string RefreshToken);
    public record CreateCodeRequest(string? Role);

    public static void MapDeviceEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/v1/devices");

        // Ochiq: qurilma hali tokenga ega emas. Kodning o'zi maxfiy.
        group.MapPost("/enroll", async (
            RedeemRequest request, DeviceService devices, JwtIssuer jwt, AuditService audit) =>
        {
            var enrolled = await devices.RedeemAsync(
                request.Code, request.Name, request.Platform);
            if (enrolled is null)
                return Results.BadRequest(new { error = "invalid_or_used_code" });

            var (token, expiresAt) = await jwt.IssueAsync(enrolled.DeviceId, enrolled.Role);

            // Audit: enroll amaliyoti TUGAGANDAN KEYIN yoziladi.
            // Actor yo'q (hali tokeni bo'lmagan qurilma o'zi enroll bo'lyapti).
            await audit.WriteAsync(
                "device.enrolled",
                targetDeviceId: enrolled.DeviceId,
                detail: new { enrolled.Name, enrolled.Platform });

            return Results.Ok(new
            {
                deviceId     = enrolled.DeviceId,
                refreshToken = enrolled.RefreshToken,
                accessToken  = token,
                expiresAt
            });
        });

        group.MapPost("/refresh", async (
            RefreshRequest request, DeviceService devices, JwtIssuer jwt) =>
        {
            var refreshed = await devices.RefreshAsync(
                request.DeviceId, request.RefreshToken);
            if (refreshed is null)
                return Results.Unauthorized();

            var (token, expiresAt) = await jwt.IssueAsync(refreshed.DeviceId, refreshed.Role);
            return Results.Ok(new
            {
                refreshToken = refreshed.RefreshToken,
                accessToken  = token,
                expiresAt
            });
        });

        group.MapGet("/", async (DeviceService devices) =>
            Results.Ok(await devices.ListAsync()))
            .RequireAuthorization("admin");

        // Rol ixtiyoriy: berilmasa oddiy qurilma kodi chiqadi. Admin
        // kodini web app'dan ham chiqarish mumkin -- aks holda yagona
        // admin qurilma yo'qolganda SSH'dan boshqa yo'l qolmasdi.
        group.MapPost("/codes", async (
            CreateCodeRequest? request, DeviceService devices) =>
        {
            // Maydon umuman berilmasa -- standart. Ataylab bo'sh satr
            // yuborilsa -- bu klient xatosi, jimgina standartga
            // tushirilmaydi (noma'lum sozlama kaliti ham shunday
            // qattiq rad etiladi).
            var role = request?.Role ?? DeviceService.RoleDevice;
            if (!DeviceService.IsValidRole(role))
                return Results.BadRequest(new { error = "unknown_role", role });

            return Results.Ok(new { code = await devices.CreateEnrollmentCodeAsync(role) });
        }).RequireAuthorization("admin");

        group.MapDelete("/{deviceId}", async (
            string deviceId, DeviceService devices, ClaimsPrincipal user, AuditService audit) =>
        {
            var callerId = user.FindFirstValue(ClaimTypes.NameIdentifier);
            var isAdmin  = user.IsInRole("admin");
            if (!isAdmin && callerId != deviceId) return Results.Forbid();

            await devices.RevokeAsync(deviceId);

            // Audit: revoke amaliyoti TUGAGANDAN KEYIN yoziladi.
            // Actor (kim revoke qildi) ham qayd etiladi: admin boshqa
            // qurilmani bekor qilgan bo'lishi mumkin — "kim qildi" savoliga
            // javob berish uchun actor'ni saqlash majburiy.
            await audit.WriteAsync(
                "device.revoked",
                targetDeviceId: deviceId,
                actorDeviceId: callerId);

            return Results.NoContent();
        }).RequireAuthorization();

        group.MapPost("/health", async (
            HttpContext context,
            ClaimsPrincipal user,
            SyncDbContext db) =>
        {
            var callerId = user.FindFirstValue(ClaimTypes.NameIdentifier);
            if (string.IsNullOrEmpty(callerId))
                return Results.Unauthorized();

            const int maxBytes = 4096;
            if (context.Request.ContentLength > maxBytes)
                return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);

            var buffer = new byte[maxBytes + 1];
            int totalRead = 0;
            while (totalRead < buffer.Length)
            {
                int read = await context.Request.Body.ReadAsync(buffer.AsMemory(totalRead, buffer.Length - totalRead), context.RequestAborted);
                if (read == 0) break;
                totalRead += read;
            }
            if (totalRead > maxBytes)
                return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);

            if (totalRead == 0)
                return Results.BadRequest(new { error = "empty_body" });

            JsonDocument doc;
            try
            {
                doc = JsonDocument.Parse(buffer.AsMemory(0, totalRead));
            }
            catch
            {
                return Results.BadRequest(new { error = "invalid_json" });
            }

            using (doc)
            {
                if (doc.RootElement.ValueKind != JsonValueKind.Object)
                    return Results.BadRequest(new { error = "invalid_json_object" });

                const long maxByteCount = 9007199254740991L; // 2^53 - 1
                const long maxFilesCount = 1000000000L;

                // Required fields: rss_bytes, cache_db_bytes, media_store_bytes, media_store_files
                if (!doc.RootElement.TryGetProperty("rss_bytes", out var rssEl) ||
                    !doc.RootElement.TryGetProperty("cache_db_bytes", out var cacheEl) ||
                    !doc.RootElement.TryGetProperty("media_store_bytes", out var storeEl) ||
                    !doc.RootElement.TryGetProperty("media_store_files", out var filesEl))
                {
                    return Results.BadRequest(new { error = "missing_required_fields" });
                }

                static bool TryParseMetric(JsonElement el, long maxVal, out long? val)
                {
                    val = null;
                    if (el.ValueKind == JsonValueKind.Null) return true;
                    if (el.ValueKind != JsonValueKind.Number) return false;
                    if (!el.TryGetInt64(out var num)) return false;
                    if (num < 0 || num > maxVal) return false;
                    val = num;
                    return true;
                }

                static bool TryParseRequiredMetric(JsonElement el, long maxVal, out long val)
                {
                    val = 0;
                    if (el.ValueKind != JsonValueKind.Number) return false;
                    if (!el.TryGetInt64(out var num)) return false;
                    if (num < 0 || num > maxVal) return false;
                    val = num;
                    return true;
                }

                if (!TryParseRequiredMetric(rssEl, maxByteCount, out var rssBytes) ||
                    !TryParseRequiredMetric(cacheEl, maxByteCount, out var cacheDbBytes) ||
                    !TryParseRequiredMetric(storeEl, maxByteCount, out var mediaStoreBytes) ||
                    !TryParseRequiredMetric(filesEl, maxFilesCount, out var mediaStoreFiles))
                {
                    return Results.BadRequest(new { error = "invalid_required_metric" });
                }

                long? memoryLimitBytes = null;
                if (doc.RootElement.TryGetProperty("memory_limit_bytes", out var memEl) &&
                    !TryParseMetric(memEl, maxByteCount, out memoryLimitBytes))
                {
                    return Results.BadRequest(new { error = "invalid_memory_limit_bytes" });
                }

                long? tdlibFilesBytes = null;
                if (doc.RootElement.TryGetProperty("tdlib_files_bytes", out var tdFilesEl) &&
                    !TryParseMetric(tdFilesEl, maxByteCount, out tdlibFilesBytes))
                {
                    return Results.BadRequest(new { error = "invalid_tdlib_files_bytes" });
                }

                long? tdlibDatabaseBytes = null;
                if (doc.RootElement.TryGetProperty("tdlib_database_bytes", out var tdDbEl) &&
                    !TryParseMetric(tdDbEl, maxByteCount, out tdlibDatabaseBytes))
                {
                    return Results.BadRequest(new { error = "invalid_tdlib_database_bytes" });
                }

                long? freeDiskBytes = null;
                if (doc.RootElement.TryGetProperty("free_disk_bytes", out var freeDiskEl) &&
                    !TryParseMetric(freeDiskEl, maxByteCount, out freeDiskBytes))
                {
                    return Results.BadRequest(new { error = "invalid_free_disk_bytes" });
                }
                var now = DateTime.UtcNow;
                var existing = await db.DeviceHealth.FirstOrDefaultAsync(h => h.DeviceId == callerId);
                if (existing is not null)
                {
                    existing.ReportedAt = now;
                    existing.RssBytes = rssBytes;
                    existing.MemoryLimitBytes = memoryLimitBytes;
                    existing.CacheDbBytes = cacheDbBytes;
                    existing.MediaStoreBytes = mediaStoreBytes;
                    existing.MediaStoreFiles = mediaStoreFiles;
                    existing.TdlibFilesBytes = tdlibFilesBytes;
                    existing.TdlibDatabaseBytes = tdlibDatabaseBytes;
                    existing.FreeDiskBytes = freeDiskBytes;
                }
                else
                {
                    db.DeviceHealth.Add(new DeviceHealthEntity
                    {
                        DeviceId = callerId,
                        ReportedAt = now,
                        RssBytes = rssBytes,
                        MemoryLimitBytes = memoryLimitBytes,
                        CacheDbBytes = cacheDbBytes,
                        MediaStoreBytes = mediaStoreBytes,
                        MediaStoreFiles = mediaStoreFiles,
                        TdlibFilesBytes = tdlibFilesBytes,
                        TdlibDatabaseBytes = tdlibDatabaseBytes,
                        FreeDiskBytes = freeDiskBytes
                    });
                }

                await db.SaveChangesAsync();
                return Results.NoContent();
            }
        }).RequireAuthorization();

        group.MapGet("/health", async (SyncDbContext db, SettingsService settings) =>
        {
            int staleAfterSeconds = 1800;
            try
            {
                staleAfterSeconds = await settings.GetIntAsync("health.stale_after_seconds");
            }
            catch
            {
                staleAfterSeconds = 1800;
            }
            var now = DateTime.UtcNow;

            var rows = await db.DeviceHealth
                .Include(h => h.Device)
                .Where(h => h.Device != null)
                .OrderBy(h => h.Device!.Name)
                .ToListAsync();

            var result = rows.Select(h => new
            {
                device_id = h.DeviceId,
                name = h.Device!.Name,
                platform = h.Device!.Platform,
                revoked = h.Device!.RevokedAt != null,
                reported_at = h.ReportedAt,
                stale = (now - h.ReportedAt).TotalSeconds > staleAfterSeconds,
                rss_bytes = h.RssBytes,
                memory_limit_bytes = h.MemoryLimitBytes,
                cache_db_bytes = h.CacheDbBytes,
                media_store_bytes = h.MediaStoreBytes,
                media_store_files = h.MediaStoreFiles,
                tdlib_files_bytes = h.TdlibFilesBytes,
                tdlib_database_bytes = h.TdlibDatabaseBytes,
                free_disk_bytes = h.FreeDiskBytes
            });

            return Results.Ok(result);
        }).RequireAuthorization("admin");
    }
}
