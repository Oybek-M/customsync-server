using System.Security.Claims;
using CustomSync.Api.Realtime;
using CustomSync.Core.Contracts;
using CustomSync.Services;

namespace CustomSync.Api.Endpoints;

public static class SyncEndpoints
{
    public record PushRequest(IReadOnlyList<SyncRecord> Records);

    public static void MapSyncEndpoints(this WebApplication app)
    {
        // Sync endpoint'lari device roli uchun ham ochiq —
        // admin siyosatiga bog'lamang. Bekor qilingan qurilmalar
        // DeviceRevocationCache orqali OnTokenValidated da rad etiladi
        // (xotirada O(1) — har so'rovda DB'dan tekshirilmaydi).
        var group = app.MapGroup("/api/v1/sync").RequireAuthorization();

        group.MapPost("/push", async (
            PushRequest request,
            ClaimsPrincipal user,
            SyncService sync,
            SettingsService settings,
            NotifyHub hub) =>
        {
            var deviceId = user.FindFirstValue(ClaimTypes.NameIdentifier)!;

            var maxBatch = await settings.GetIntAsync("sync.push_batch_size");
            if (request.Records.Count > maxBatch)
                return Results.BadRequest(new
                {
                    error    = "batch_too_large",
                    max      = maxBatch,
                    received = request.Records.Count
                });

            var results = await sync.PushAsync(deviceId, request.Records);

            // Faqat haqiqatan o'zgarish bo'lsa xabar beramiz — dublikatlar
            // boshqa qurilmalarni behuda uyg'otmasligi kerak.
            var applied = results
                .Where(r => r.Seq.HasValue)
                .Select(r => r.Seq!.Value)
                .DefaultIfEmpty(0)
                .Max();
            if (applied > 0)
                await hub.NotifyOthersAsync(deviceId, applied);

            return Results.Ok(new { results });
        });

        group.MapGet("/pull", async (
            long since,
            int? limit,
            SyncService sync,
            SettingsService settings) =>
        {
            var configured = await settings.GetIntAsync("sync.pull_batch_size");
            var effective  = Math.Clamp(limit ?? configured, 1, configured);
            return Results.Ok(await sync.PullAsync(since, effective));
        });
    }
}
