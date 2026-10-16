using CustomSync.Services;

namespace CustomSync.Api.Endpoints;

public static class AuditEndpoints
{
    public static void MapAuditEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/v1/audit").RequireAuthorization("admin");

        group.MapGet("/", async (
            long? before, int? limit, AuditService audit, SettingsService settings) =>
        {
            var defaultSize = await settings.GetIntAsync("api.default_page_size");
            var maxSize     = await settings.GetIntAsync("api.max_page_size");
            var size = Math.Clamp(limit ?? defaultSize, 1, maxSize);

            var rows = await audit.PageAsync(before, size);
            return Results.Ok(new
            {
                items = rows.Select(a => new
                {
                    a.Id, a.At, a.Action, a.DeviceId, a.ActorDeviceId, a.Detail
                }),
                // To'liq sahifa -- davomi bo'lishi mumkin; kam kelsa oxiri.
                nextBefore = rows.Count == size ? rows[^1].Id : (long?)null
            });
        });
    }
}
