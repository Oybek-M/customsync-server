using CustomSync.Services;

namespace CustomSync.Api.Endpoints;

public static class StatsEndpoints
{
    public static void MapStatsEndpoints(this WebApplication app)
    {
        // Web app boshqaruv paneli uchun — faqat admin ruxsat etiladi (Correction 4)
        var group = app.MapGroup("/api/v1/stats").RequireAuthorization("admin");

        group.MapGet("/peers", async (
            string? sort, int? limit,
            StatsService stats, SettingsService settings) =>
        {
            var defaultSize = await settings.GetIntAsync("api.default_page_size");
            var maxSize     = await settings.GetIntAsync("api.max_page_size");
            var effectiveLimit = Math.Clamp(limit ?? defaultSize, 1, maxSize);

            return Results.Ok(await stats.PeersAsync(sort ?? "bytes", effectiveLimit));
        });

        group.MapGet("/storage", async (StatsService stats) =>
            Results.Ok(await stats.StorageAsync()));
    }
}
