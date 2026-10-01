using System.Security.Claims;
using CustomSync.Services;

namespace CustomSync.Api.Endpoints;

public static class MediaEndpoints
{
    public static void MapMediaEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/v1/media").RequireAuthorization();

        // Klient yuklashdan oldin shu bilan tekshiradi — boshqa qurilma
        // allaqachon yuklagan bo'lsa trafik behuda sarflanmaydi.
        group.MapMethods("/{hash}", ["HEAD"], async (
            string hash, HttpResponse response, MediaService media, CancellationToken ct) =>
        {
            if (!MediaService.IsValidHash(hash))
                return Results.BadRequest();

            var blob = await media.GetBlobAsync(hash, ct);
            if (blob is null) return Results.NotFound();

            response.Headers["X-Nonce"] = Convert.ToBase64String(blob.Nonce);
            return Results.Ok();
        });

        group.MapPut("/{hash}", async (
            string hash,
            HttpRequest request,
            ClaimsPrincipal user,
            MediaService media,
            SettingsService settings,
            CancellationToken ct) =>
        {
            if (!MediaService.IsValidHash(hash))
                return Results.BadRequest();

            var nonceHeader = request.Headers["X-Nonce"].ToString();
            if (string.IsNullOrEmpty(nonceHeader))
                return Results.BadRequest();

            byte[] nonce;
            try
            {
                nonce = Convert.FromBase64String(nonceHeader);
            }
            catch (FormatException)
            {
                return Results.BadRequest();
            }

            if (nonce.Length != 12)
                return Results.BadRequest();

            var deviceId = user.FindFirstValue(ClaimTypes.NameIdentifier)!;

            var max = await settings.GetIntAsync("media.max_upload_bytes");
            using var buffer = new MemoryStream();
            await request.Body.CopyToAsync(buffer, ct);
            if (buffer.Length > max)
                return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);

            // Agar blob allaqachon mavjud bo'lsa, qayta saqlanmaydi va kvotaga
            // ta'sir qilmaydi (dedup).
            if (!await media.ExistsAsync(hash, ct))
            {
                var quotaTotalMb = await settings.GetIntAsync("storage.quota_total_mb");
                if (quotaTotalMb > 0)
                {
                    var totalBytes = await media.GetTotalStoredBytesAsync(ct);
                    if (totalBytes + buffer.Length > (long)quotaTotalMb * 1024L * 1024L)
                        return Results.StatusCode(StatusCodes.Status507InsufficientStorage);
                }

                var quotaPerDeviceMb = await settings.GetIntAsync("storage.quota_per_device_mb");
                if (quotaPerDeviceMb > 0)
                {
                    var deviceBytes = await media.GetDeviceStoredBytesAsync(deviceId, ct);
                    if (deviceBytes + buffer.Length > (long)quotaPerDeviceMb * 1024L * 1024L)
                        return Results.StatusCode(StatusCodes.Status507InsufficientStorage);
                }
            }

            // Eslatma (spec §0.5): server hash'ni shifrlangan baytlar ustidan
            // tekshira OLMAYDI, chunki hash ochiq matn (plaintext) ustidan
            // shifrlashdan oldin hisoblangan. Shifrni ochgandan so'ng tekshirish
            // klient vazifasi. Agar server shifrlangan matnni hashlasa, turli
            // qurilmalardagi turli nonce'lar tufayli dedup butunlay buziladi.
            await media.StoreAsync(hash, buffer.ToArray(), nonce, deviceId, ct);
            return Results.Ok();
        }).DisableAntiforgery();

        group.MapGet("/{hash}", async (
            string hash, HttpResponse response, MediaService media, CancellationToken ct) =>
        {
            if (!MediaService.IsValidHash(hash))
                return Results.BadRequest();

            var blob = await media.GetBlobAsync(hash, ct);
            if (blob is null)
                return Results.NotFound();

            var content = await media.ReadAsync(hash, ct);
            if (content is null)
                return Results.NotFound();

            response.Headers["X-Nonce"] = Convert.ToBase64String(blob.Nonce);
            return Results.File(content, "application/octet-stream");
        });
    }
}
