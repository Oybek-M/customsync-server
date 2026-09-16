using CustomSync.Tests.Fixtures;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using CustomSync.Api.Auth;
using CustomSync.Core;
using CustomSync.Core.Contracts;
using CustomSync.Services;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CustomSync.Tests;

public class MediaEndpointsTests : IClassFixture<CustomSyncWebApplicationFactory>, IDisposable
{
    private readonly WebApplicationFactory<Program> _factory;
    private readonly string _tempMediaRoot;

    public MediaEndpointsTests(CustomSyncWebApplicationFactory factory)
    {
        _tempMediaRoot = Path.Combine(Path.GetTempPath(), $"cs-media-test-{Guid.NewGuid():N}");
        _factory = factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((ctx, config) =>
            {
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Storage:MediaRoot"] = _tempMediaRoot
                });
            });
        });
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempMediaRoot))
        {
            try { Directory.Delete(_tempMediaRoot, recursive: true); } catch { }
        }
    }

    private async Task<(HttpClient Client, string DeviceId, string Token)> EnrolDeviceAsync(string role = "device")
    {
        using var scope = _factory.Services.CreateScope();
        var devices = scope.ServiceProvider.GetRequiredService<DeviceService>();
        var jwt     = scope.ServiceProvider.GetRequiredService<JwtIssuer>();

        var code     = await devices.CreateEnrollmentCodeAsync(role);
        var enrolled = await devices.RedeemAsync(code, $"test-{Guid.NewGuid():N}", "media-test");
        Assert.NotNull(enrolled);

        var (token, _) = await jwt.IssueAsync(enrolled.DeviceId, enrolled.Role);
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        return (client, enrolled.DeviceId, token);
    }

    [Fact]
    public async Task Put_get_and_head_work_for_valid_blob()
    {
        var (client, _, _) = await EnrolDeviceAsync();
        var hash = $"hash_{Guid.NewGuid():N}";
        var content = new byte[] { 10, 20, 30, 40, 50 };
        var nonce = Convert.ToBase64String(new byte[12]);

        // 1. Mavjud bo'lmagan hash uchun HEAD -> 404
        using var headBefore = new HttpRequestMessage(HttpMethod.Head, $"/api/v1/media/{hash}");
        var headBeforeResp = await client.SendAsync(headBefore);
        Assert.Equal(HttpStatusCode.NotFound, headBeforeResp.StatusCode);

        // 2. PUT orqali blob yuklash -> 200 OK
        using var putRequest = new HttpRequestMessage(HttpMethod.Put, $"/api/v1/media/{hash}")
        {
            Content = new ByteArrayContent(content)
        };
        putRequest.Headers.Add("X-Nonce", nonce);
        var putResp = await client.SendAsync(putRequest);
        Assert.Equal(HttpStatusCode.OK, putResp.StatusCode);

        // 3. HEAD orqali tekshirish -> 200 OK
        using var headAfter = new HttpRequestMessage(HttpMethod.Head, $"/api/v1/media/{hash}");
        var headAfterResp = await client.SendAsync(headAfter);
        Assert.Equal(HttpStatusCode.OK, headAfterResp.StatusCode);

        // 4. GET orqali yuklab olish -> bayt-ma-bayt bir xil
        var getResp = await client.GetAsync($"/api/v1/media/{hash}");
        Assert.Equal(HttpStatusCode.OK, getResp.StatusCode);
        var downloaded = await getResp.Content.ReadAsByteArrayAsync();
        Assert.Equal(content, downloaded);
    }

    [Fact]
    public async Task Put_larger_than_max_upload_bytes_returns_413()
    {
        var (client, _, _) = await EnrolDeviceAsync();
        var hash = $"hash_{Guid.NewGuid():N}";

        using var scope = _factory.Services.CreateScope();
        var settings = scope.ServiceProvider.GetRequiredService<SettingsService>();

        await settings.SetAsync("media.max_upload_bytes", "100");
        try
        {
            var largeContent = new byte[150];
            using var putRequest = new HttpRequestMessage(HttpMethod.Put, $"/api/v1/media/{hash}")
            {
                Content = new ByteArrayContent(largeContent)
            };
            var putResp = await client.SendAsync(putRequest);
            Assert.Equal(HttpStatusCode.RequestEntityTooLarge, putResp.StatusCode);
        }
        finally
        {
            await settings.SetAsync("media.max_upload_bytes", "52428800");
        }
    }

    [Fact]
    public async Task Put_over_quota_total_mb_returns_507()
    {
        var (client, _, _) = await EnrolDeviceAsync();

        using var scope = _factory.Services.CreateScope();
        var settings = scope.ServiceProvider.GetRequiredService<SettingsService>();
        var media    = scope.ServiceProvider.GetRequiredService<MediaService>();

        // Boshqa testlardan qolgan media qatorlarini hisobga olib,
        // kvotani joriy saqlangan hajm ustiga dinamik o'rnatamiz.
        var currentBytes = await media.GetTotalStoredBytesAsync();
        var quotaMb = (int)((currentBytes + 500 * 1024) / (1024 * 1024)) + 1;
        var maxBytes = (long)quotaMb * 1024L * 1024L;
        var headroom = maxBytes - currentBytes;

        await settings.SetAsync("storage.quota_total_mb", quotaMb.ToString());
        try
        {
            // headroom / 2 yuklaymiz -> o'tadi
            var firstSize = (int)(headroom / 2);
            var hash1 = $"hash_q1_{Guid.NewGuid():N}";
            using var req1 = new HttpRequestMessage(HttpMethod.Put, $"/api/v1/media/{hash1}")
            {
                Content = new ByteArrayContent(new byte[firstSize])
            };
            var resp1 = await client.SendAsync(req1);
            Assert.Equal(HttpStatusCode.OK, resp1.StatusCode);

            // headroom yuklaymiz -> jami hajm maxBytes'dan oshadi -> 507 Insufficient Storage
            var secondSize = (int)headroom;
            var hash2 = $"hash_q2_{Guid.NewGuid():N}";
            using var req2 = new HttpRequestMessage(HttpMethod.Put, $"/api/v1/media/{hash2}")
            {
                Content = new ByteArrayContent(new byte[secondSize])
            };
            var resp2 = await client.SendAsync(req2);
            Assert.Equal((HttpStatusCode)507, resp2.StatusCode);
        }
        finally
        {
            await settings.SetAsync("storage.quota_total_mb", "0");
        }
    }

    [Fact]
    public async Task Put_over_quota_per_device_mb_returns_507()
    {
        var (client1, _, _) = await EnrolDeviceAsync();
        var (client2, _, _) = await EnrolDeviceAsync();

        using var scope = _factory.Services.CreateScope();
        var settings = scope.ServiceProvider.GetRequiredService<SettingsService>();

        // 1 MB qurilma kvotasi qo'yamiz
        await settings.SetAsync("storage.quota_per_device_mb", "1");
        try
        {
            // Qurilma 1: 700 KB yuklaydi -> o'tadi
            var hash1 = $"hash_dev1_{Guid.NewGuid():N}";
            using var req1 = new HttpRequestMessage(HttpMethod.Put, $"/api/v1/media/{hash1}")
            {
                Content = new ByteArrayContent(new byte[700 * 1024])
            };
            var resp1 = await client1.SendAsync(req1);
            Assert.Equal(HttpStatusCode.OK, resp1.StatusCode);

            // Qurilma 1: yana 400 KB yuklaydi -> 507
            var hash2 = $"hash_dev2_{Guid.NewGuid():N}";
            using var req2 = new HttpRequestMessage(HttpMethod.Put, $"/api/v1/media/{hash2}")
            {
                Content = new ByteArrayContent(new byte[400 * 1024])
            };
            var resp2 = await client1.SendAsync(req2);
            Assert.Equal((HttpStatusCode)507, resp2.StatusCode);

            // Qurilma 2: o'zining 400 KB faylini yuklaydi -> o'tadi (chunki o'z kvotasi to'lmagan)
            var hash3 = $"hash_dev3_{Guid.NewGuid():N}";
            using var req3 = new HttpRequestMessage(HttpMethod.Put, $"/api/v1/media/{hash3}")
            {
                Content = new ByteArrayContent(new byte[400 * 1024])
            };
            var resp3 = await client2.SendAsync(req3);
            Assert.Equal(HttpStatusCode.OK, resp3.StatusCode);
        }
        finally
        {
            await settings.SetAsync("storage.quota_per_device_mb", "0");
        }
    }

    [Fact]
    public async Task Media_requests_without_token_return_401()
    {
        var client = _factory.CreateClient();
        var hash = $"hash_{Guid.NewGuid():N}";

        using var head = new HttpRequestMessage(HttpMethod.Head, $"/api/v1/media/{hash}");
        var headResp = await client.SendAsync(head);
        Assert.Equal(HttpStatusCode.Unauthorized, headResp.StatusCode);

        var getResp = await client.GetAsync($"/api/v1/media/{hash}");
        Assert.Equal(HttpStatusCode.Unauthorized, getResp.StatusCode);

        using var put = new HttpRequestMessage(HttpMethod.Put, $"/api/v1/media/{hash}")
        {
            Content = new ByteArrayContent([1, 2, 3])
        };
        var putResp = await client.SendAsync(put);
        Assert.Equal(HttpStatusCode.Unauthorized, putResp.StatusCode);
    }

    [Fact]
    public async Task Pushed_record_with_media_hashes_survives_round_trip()
    {
        var (client, _, _) = await EnrolDeviceAsync();
        var peer = $"peer_media_{Guid.NewGuid():N}";
        var hash1 = $"media_h1_{Guid.NewGuid():N}";
        var hash2 = $"media_h2_{Guid.NewGuid():N}";

        var recordId = RecordId.Compute("edited", "acc01", peer, 1, 1753900000L);
        var record = new
        {
            record_id = recordId,
            kind        = "edited",
            account_hash = "acc01",
            peer_hash    = peer,
            msg_id       = 1L,
            occurred_at  = 1753900000L,
            observed_at  = 1753900001L,
            device_id    = "test-device",
            nonce       = Convert.ToBase64String(new byte[12]),
            payload     = Convert.ToBase64String(new byte[] { 1, 2, 3 }),
            media       = new[]
            {
                new { hash = hash1, size = 100L, nonce = Convert.ToBase64String(new byte[12]) },
                new { hash = hash2, size = 200L, nonce = Convert.ToBase64String(new byte[12]) }
            }
        };

        // 1. Push
        var pushResp = await client.PostAsJsonAsync("/api/v1/sync/push", new { records = new[] { record } });
        Assert.Equal(HttpStatusCode.OK, pushResp.StatusCode);

        // Pull'ni O'Z push'imizdan boshlaymiz -- `since=0` dev bazasi
        // o'sgani sari yiqiladi (yozuv birinchi sahifadan chiqib ketadi).
        var pushJson = await pushResp.Content.ReadFromJsonAsync<JsonElement>();
        var since = pushJson.GetProperty("results").EnumerateArray()
            .Where(r => r.TryGetProperty("seq", out var sq) && sq.ValueKind == JsonValueKind.Number)
            .Select(r => r.GetProperty("seq").GetInt64())
            .DefaultIfEmpty(1)
            .Min() - 1;

        // 2. Pull qilib tekshirish
        var pullResp = await client.GetAsync($"/api/v1/sync/pull?since={(since < 0 ? 0 : since)}&limit=500");
        Assert.Equal(HttpStatusCode.OK, pullResp.StatusCode);

        var pullBody = await pullResp.Content.ReadFromJsonAsync<JsonElement>();
        var records = pullBody.GetProperty("records").EnumerateArray().ToList();
        var ours = records.FirstOrDefault(r => r.GetProperty("record_id").GetString() == recordId);
        Assert.True(ours.ValueKind != JsonValueKind.Undefined, "Yozuv pull ro'yxatida topilmadi");

        var mediaHashes = ours.GetProperty("media_hashes").EnumerateArray()
            .Select(h => h.GetString()!)
            .ToList();
        Assert.Contains(hash1, mediaHashes);
        Assert.Contains(hash2, mediaHashes);
        Assert.Equal(2, mediaHashes.Count);

        // 3. Qayta push qilinganda dublikatlar ko'payib ketmasligini tekshirish (idempotentlik, K4)
        var repushResp = await client.PostAsJsonAsync("/api/v1/sync/push", new { records = new[] { record } });
        Assert.Equal(HttpStatusCode.OK, repushResp.StatusCode);

        var pullAgain = await client.GetAsync($"/api/v1/sync/pull?since={(since < 0 ? 0 : since)}&limit=500");
        var pullAgainBody = await pullAgain.Content.ReadFromJsonAsync<JsonElement>();
        var recordsAgain = pullAgainBody.GetProperty("records").EnumerateArray().ToList();
        var oursAgain = recordsAgain.First(r => r.GetProperty("record_id").GetString() == recordId);
        var mediaAgain = oursAgain.GetProperty("media_hashes").EnumerateArray()
            .Select(h => h.GetString()!)
            .ToList();
        Assert.Equal(2, mediaAgain.Count);
    }
}
