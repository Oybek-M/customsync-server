using System.Net;
using System.Net.Http.Json;
using CustomSync.Services;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CustomSync.Tests;

public class DeviceEndpointsTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    public DeviceEndpointsTests(WebApplicationFactory<Program> factory) => _factory = factory;

    private async Task<(HttpClient Client, string Code)> CreateClientAndCodeAsync()
    {
        var client = _factory.CreateClient();

        using var scope = _factory.Services.CreateScope();
        var devices = scope.ServiceProvider.GetRequiredService<DeviceService>();
        var code = await devices.CreateEnrollmentCodeAsync();

        return (client, code);
    }

    [Fact]
    public async Task Enroll_with_valid_code_succeeds()
    {
        var (client, code) = await CreateClientAndCodeAsync();

        var response = await client.PostAsJsonAsync("/api/v1/devices/enroll", new
        {
            code,
            name = "test-device",
            platform = "test-platform"
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<EnrollResponse>();
        Assert.NotNull(body);
        Assert.NotNull(body.DeviceId);
        Assert.NotNull(body.RefreshToken);
        Assert.NotNull(body.AccessToken);
    }

    [Fact]
    public async Task Enroll_with_used_code_fails()
    {
        var (client, code) = await CreateClientAndCodeAsync();

        var response1 = await client.PostAsJsonAsync("/api/v1/devices/enroll", new
        {
            code,
            name = "device-1",
            platform = "platform-1"
        });
        Assert.Equal(HttpStatusCode.OK, response1.StatusCode);

        var response2 = await client.PostAsJsonAsync("/api/v1/devices/enroll", new
        {
            code,
            name = "device-2",
            platform = "platform-2"
        });
        Assert.Equal(HttpStatusCode.BadRequest, response2.StatusCode);
        var body = await response2.Content.ReadFromJsonAsync<ErrorResponse>();
        Assert.Equal("invalid_or_used_code", body?.Error);
    }

    [Fact]
    public async Task Refresh_with_valid_token_rotates_refresh_token()
    {
        var (client, code) = await CreateClientAndCodeAsync();

        var enrollResponse = await client.PostAsJsonAsync("/api/v1/devices/enroll", new
        {
            code,
            name = "refresh-device",
            platform = "platform-refresh"
        });
        var enrolled = await enrollResponse.Content.ReadFromJsonAsync<EnrollResponse>();
        Assert.NotNull(enrolled);

        // Birinchi refresh
        var refreshResponse1 = await client.PostAsJsonAsync("/api/v1/devices/refresh", new
        {
            deviceId = enrolled.DeviceId,
            refreshToken = enrolled.RefreshToken
        });
        Assert.Equal(HttpStatusCode.OK, refreshResponse1.StatusCode);
        var refreshed1 = await refreshResponse1.Content.ReadFromJsonAsync<RefreshResponse>();
        Assert.NotNull(refreshed1);
        Assert.NotEqual(enrolled.RefreshToken, refreshed1.RefreshToken);

        // Eski refresh token bilan qayta urinish (rad etilishi kerak)
        var refreshResponse2 = await client.PostAsJsonAsync("/api/v1/devices/refresh", new
        {
            deviceId = enrolled.DeviceId,
            refreshToken = enrolled.RefreshToken
        });
        Assert.Equal(HttpStatusCode.Unauthorized, refreshResponse2.StatusCode);
    }

    [Fact]
    public async Task Get_devices_without_token_returns_unauthorized()
    {
        var (client, _) = await CreateClientAndCodeAsync();

        var response = await client.GetAsync("/api/v1/devices");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    private record EnrollResponse(string DeviceId, string RefreshToken, string AccessToken, DateTime ExpiresAt);
    private record RefreshResponse(string RefreshToken, string AccessToken, DateTime ExpiresAt);
    private record ErrorResponse(string Error);
}
