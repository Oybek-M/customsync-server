using System.Security.Claims;
using CustomSync.Api.Auth;
using CustomSync.Services;

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
    }
}
