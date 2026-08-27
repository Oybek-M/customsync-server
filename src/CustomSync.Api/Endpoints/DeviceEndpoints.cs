using System.Security.Claims;
using CustomSync.Api.Auth;
using CustomSync.Services;

namespace CustomSync.Api.Endpoints;

public static class DeviceEndpoints
{
    public record RedeemRequest(string Code, string Name, string Platform);
    public record RefreshRequest(string DeviceId, string RefreshToken);

    public static void MapDeviceEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/v1/devices");

        // Ochiq: qurilma hali tokenga ega emas. Kodning o'zi maxfiy.
        group.MapPost("/enroll", async (
            RedeemRequest request, DeviceService devices, JwtIssuer jwt) =>
        {
            var enrolled = await devices.RedeemAsync(
                request.Code, request.Name, request.Platform);
            if (enrolled is null)
                return Results.BadRequest(new { error = "invalid_or_used_code" });

            var (token, expiresAt) = await jwt.IssueAsync(enrolled.DeviceId);
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

            var (token, expiresAt) = await jwt.IssueAsync(refreshed.DeviceId);
            return Results.Ok(new
            {
                refreshToken = refreshed.RefreshToken,
                accessToken  = token,
                expiresAt
            });
        });

        group.MapGet("/", async (DeviceService devices) =>
            Results.Ok(await devices.ListAsync()))
            .RequireAuthorization();

        group.MapPost("/codes", async (DeviceService devices) =>
            Results.Ok(new { code = await devices.CreateEnrollmentCodeAsync() }))
            .RequireAuthorization();

        group.MapDelete("/{deviceId}", async (
            string deviceId, DeviceService devices, ClaimsPrincipal user) =>
        {
            await devices.RevokeAsync(deviceId);
            return Results.NoContent();
        }).RequireAuthorization();
    }
}
