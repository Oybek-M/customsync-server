using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using CustomSync.Api.Auth;
using CustomSync.Services;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CustomSync.Tests;

public class AuthorizationTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    public AuthorizationTests(WebApplicationFactory<Program> factory) => _factory = factory;

    private async Task<(HttpClient Client, string DeviceId, string Token)> EnrolDeviceAsync(string role)
    {
        var factory = _factory;
        using var scope = factory.Services.CreateScope();
        var devices = scope.ServiceProvider.GetRequiredService<DeviceService>();
        var jwt = scope.ServiceProvider.GetRequiredService<JwtIssuer>();

        var code = await devices.CreateEnrollmentCodeAsync(role);
        var enrolled = await devices.RedeemAsync(code, $"test-dev-{Guid.NewGuid():N}", "test-platform");
        Assert.NotNull(enrolled);

        // JWT tokenni qo'lda chiqaramiz
        var (token, _) = await jwt.IssueAsync(enrolled.DeviceId, enrolled.Role);

        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        return (client, enrolled.DeviceId, token);
    }

    [Fact]
    public async Task Device_role_cannot_create_enrollment_codes()
    {
        var (client, _, _) = await EnrolDeviceAsync("device");

        var response = await client.PostAsJsonAsync("/api/v1/devices/codes", new { });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Admin_role_can_create_enrollment_codes()
    {
        var (client, _, _) = await EnrolDeviceAsync("admin");

        var response = await client.PostAsJsonAsync("/api/v1/devices/codes", new { });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Device_role_cannot_modify_settings()
    {
        var (client, _, _) = await EnrolDeviceAsync("device");

        var response = await client.PutAsJsonAsync("/api/v1/settings/sync.push_batch_size", new { value = "100" });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Device_can_delete_its_own_device()
    {
        var (client, deviceId, _) = await EnrolDeviceAsync("device");

        var response = await client.DeleteAsync($"/api/v1/devices/{deviceId}");

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    [Fact]
    public async Task Device_cannot_delete_another_device()
    {
        var (client1, _, _) = await EnrolDeviceAsync("device");
        var (_, deviceId2, _) = await EnrolDeviceAsync("device");

        var response = await client1.DeleteAsync($"/api/v1/devices/{deviceId2}");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Revoked_token_is_immediately_unauthorized()
    {
        var (client, deviceId, token) = await EnrolDeviceAsync("device");

        // token faol ekanini tekshiramiz
        var testResponse = await client.DeleteAsync($"/api/v1/devices/nonexistent");
        Assert.Equal(HttpStatusCode.Forbidden, testResponse.StatusCode); // o'ziga tegishli bo'lmagani uchun 403, demak auth o'tdi

        // bekor qilamiz
        using (var scope = _factory.Services.CreateScope())
        {
            var devices = scope.ServiceProvider.GetRequiredService<DeviceService>();
            await devices.RevokeAsync(deviceId);
        }

        // qayta tekshiramiz (darhol 401 Unauthorized qaytishi kerak)
        var response = await client.DeleteAsync($"/api/v1/devices/nonexistent");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}
