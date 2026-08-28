using System.Security.Claims;
using CustomSync.Services;

namespace CustomSync.Api.Endpoints;

public static class KeyEndpoints
{
    public record CreateWrapRequest(
        string WrapType, string Label, string Salt,
        string Nonce, string WrappedKey, int Iterations);

    public static void MapKeyEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/v1/keys/wraps");

        // O'qish har qanday autentifikatsiyalangan qurilma uchun ochiq
        group.MapGet("/", async (KeyWrapService keys) =>
            Results.Ok(await keys.ListAsync()))
            .RequireAuthorization();

        // O'ramni olish — rate limiter bilan cheklangan
        group.MapGet("/{wrapId}", async (
            string wrapId,
            ClaimsPrincipal user,
            KeyWrapService keys) =>
        {
            var deviceId = user.FindFirstValue(ClaimTypes.NameIdentifier)!;
            var wrap = await keys.GetAsync(wrapId, deviceId);
            return wrap is null ? Results.NotFound() : Results.Ok(new
            {
                wrap.WrapId,
                wrap.WrapType,
                wrap.Label,
                Salt       = Convert.ToBase64String(wrap.Salt),
                Nonce      = Convert.ToBase64String(wrap.Nonce),
                WrappedKey = Convert.ToBase64String(wrap.WrappedKey),
                wrap.Iterations
            });
        })
        .RequireAuthorization()
        .RequireRateLimiting("keywrap");

        // Yaratish faqat admin roli uchun
        group.MapPost("/", async (
            CreateWrapRequest request,
            ClaimsPrincipal user,
            KeyWrapService keys) =>
        {
            var deviceId = user.FindFirstValue(ClaimTypes.NameIdentifier)!;
            var wrapId = await keys.CreateAsync(
                request.WrapType, request.Label,
                Convert.FromBase64String(request.Salt),
                Convert.FromBase64String(request.Nonce),
                Convert.FromBase64String(request.WrappedKey),
                request.Iterations,
                deviceId);
            return Results.Ok(new { wrapId });
        })
        .RequireAuthorization("admin");

        // O'chirish faqat admin roli uchun
        group.MapDelete("/{wrapId}", async (
            string wrapId,
            ClaimsPrincipal user,
            KeyWrapService keys) =>
        {
            var deviceId = user.FindFirstValue(ClaimTypes.NameIdentifier)!;
            await keys.DeleteAsync(wrapId, deviceId);
            return Results.NoContent();
        })
        .RequireAuthorization("admin");
    }
}
