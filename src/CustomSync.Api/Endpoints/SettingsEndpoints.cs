using CustomSync.Services;

namespace CustomSync.Api.Endpoints;

public static class SettingsEndpoints
{
    public record UpdateRequest(string Value);

    public static void MapSettingsEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/v1/settings").RequireAuthorization();

        group.MapGet("/", async (SettingsService settings) =>
            Results.Ok(await settings.ListAsync()));

        group.MapPut("/{key}", async (
            string key, UpdateRequest request, SettingsService settings) =>
        {
            try
            {
                await settings.SetAsync(key, request.Value);
                return Results.NoContent();
            }
            catch (KeyNotFoundException)
            {
                return Results.NotFound(new { error = "unknown_setting", key });
            }
        });
    }
}
