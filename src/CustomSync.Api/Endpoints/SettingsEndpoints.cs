using System.Security.Claims;
using CustomSync.Services;

namespace CustomSync.Api.Endpoints;

public static class SettingsEndpoints
{
    public record UpdateRequest(string Value);

    public static void MapSettingsEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/v1/settings").RequireAuthorization("admin");

        group.MapGet("/", async (SettingsService settings) =>
            Results.Ok(await settings.ListAsync()));

        group.MapPut("/{key}", async (
            string key, UpdateRequest request, SettingsService settings,
            AuditService audit, ClaimsPrincipal user) =>
        {
            try
            {
                // Eski qiymatni audit uchun oldindan saqlab olamiz
                var oldValue = await settings.GetStringAsync(key);

                await settings.SetAsync(key, request.Value);

                // Audit: sozlama o'zgarishi TUGAGANDAN KEYIN yoziladi.
                // Actor (admin qurilma) va eski/yangi qiymatlar qayd etiladi --
                // retention.activity_days=1 kabi halokatli o'zgarishda "kim qildi"
                // savoliga javob berish audit izining asosiy vazifasi.
                var actor = user.FindFirstValue(ClaimTypes.NameIdentifier);
                await audit.WriteAsync(
                    "settings.changed",
                    actorDeviceId: actor,
                    detail: new { key, oldValue, newValue = request.Value });

                return Results.NoContent();
            }
            catch (KeyNotFoundException)
            {
                return Results.NotFound(new { error = "unknown_setting", key });
            }
        });
    }
}
