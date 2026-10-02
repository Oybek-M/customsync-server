using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using CustomSync.Api.Auth;
using CustomSync.Data;
using CustomSync.Data.Entities;
using CustomSync.Services;
using CustomSync.Tests.Fixtures;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CustomSync.Tests;

public class DeviceHealthEndpointsTests : IClassFixture<CustomSyncWebApplicationFactory>
{
    private readonly CustomSyncWebApplicationFactory _factory;

    public DeviceHealthEndpointsTests(CustomSyncWebApplicationFactory factory) => _factory = factory;

    private async Task<(HttpClient Client, string DeviceId, string Token)> CreateEnrolledDeviceAsync(string role = "device")
    {
        using var scope = _factory.Services.CreateScope();
        var devices = scope.ServiceProvider.GetRequiredService<DeviceService>();
        var jwt = scope.ServiceProvider.GetRequiredService<JwtIssuer>();

        var code = await devices.CreateEnrollmentCodeAsync(role);
        var enrolled = await devices.RedeemAsync(code, $"dev-{Guid.NewGuid():N}", "linux");
        Assert.NotNull(enrolled);

        var (token, _) = await jwt.IssueAsync(enrolled.DeviceId, enrolled.Role);
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        return (client, enrolled.DeviceId, token);
    }

    // 1. A valid POST with a device token -> 204; the row is stored under the token's device id;
    // reported_at is within a few seconds of the server's now even though the body carries a reported_at far in the past.
    [Fact]
    public async Task Test01_PostHealth_valid_token_stores_row_and_uses_server_now()
    {
        var (client, deviceId, _) = await CreateEnrolledDeviceAsync();

        var pastTime = "2020-01-01T00:00:00Z";
        var payload = new Dictionary<string, object?>
        {
            ["rss_bytes"] = 100_000_000L,
            ["memory_limit_bytes"] = 1_000_000_000L,
            ["cache_db_bytes"] = 2_000_000L,
            ["media_store_bytes"] = 50_000_000L,
            ["media_store_files"] = 120,
            ["tdlib_files_bytes"] = 30_000_000L,
            ["tdlib_database_bytes"] = 10_000_000L,
            ["free_disk_bytes"] = 500_000_000_000L,
            ["reported_at"] = pastTime, // should be ignored by server
            ["device_id"] = "fake-device-id" // should be ignored by server
        };

        var response = await client.PostAsJsonAsync("/api/v1/devices/health", payload);
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SyncDbContext>();
        var row = await db.DeviceHealth.FirstOrDefaultAsync(h => h.DeviceId == deviceId);
        Assert.NotNull(row);
        Assert.Equal(100_000_000L, row.RssBytes);
        Assert.Equal(1_000_000_000L, row.MemoryLimitBytes);
        Assert.Equal(2_000_000L, row.CacheDbBytes);
        Assert.Equal(50_000_000L, row.MediaStoreBytes);
        Assert.Equal(120, row.MediaStoreFiles);
        Assert.Equal(30_000_000L, row.TdlibFilesBytes);
        Assert.Equal(10_000_000L, row.TdlibDatabaseBytes);
        Assert.Equal(500_000_000_000L, row.FreeDiskBytes);

        // Server's now must be used, not 2020
        var now = DateTime.UtcNow;
        Assert.True((now - row.ReportedAt).TotalSeconds < 30, "ReportedAt must be close to server now");
        Assert.True(row.ReportedAt.Year >= 2026, "ReportedAt must not be the 2020 client time");
    }

    // 2. A body naming another device's device_id -> stored under the caller only;
    // the other device's row is absent or unchanged.
    [Fact]
    public async Task Test02_PostHealth_body_naming_another_device_stores_under_caller_only()
    {
        var (clientCaller, callerId, _) = await CreateEnrolledDeviceAsync();
        var (_, otherId, _) = await CreateEnrolledDeviceAsync();

        var payload = new Dictionary<string, object?>
        {
            ["device_id"] = otherId, // malicious device attempts to overwrite other device
            ["rss_bytes"] = 42_000_000L,
            ["cache_db_bytes"] = 1_000_000L,
            ["media_store_bytes"] = 2_000_000L,
            ["media_store_files"] = 10
        };

        var response = await clientCaller.PostAsJsonAsync("/api/v1/devices/health", payload);
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SyncDbContext>();

        var callerRow = await db.DeviceHealth.FirstOrDefaultAsync(h => h.DeviceId == callerId);
        Assert.NotNull(callerRow);
        Assert.Equal(42_000_000L, callerRow.RssBytes);

        var otherRow = await db.DeviceHealth.FirstOrDefaultAsync(h => h.DeviceId == otherId);
        Assert.Null(otherRow);
    }

    // 3. Two POSTs -> one row, holding the second values.
    [Fact]
    public async Task Test03_PostHealth_two_posts_upserts_single_row()
    {
        var (client, deviceId, _) = await CreateEnrolledDeviceAsync();

        var payload1 = new Dictionary<string, object?>
        {
            ["rss_bytes"] = 10_000_000L,
            ["cache_db_bytes"] = 1_000_000L,
            ["media_store_bytes"] = 1_000_000L,
            ["media_store_files"] = 5
        };
        var resp1 = await client.PostAsJsonAsync("/api/v1/devices/health", payload1);
        Assert.Equal(HttpStatusCode.NoContent, resp1.StatusCode);

        var payload2 = new Dictionary<string, object?>
        {
            ["rss_bytes"] = 20_000_000L,
            ["cache_db_bytes"] = 2_000_000L,
            ["media_store_bytes"] = 3_000_000L,
            ["media_store_files"] = 15
        };
        var resp2 = await client.PostAsJsonAsync("/api/v1/devices/health", payload2);
        Assert.Equal(HttpStatusCode.NoContent, resp2.StatusCode);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SyncDbContext>();
        var rows = await db.DeviceHealth.Where(h => h.DeviceId == deviceId).ToListAsync();
        Assert.Single(rows);
        Assert.Equal(20_000_000L, rows[0].RssBytes);
        Assert.Equal(15, rows[0].MediaStoreFiles);
    }

    // 4. [Theory] invalid bodies — a negative value, 9007199254740992, a missing required field,
    // a number as a string, media_store_files above the limit -> 400 and the stored row unchanged.
    [Theory]
    [InlineData("negative_rss", """{"rss_bytes": -1, "cache_db_bytes": 100, "media_store_bytes": 100, "media_store_files": 1}""")]
    [InlineData("too_large_rss", """{"rss_bytes": 9007199254740992, "cache_db_bytes": 100, "media_store_bytes": 100, "media_store_files": 1}""")]
    [InlineData("missing_required", """{"rss_bytes": 100, "cache_db_bytes": 100, "media_store_files": 1}""")]
    [InlineData("number_as_string", """{"rss_bytes": "100", "cache_db_bytes": 100, "media_store_bytes": 100, "media_store_files": 1}""")]
    [InlineData("files_over_limit", """{"rss_bytes": 100, "cache_db_bytes": 100, "media_store_bytes": 100, "media_store_files": 1000000001}""")]
    [InlineData("negative_files", """{"rss_bytes": 100, "cache_db_bytes": 100, "media_store_bytes": 100, "media_store_files": -1}""")]
    [InlineData("negative_nullable", """{"rss_bytes": 100, "cache_db_bytes": 100, "media_store_bytes": 100, "media_store_files": 1, "free_disk_bytes": -10}""")]
    public async Task Test04_PostHealth_invalid_bodies_return_400_and_row_unchanged(string caseName, string json)
    {
        Assert.NotEmpty(caseName);
        var (client, deviceId, _) = await CreateEnrolledDeviceAsync();

        // Seed an initial valid row
        var initial = new Dictionary<string, object?>
        {
            ["rss_bytes"] = 55_000_000L,
            ["cache_db_bytes"] = 1_000_000L,
            ["media_store_bytes"] = 2_000_000L,
            ["media_store_files"] = 10
        };
        var initResp = await client.PostAsJsonAsync("/api/v1/devices/health", initial);
        Assert.Equal(HttpStatusCode.NoContent, initResp.StatusCode);

        // Now post invalid body
        var content = new StringContent(json, Encoding.UTF8, "application/json");
        var response = await client.PostAsync("/api/v1/devices/health", content);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        // Verify stored row is unchanged
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SyncDbContext>();
        var row = await db.DeviceHealth.FirstOrDefaultAsync(h => h.DeviceId == deviceId);
        Assert.NotNull(row);
        Assert.Equal(55_000_000L, row.RssBytes);
    }

    // 5. A body of more than 4096 bytes -> 413, nothing stored.
    [Fact]
    public async Task Test05_PostHealth_body_larger_than_4096_bytes_returns_413()
    {
        var (client, deviceId, _) = await CreateEnrolledDeviceAsync();

        // Build a valid JSON structure padded with large ignored dummy property to exceed 4096 bytes
        var dummyPadding = new string('x', 4500);
        var json = $"{{\"rss_bytes\":100,\"cache_db_bytes\":100,\"media_store_bytes\":100,\"media_store_files\":1,\"padding\":\"{dummyPadding}\"}}";
        Assert.True(Encoding.UTF8.GetByteCount(json) > 4096);

        var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/devices/health");
        request.Headers.TransferEncodingChunked = true;
        var streamContent = new StreamContent(new NonSeekableStream(Encoding.UTF8.GetBytes(json)));
        streamContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
        request.Content = streamContent;
        var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SyncDbContext>();
        var row = await db.DeviceHealth.FirstOrDefaultAsync(h => h.DeviceId == deviceId);
        Assert.Null(row);
    }

    // 6. No token -> 401; a revoked device's token -> 401.
    [Fact]
    public async Task Test06_PostHealth_no_token_or_revoked_token_returns_401()
    {
        var anonymousClient = _factory.CreateClient();
        var payload = new Dictionary<string, object?>
        {
            ["rss_bytes"] = 100L,
            ["cache_db_bytes"] = 100L,
            ["media_store_bytes"] = 100L,
            ["media_store_files"] = 1
        };

        var noTokenResp = await anonymousClient.PostAsJsonAsync("/api/v1/devices/health", payload);
        Assert.Equal(HttpStatusCode.Unauthorized, noTokenResp.StatusCode);

        // Revoked device token
        var (client, deviceId, _) = await CreateEnrolledDeviceAsync();
        using (var scope = _factory.Services.CreateScope())
        {
            var devices = scope.ServiceProvider.GetRequiredService<DeviceService>();
            await devices.RevokeAsync(deviceId);
        }

        var revokedResp = await client.PostAsJsonAsync("/api/v1/devices/health", payload);
        Assert.Equal(HttpStatusCode.Unauthorized, revokedResp.StatusCode);
    }

    // 7. GET as admin -> the entry has name, platform, revoked and the metrics;
    // after moving reported_at back past health.stale_after_seconds (a database update in the test) stale is true, otherwise false;
    // changing the setting changes the result.
    [Fact]
    public async Task Test07_GetHealth_as_admin_returns_metrics_and_respects_stale_setting()
    {
        var (adminClient, _, _) = await CreateEnrolledDeviceAsync(role: "admin");
        var (deviceClient, devId, _) = await CreateEnrolledDeviceAsync(role: "device");

        var payload = new Dictionary<string, object?>
        {
            ["rss_bytes"] = 80_000_000L,
            ["cache_db_bytes"] = 5_000_000L,
            ["media_store_bytes"] = 10_000_000L,
            ["media_store_files"] = 25
        };
        var postResp = await deviceClient.PostAsJsonAsync("/api/v1/devices/health", payload);
        Assert.Equal(HttpStatusCode.NoContent, postResp.StatusCode);

        // Fresh report -> stale is false
        var getResp = await adminClient.GetAsync("/api/v1/devices/health");
        Assert.Equal(HttpStatusCode.OK, getResp.StatusCode);
        var list = await getResp.Content.ReadFromJsonAsync<List<JsonElement>>(TestJson.Options);
        Assert.NotNull(list);
        var entry = Assert.Single(list.Where(e => e.GetProperty("device_id").GetString() == devId));
        Assert.False(entry.GetProperty("stale").GetBoolean());
        Assert.Equal(80_000_000L, entry.GetProperty("rss_bytes").GetInt64());
        Assert.Equal(25, entry.GetProperty("media_store_files").GetInt64());
        Assert.False(entry.GetProperty("revoked").GetBoolean());

        // Now move reported_at back 2000 seconds (setting default is 1800)
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<SyncDbContext>();
            var row = await db.DeviceHealth.FirstAsync(h => h.DeviceId == devId);
            row.ReportedAt = DateTime.UtcNow.AddSeconds(-2000);
            await db.SaveChangesAsync();
        }

        // Stale should now be true
        getResp = await adminClient.GetAsync("/api/v1/devices/health");
        Assert.Equal(HttpStatusCode.OK, getResp.StatusCode);
        list = await getResp.Content.ReadFromJsonAsync<List<JsonElement>>(TestJson.Options);
        entry = Assert.Single(list!.Where(e => e.GetProperty("device_id").GetString() == devId));
        Assert.True(entry.GetProperty("stale").GetBoolean());

        // Now change setting to 3600 seconds -> stale becomes false again!
        using (var scope = _factory.Services.CreateScope())
        {
            var settings = scope.ServiceProvider.GetRequiredService<SettingsService>();
            await settings.SetAsync("health.stale_after_seconds", "3600");
        }

        try
        {
            getResp = await adminClient.GetAsync("/api/v1/devices/health");
            Assert.Equal(HttpStatusCode.OK, getResp.StatusCode);
            list = await getResp.Content.ReadFromJsonAsync<List<JsonElement>>(TestJson.Options);
            entry = Assert.Single(list!.Where(e => e.GetProperty("device_id").GetString() == devId));
            Assert.False(entry.GetProperty("stale").GetBoolean(), "With 3600s setting, 2000s age is not stale");
        }
        finally
        {
            // Reset setting
            using var scope = _factory.Services.CreateScope();
            var settings = scope.ServiceProvider.GetRequiredService<SettingsService>();
            await settings.SetAsync("health.stale_after_seconds", "1800");
        }
    }

    // 8. GET with a device-role token -> 403.
    [Fact]
    public async Task Test08_GetHealth_with_device_role_token_returns_403()
    {
        var (deviceClient, _, _) = await CreateEnrolledDeviceAsync(role: "device");
        var response = await deviceClient.GetAsync("/api/v1/devices/health");
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    // 9. health.stale_after_seconds exists with default 1800.
    [Fact]
    public void Test09_Setting_health_stale_after_seconds_exists_with_default_1800()
    {
        var defaults = SettingsService.CreateDefaults();
        var setting = Assert.Single(defaults.Where(s => s.Key == "health.stale_after_seconds"));
        Assert.Equal("1800", setting.Value);
        Assert.Equal("int", setting.ValueType);
        Assert.Equal("health", setting.Category);
    }
}

file sealed class NonSeekableStream : Stream
{
    private readonly MemoryStream _inner;
    public NonSeekableStream(byte[] bytes) => _inner = new MemoryStream(bytes);
    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
