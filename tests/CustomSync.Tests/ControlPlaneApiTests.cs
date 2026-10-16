using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using CustomSync.Api.Auth;
using CustomSync.Services;
using CustomSync.Tests.Fixtures;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CustomSync.Tests;

/// <summary>
/// Web panelning control plane ekranlari (plan 03 Task 3) tayanadigan API.
/// </summary>
public class ControlPlaneApiTests : IClassFixture<CustomSyncWebApplicationFactory>
{
    private readonly CustomSyncWebApplicationFactory _factory;

    public ControlPlaneApiTests(CustomSyncWebApplicationFactory factory) => _factory = factory;

    private async Task<HttpClient> ClientAsync(string role)
    {
        using var scope = _factory.Services.CreateScope();
        var devices = scope.ServiceProvider.GetRequiredService<DeviceService>();
        var jwt = scope.ServiceProvider.GetRequiredService<JwtIssuer>();

        var code = await devices.CreateEnrollmentCodeAsync(role);
        var enrolled = await devices.RedeemAsync(code, $"cp-{Guid.NewGuid():N}", "test-platform");
        Assert.NotNull(enrolled);
        var (token, _) = await jwt.IssueAsync(enrolled.DeviceId, enrolled.Role);

        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    private async Task<string> SettingAsync(string key)
    {
        using var scope = _factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<SettingsService>().GetStringAsync(key);
    }

    /// Refresh hash'lari va grace shifrmatni serverdan chiqmasligi kerak --
    /// ro'yxat entity'ni emas, aniq maydonlarni qaytaradi.
    [Fact]
    public async Task Device_list_does_not_expose_refresh_material()
    {
        var client = await ClientAsync("admin");

        var json = await client.GetStringAsync("/api/v1/devices");
        using var doc = JsonDocument.Parse(json);
        var first = doc.RootElement.EnumerateArray().First();
        var names = first.EnumerateObject().Select(p => p.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);

        Assert.DoesNotContain("refresh_hash", names);
        Assert.DoesNotContain("previous_refresh_hash", names);
        Assert.DoesNotContain("rotation_replay", names);
        Assert.DoesNotContain("refresh_rotated_at", names);
        foreach (var expected in new[] { "device_id", "name", "platform", "role", "enrolled_at", "last_seen_at", "last_cursor", "revoked_at" })
            Assert.Contains(expected, names);
    }

    /// Noto'g'ri tipdagi qiymat saqlansa, keyingi GetIntAsync yiqiladi --
    /// masalan auth.refresh_grace_seconds buzilsa hamma refresh 500 beradi.
    [Theory]
    [InlineData("auth.refresh_grace_seconds", "abc")]
    [InlineData("auth.refresh_grace_seconds", "1.5")]
    [InlineData("auth.refresh_grace_seconds", "")]
    [InlineData("auth.refresh_grace_seconds", " 120")]
    [InlineData("storage.jobs_dry_run", "yes")]
    [InlineData("releases.mirrors", "[")]
    public async Task Setting_with_a_value_of_the_wrong_type_is_rejected(string key, string value)
    {
        var client = await ClientAsync("admin");
        var before = await SettingAsync(key);

        var response = await client.PutAsJsonAsync($"/api/v1/settings/{key}", new { value });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("invalid_value", await response.Content.ReadAsStringAsync());
        Assert.Equal(before, await SettingAsync(key));
    }

    [Fact]
    public async Task Setting_with_a_valid_value_is_saved()
    {
        var client = await ClientAsync("admin");
        var before = await SettingAsync("auth.refresh_grace_seconds");
        try
        {
            var response = await client.PutAsJsonAsync(
                "/api/v1/settings/auth.refresh_grace_seconds", new { value = "90" });

            Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
            Assert.Equal("90", await SettingAsync("auth.refresh_grace_seconds"));
        }
        finally
        {
            using var scope = _factory.Services.CreateScope();
            await scope.ServiceProvider.GetRequiredService<SettingsService>()
                .SetAsync("auth.refresh_grace_seconds", before);
        }
    }

    [Fact]
    public async Task Audit_lists_newest_first_with_keyset_paging()
    {
        using (var scope = _factory.Services.CreateScope())
        {
            var audit = scope.ServiceProvider.GetRequiredService<AuditService>();
            for (var i = 0; i < 3; i++)
                await audit.WriteAsync("test.paging", detail: new { i });
        }
        var client = await ClientAsync("admin");

        var page1 = await client.GetFromJsonAsync<JsonElement>("/api/v1/audit?limit=2");
        var items1 = page1.GetProperty("items").EnumerateArray().ToList();
        Assert.Equal(2, items1.Count);
        var ids1 = items1.Select(e => e.GetProperty("id").GetInt64()).ToList();
        Assert.True(ids1[0] > ids1[1]);
        var next = page1.GetProperty("next_before").GetInt64();
        Assert.Equal(ids1[1], next);

        var page2 = await client.GetFromJsonAsync<JsonElement>(
            $"/api/v1/audit?limit=2&before={next.ToString(CultureInfo.InvariantCulture)}");
        var ids2 = page2.GetProperty("items").EnumerateArray().Select(e => e.GetProperty("id").GetInt64()).ToList();
        Assert.NotEmpty(ids2);
        Assert.All(ids2, id => Assert.True(id < next));

        var row = items1[0];
        foreach (var field in new[] { "at", "action", "device_id", "actor_device_id", "detail" })
            Assert.True(row.TryGetProperty(field, out _), field);
    }

    [Fact]
    public async Task Audit_last_page_has_no_next_cursor()
    {
        var client = await ClientAsync("admin");

        var page = await client.GetFromJsonAsync<JsonElement>("/api/v1/audit?limit=2&before=1");

        Assert.Empty(page.GetProperty("items").EnumerateArray());
        Assert.Equal(JsonValueKind.Null, page.GetProperty("next_before").ValueKind);
    }

    [Fact]
    public async Task Audit_is_admin_only()
    {
        var device = await ClientAsync("device");
        Assert.Equal(HttpStatusCode.Forbidden, (await device.GetAsync("/api/v1/audit")).StatusCode);

        var anonymous = _factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/v1/audit")).StatusCode);
    }
}
