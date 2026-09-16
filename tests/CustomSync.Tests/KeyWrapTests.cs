using CustomSync.Tests.Fixtures;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using CustomSync.Api.Auth;
using CustomSync.Data;
using CustomSync.Services;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CustomSync.Tests;

public class KeyWrapTests : IClassFixture<CustomSyncWebApplicationFactory>
{
    private readonly CustomSyncWebApplicationFactory _factory;

    public KeyWrapTests(CustomSyncWebApplicationFactory factory) => _factory = factory;

    private async Task<(HttpClient Client, string DeviceId, string Token)> EnrolDeviceAsync(string role = "device")
    {
        using var scope = _factory.Services.CreateScope();
        var devices = scope.ServiceProvider.GetRequiredService<DeviceService>();
        var jwt     = scope.ServiceProvider.GetRequiredService<JwtIssuer>();

        var code     = await devices.CreateEnrollmentCodeAsync(role);
        var enrolled = await devices.RedeemAsync(code, $"test-{Guid.NewGuid():N}", "keywrap-test");
        Assert.NotNull(enrolled);

        var (token, _) = await jwt.IssueAsync(enrolled.DeviceId, enrolled.Role);
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        return (client, enrolled.DeviceId, token);
    }

    private static object MakeWrapPayload(string label) => new
    {
        wrap_type   = "passphrase",
        label,
        salt       = Convert.ToBase64String(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16 }),
        nonce      = Convert.ToBase64String(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12 }),
        wrapped_key = Convert.ToBase64String(new byte[] { 10, 20, 30, 40 }),
        iterations = 600000
    };

    [Fact]
    public async Task Post_as_admin_creates_wrap_and_get_lists_it()
    {
        var (adminClient, _, _) = await EnrolDeviceAsync("admin");
        var (deviceClient, _, _) = await EnrolDeviceAsync("device");

        var label = $"label_{Guid.NewGuid():N}";
        var postResp = await adminClient.PostAsJsonAsync("/api/v1/keys/wraps", MakeWrapPayload(label));
        Assert.Equal(HttpStatusCode.OK, postResp.StatusCode);

        var postBody = await postResp.Content.ReadFromJsonAsync<JsonElement>();
        var wrapId = postBody.GetProperty("wrap_id").GetString();
        Assert.NotNull(wrapId);

        // GET / oddiy qurilma uchun ham ro'yxatni qaytarishi kerak
        var listResp = await deviceClient.GetAsync("/api/v1/keys/wraps");
        Assert.Equal(HttpStatusCode.OK, listResp.StatusCode);

        var list = await listResp.Content.ReadFromJsonAsync<List<JsonElement>>();
        Assert.NotNull(list);
        var found = list.FirstOrDefault(w => w.GetProperty("wrap_id").GetString() == wrapId);
        Assert.True(found.ValueKind != JsonValueKind.Undefined, "Yaratilgan wrap ro'yxatda topilmadi");
        Assert.Equal(label, found.GetProperty("label").GetString());
    }

    [Fact]
    public async Task Get_by_id_returns_base64_fields_and_updates_last_used_at()
    {
        var (adminClient, _, _) = await EnrolDeviceAsync("admin");
        var (deviceClient, _, _) = await EnrolDeviceAsync("device");

        var label = $"label_{Guid.NewGuid():N}";
        var postResp = await adminClient.PostAsJsonAsync("/api/v1/keys/wraps", MakeWrapPayload(label));
        var postBody = await postResp.Content.ReadFromJsonAsync<JsonElement>();
        var wrapId = postBody.GetProperty("wrap_id").GetString()!;

        // GET /{wrapId}
        var getResp = await deviceClient.GetAsync($"/api/v1/keys/wraps/{wrapId}");
        Assert.Equal(HttpStatusCode.OK, getResp.StatusCode);

        var getBody = await getResp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(wrapId, getBody.GetProperty("wrap_id").GetString());
        Assert.Equal("passphrase", getBody.GetProperty("wrap_type").GetString());
        Assert.Equal(label, getBody.GetProperty("label").GetString());
        Assert.NotEmpty(getBody.GetProperty("salt").GetString()!);
        Assert.NotEmpty(getBody.GetProperty("nonce").GetString()!);
        Assert.NotEmpty(getBody.GetProperty("wrapped_key").GetString()!);
        Assert.Equal(600000, getBody.GetProperty("iterations").GetInt32());

        // LastUsedAt bazada yangilanganini tekshirish
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SyncDbContext>();
        var entity = await db.KeyWraps.AsNoTracking().FirstOrDefaultAsync(w => w.WrapId == wrapId);
        Assert.NotNull(entity);
        Assert.NotNull(entity.LastUsedAt);
    }

    [Fact]
    public async Task Get_by_id_for_unknown_id_returns_404()
    {
        var (deviceClient, _, _) = await EnrolDeviceAsync("device");
        var fakeId = Guid.NewGuid().ToString("N");

        var resp = await deviceClient.GetAsync($"/api/v1/keys/wraps/{fakeId}");
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    [Fact]
    public async Task Delete_as_admin_removes_wrap_and_second_delete_is_harmless()
    {
        var (adminClient, _, _) = await EnrolDeviceAsync("admin");
        var (deviceClient, _, _) = await EnrolDeviceAsync("device");

        var label = $"label_{Guid.NewGuid():N}";
        var postResp = await adminClient.PostAsJsonAsync("/api/v1/keys/wraps", MakeWrapPayload(label));
        var postBody = await postResp.Content.ReadFromJsonAsync<JsonElement>();
        var wrapId = postBody.GetProperty("wrap_id").GetString()!;

        // 1-marta DELETE -> 204 NoContent
        var del1 = await adminClient.DeleteAsync($"/api/v1/keys/wraps/{wrapId}");
        Assert.Equal(HttpStatusCode.NoContent, del1.StatusCode);

        // GET /{id} -> 404
        var getResp = await deviceClient.GetAsync($"/api/v1/keys/wraps/{wrapId}");
        Assert.Equal(HttpStatusCode.NotFound, getResp.StatusCode);

        // 2-marta DELETE -> 204 NoContent (idempotent, K4)
        var del2 = await adminClient.DeleteAsync($"/api/v1/keys/wraps/{wrapId}");
        Assert.Equal(HttpStatusCode.NoContent, del2.StatusCode);
    }

    [Fact]
    public async Task Post_as_device_role_returns_403()
    {
        var (deviceClient, _, _) = await EnrolDeviceAsync("device");
        var label = $"label_{Guid.NewGuid():N}";

        var resp = await deviceClient.PostAsJsonAsync("/api/v1/keys/wraps", MakeWrapPayload(label));
        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
    }

    [Fact]
    public async Task Delete_as_device_role_returns_403()
    {
        var (deviceClient, _, _) = await EnrolDeviceAsync("device");
        var wrapId = Guid.NewGuid().ToString("N");

        var resp = await deviceClient.DeleteAsync($"/api/v1/keys/wraps/{wrapId}");
        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
    }

    [Fact]
    public async Task Wraps_requests_with_no_token_return_401()
    {
        var client = _factory.CreateClient();
        var wrapId = Guid.NewGuid().ToString("N");

        var listResp = await client.GetAsync("/api/v1/keys/wraps");
        Assert.Equal(HttpStatusCode.Unauthorized, listResp.StatusCode);

        var getResp = await client.GetAsync($"/api/v1/keys/wraps/{wrapId}");
        Assert.Equal(HttpStatusCode.Unauthorized, getResp.StatusCode);

        var postResp = await client.PostAsJsonAsync("/api/v1/keys/wraps", MakeWrapPayload("test"));
        Assert.Equal(HttpStatusCode.Unauthorized, postResp.StatusCode);

        var delResp = await client.DeleteAsync($"/api/v1/keys/wraps/{wrapId}");
        Assert.Equal(HttpStatusCode.Unauthorized, delResp.StatusCode);
    }

    [Fact]
    public async Task Audit_rows_exist_for_created_retrieved_and_deleted_with_acting_device()
    {
        var (adminClient, adminDevId, _) = await EnrolDeviceAsync("admin");
        var (userClient, userDevId, _) = await EnrolDeviceAsync("device");

        var label = $"audit_label_{Guid.NewGuid():N}";

        // 1. Create
        var postResp = await adminClient.PostAsJsonAsync("/api/v1/keys/wraps", MakeWrapPayload(label));
        var postBody = await postResp.Content.ReadFromJsonAsync<JsonElement>();
        var wrapId = postBody.GetProperty("wrap_id").GetString()!;

        // 2. Retrieve
        var getResp = await userClient.GetAsync($"/api/v1/keys/wraps/{wrapId}");
        Assert.Equal(HttpStatusCode.OK, getResp.StatusCode);

        // 3. Delete
        var delResp = await adminClient.DeleteAsync($"/api/v1/keys/wraps/{wrapId}");
        Assert.Equal(HttpStatusCode.NoContent, delResp.StatusCode);

        // Audit jurnali tekshiruvi
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SyncDbContext>();

        var logs = await db.AuditLogs.AsNoTracking()
            .Where(a => a.Detail != null && a.Detail.Contains(wrapId))
            .ToListAsync();

        var createdLog = logs.FirstOrDefault(a => a.Action == "keywrap.created");
        Assert.NotNull(createdLog);
        Assert.Equal(adminDevId, createdLog.ActorDeviceId);

        var retrievedLog = logs.FirstOrDefault(a => a.Action == "keywrap.retrieved");
        Assert.NotNull(retrievedLog);
        Assert.Equal(userDevId, retrievedLog.ActorDeviceId);

        var deletedLog = logs.FirstOrDefault(a => a.Action == "keywrap.deleted");
        Assert.NotNull(deletedLog);
        Assert.Equal(adminDevId, deletedLog.ActorDeviceId);
    }

    [Fact]
    public async Task Exceeding_auth_wrap_rate_per_hour_returns_429()
    {
        var (adminClient, _, _) = await EnrolDeviceAsync("admin");
        var (deviceA, _, _) = await EnrolDeviceAsync("device");
        var (deviceB, _, _) = await EnrolDeviceAsync("device");

        var label = $"rate_label_{Guid.NewGuid():N}";
        var postResp = await adminClient.PostAsJsonAsync("/api/v1/keys/wraps", MakeWrapPayload(label));
        var postBody = await postResp.Content.ReadFromJsonAsync<JsonElement>();
        var wrapId = postBody.GetProperty("wrap_id").GetString()!;

        using var scope = _factory.Services.CreateScope();
        var settings = scope.ServiceProvider.GetRequiredService<SettingsService>();

        // auth.wrap_rate_per_hour ni vaqtincha 2 ga o'rnatamiz
        await settings.SetAsync("auth.wrap_rate_per_hour", "2");
        try
        {
            // Device A: 1-so'rov -> 200 OK
            var r1 = await deviceA.GetAsync($"/api/v1/keys/wraps/{wrapId}");
            Assert.Equal(HttpStatusCode.OK, r1.StatusCode);

            // Device A: 2-so'rov -> 200 OK
            var r2 = await deviceA.GetAsync($"/api/v1/keys/wraps/{wrapId}");
            Assert.Equal(HttpStatusCode.OK, r2.StatusCode);

            // Device A: 3-so'rov -> 429 Too Many Requests
            var r3 = await deviceA.GetAsync($"/api/v1/keys/wraps/{wrapId}");
            Assert.Equal(HttpStatusCode.TooManyRequests, r3.StatusCode);

            // Device B alohida partitsiyaga ega bo'lgani uchun unga ta'sir qilmaydi -> 200 OK
            var rB = await deviceB.GetAsync($"/api/v1/keys/wraps/{wrapId}");
            Assert.Equal(HttpStatusCode.OK, rB.StatusCode);
        }
        finally
        {
            await settings.SetAsync("auth.wrap_rate_per_hour", "5");
        }
    }
}
