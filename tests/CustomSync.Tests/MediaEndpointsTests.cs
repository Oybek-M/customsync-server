using CustomSync.Tests.Fixtures;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using CustomSync.Api.Auth;
using CustomSync.Core;
using CustomSync.Core.Contracts;
using CustomSync.Services;
using CustomSync.Data;
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

    private static string NewValidHash() => Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();

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
        var hash = NewValidHash();
        var content = new byte[] { 10, 20, 30, 40, 50 };
        var nonceBytes = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12 };
        var nonce = Convert.ToBase64String(nonceBytes);

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

        // 3. HEAD orqali tekshirish -> 200 OK va X-Nonce qaytadi
        using var headAfter = new HttpRequestMessage(HttpMethod.Head, $"/api/v1/media/{hash}");
        var headAfterResp = await client.SendAsync(headAfter);
        Assert.Equal(HttpStatusCode.OK, headAfterResp.StatusCode);
        Assert.True(headAfterResp.Headers.Contains("X-Nonce"));
        Assert.Equal(nonce, headAfterResp.Headers.GetValues("X-Nonce").Single());

        // 4. GET orqali yuklab olish -> bayt-ma-bayt bir xil va X-Nonce qaytadi
        var getResp = await client.GetAsync($"/api/v1/media/{hash}");
        Assert.Equal(HttpStatusCode.OK, getResp.StatusCode);
        Assert.True(getResp.Headers.Contains("X-Nonce"));
        Assert.Equal(nonce, getResp.Headers.GetValues("X-Nonce").Single());
        var downloaded = await getResp.Content.ReadAsByteArrayAsync();
        Assert.Equal(content, downloaded);
    }

    [Fact]
    public async Task Put_larger_than_max_upload_bytes_returns_413()
    {
        var (client, _, _) = await EnrolDeviceAsync();
        var hash = NewValidHash();

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
            putRequest.Headers.Add("X-Nonce", Convert.ToBase64String(new byte[12]));
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
            var hash1 = NewValidHash();
            using var req1 = new HttpRequestMessage(HttpMethod.Put, $"/api/v1/media/{hash1}")
            {
                Content = new ByteArrayContent(new byte[firstSize])
            };
            req1.Headers.Add("X-Nonce", Convert.ToBase64String(new byte[12]));
            var resp1 = await client.SendAsync(req1);
            Assert.Equal(HttpStatusCode.OK, resp1.StatusCode);

            // headroom yuklaymiz -> jami hajm maxBytes'dan oshadi -> 507 Insufficient Storage
            var secondSize = (int)headroom;
            var hash2 = NewValidHash();
            using var req2 = new HttpRequestMessage(HttpMethod.Put, $"/api/v1/media/{hash2}")
            {
                Content = new ByteArrayContent(new byte[secondSize])
            };
            req2.Headers.Add("X-Nonce", Convert.ToBase64String(new byte[12]));
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
            var hash1 = NewValidHash();
            using var req1 = new HttpRequestMessage(HttpMethod.Put, $"/api/v1/media/{hash1}")
            {
                Content = new ByteArrayContent(new byte[700 * 1024])
            };
            req1.Headers.Add("X-Nonce", Convert.ToBase64String(new byte[12]));
            var resp1 = await client1.SendAsync(req1);
            Assert.Equal(HttpStatusCode.OK, resp1.StatusCode);

            // Qurilma 1: yana 400 KB yuklaydi -> 507
            var hash2 = NewValidHash();
            using var req2 = new HttpRequestMessage(HttpMethod.Put, $"/api/v1/media/{hash2}")
            {
                Content = new ByteArrayContent(new byte[400 * 1024])
            };
            req2.Headers.Add("X-Nonce", Convert.ToBase64String(new byte[12]));
            var resp2 = await client1.SendAsync(req2);
            Assert.Equal((HttpStatusCode)507, resp2.StatusCode);

            // Qurilma 2: o'zining 400 KB faylini yuklaydi -> o'tadi (chunki o'z kvotasi to'lmagan)
            var hash3 = NewValidHash();
            using var req3 = new HttpRequestMessage(HttpMethod.Put, $"/api/v1/media/{hash3}")
            {
                Content = new ByteArrayContent(new byte[400 * 1024])
            };
            req3.Headers.Add("X-Nonce", Convert.ToBase64String(new byte[12]));
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
        var hash = NewValidHash();

        using var head = new HttpRequestMessage(HttpMethod.Head, $"/api/v1/media/{hash}");
        var headResp = await client.SendAsync(head);
        Assert.Equal(HttpStatusCode.Unauthorized, headResp.StatusCode);

        var getResp = await client.GetAsync($"/api/v1/media/{hash}");
        Assert.Equal(HttpStatusCode.Unauthorized, getResp.StatusCode);

        using var put = new HttpRequestMessage(HttpMethod.Put, $"/api/v1/media/{hash}")
        {
            Content = new ByteArrayContent([1, 2, 3])
        };
        put.Headers.Add("X-Nonce", Convert.ToBase64String(new byte[12]));
        var putResp = await client.SendAsync(put);
        Assert.Equal(HttpStatusCode.Unauthorized, putResp.StatusCode);
    }

    [Fact]
    public async Task Server_rejects_malformed_hashes_on_head_put_get()
    {
        var (client, _, _) = await EnrolDeviceAsync();
        var validHash = NewValidHash();
        var invalidHashes = new[]
        {
            "..x",
            validHash.ToUpperInvariant(),
            validHash[..63],
            validHash + "a",
            "not-a-hash"
        };

        foreach (var badHash in invalidHashes)
        {
            // HEAD
            using var headReq = new HttpRequestMessage(HttpMethod.Head, $"/api/v1/media/{badHash}");
            var headResp = await client.SendAsync(headReq);
            Assert.Equal(HttpStatusCode.BadRequest, headResp.StatusCode);

            // GET
            var getResp = await client.GetAsync($"/api/v1/media/{badHash}");
            Assert.Equal(HttpStatusCode.BadRequest, getResp.StatusCode);

            // PUT
            using var putReq = new HttpRequestMessage(HttpMethod.Put, $"/api/v1/media/{badHash}")
            {
                Content = new ByteArrayContent([1, 2, 3])
            };
            putReq.Headers.Add("X-Nonce", Convert.ToBase64String(new byte[12]));
            var putResp = await client.SendAsync(putReq);
            Assert.Equal(HttpStatusCode.BadRequest, putResp.StatusCode);
        }

        // Assert no files created in storage root
        if (Directory.Exists(_tempMediaRoot))
        {
            Assert.Empty(Directory.GetFiles(_tempMediaRoot, "*", SearchOption.AllDirectories));
        }

        // Assert MediaService refuses directly
        using var scope = _factory.Services.CreateScope();
        var mediaService = scope.ServiceProvider.GetRequiredService<MediaService>();
        await Assert.ThrowsAsync<ArgumentException>(() => mediaService.ExistsAsync("..x"));
        await Assert.ThrowsAsync<ArgumentException>(() => mediaService.ReadAsync("..x"));
        await Assert.ThrowsAsync<ArgumentException>(() => mediaService.StoreAsync("..x", [1, 2, 3], new byte[12]));
    }

    [Fact]
    public async Task Server_requires_valid_12byte_x_nonce_on_put()
    {
        var (client, _, _) = await EnrolDeviceAsync();
        var hash = NewValidHash();

        // 1. Missing X-Nonce -> 400
        using (var putReq = new HttpRequestMessage(HttpMethod.Put, $"/api/v1/media/{hash}") { Content = new ByteArrayContent([1, 2, 3]) })
        {
            var resp = await client.SendAsync(putReq);
            Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        }

        // 2. Bad base64 -> 400
        using (var putReq = new HttpRequestMessage(HttpMethod.Put, $"/api/v1/media/{hash}") { Content = new ByteArrayContent([1, 2, 3]) })
        {
            putReq.Headers.Add("X-Nonce", "not-valid-base64!!!");
            var resp = await client.SendAsync(putReq);
            Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        }

        // 3. 11 bytes nonce -> 400
        using (var putReq = new HttpRequestMessage(HttpMethod.Put, $"/api/v1/media/{hash}") { Content = new ByteArrayContent([1, 2, 3]) })
        {
            putReq.Headers.Add("X-Nonce", Convert.ToBase64String(new byte[11]));
            var resp = await client.SendAsync(putReq);
            Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        }

        // 4. 13 bytes nonce -> 400
        using (var putReq = new HttpRequestMessage(HttpMethod.Put, $"/api/v1/media/{hash}") { Content = new ByteArrayContent([1, 2, 3]) })
        {
            putReq.Headers.Add("X-Nonce", Convert.ToBase64String(new byte[13]));
            var resp = await client.SendAsync(putReq);
            Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        }

        // 5. Valid 12-byte nonce -> 200, returned on HEAD/GET
        var nonceBytes = System.Security.Cryptography.RandomNumberGenerator.GetBytes(12);
        var validNonce = Convert.ToBase64String(nonceBytes);
        using (var putReq = new HttpRequestMessage(HttpMethod.Put, $"/api/v1/media/{hash}") { Content = new ByteArrayContent([10, 20, 30]) })
        {
            putReq.Headers.Add("X-Nonce", validNonce);
            var resp = await client.SendAsync(putReq);
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        }

        using (var headReq = new HttpRequestMessage(HttpMethod.Head, $"/api/v1/media/{hash}"))
        {
            var headResp = await client.SendAsync(headReq);
            Assert.Equal(HttpStatusCode.OK, headResp.StatusCode);
            Assert.Equal(validNonce, headResp.Headers.GetValues("X-Nonce").Single());
        }

        var getResp = await client.GetAsync($"/api/v1/media/{hash}");
        Assert.Equal(HttpStatusCode.OK, getResp.StatusCode);
        Assert.Equal(validNonce, getResp.Headers.GetValues("X-Nonce").Single());
    }

    [Fact]
    public async Task Push_record_referencing_unknown_media_hash_returns_error_and_batch_succeeds()
    {
        var (client, deviceId, _) = await EnrolDeviceAsync();
        var peer = $"peer_{Guid.NewGuid():N}";
        var unknownHash = NewValidHash();

        var recordWithUnknownMedia = new
        {
            record_id = RecordId.Compute("deleted", "acc01", peer, 1, 1753900010L),
            kind = "deleted",
            account_hash = "acc01",
            peer_hash = peer,
            msg_id = 1L,
            occurred_at = 1753900010L,
            observed_at = 1753900011L,
            device_id = deviceId,
            nonce = Convert.ToBase64String(new byte[12]),
            payload = Convert.ToBase64String(new byte[] { 1, 2 }),
            media = new[]
            {
                new { hash = unknownHash, size = 100L, nonce = Convert.ToBase64String(new byte[12]) }
            }
        };

        var validNormalRecord = new
        {
            record_id = RecordId.Compute("deleted", "acc01", peer, 2, 1753900020L),
            kind = "deleted",
            account_hash = "acc01",
            peer_hash = peer,
            msg_id = 2L,
            occurred_at = 1753900020L,
            observed_at = 1753900021L,
            device_id = deviceId,
            nonce = Convert.ToBase64String(new byte[12]),
            payload = Convert.ToBase64String(new byte[] { 3, 4 }),
            media = Array.Empty<object>()
        };

        var pushResp = await client.PostAsJsonAsync("/api/v1/sync/push", new
        {
            records = new object[] { recordWithUnknownMedia, validNormalRecord }
        });
        Assert.Equal(HttpStatusCode.OK, pushResp.StatusCode);

        var pushJson = await pushResp.Content.ReadFromJsonAsync<JsonElement>();
        var results = pushJson.GetProperty("results").EnumerateArray().ToList();
        Assert.Equal(2, results.Count);

        var res1 = results.First(r => r.GetProperty("record_id").GetString() == recordWithUnknownMedia.record_id);
        Assert.Equal("error", res1.GetProperty("status").GetString());
        Assert.Equal("media_hash_missing", res1.GetProperty("message").GetString());

        var res2 = results.First(r => r.GetProperty("record_id").GetString() == validNormalRecord.record_id);
        Assert.Equal("created", res2.GetProperty("status").GetString());
        var seq = res2.GetProperty("seq").GetInt64();

        // Pull to verify Record A was NOT stored and Record B was stored
        var pullResp = await client.GetAsync($"/api/v1/sync/pull?since={seq - 1}&limit=50");
        var pullBody = await pullResp.Content.ReadFromJsonAsync<JsonElement>();
        var pulledRecords = pullBody.GetProperty("records").EnumerateArray().ToList();

        Assert.Contains(pulledRecords, r => r.GetProperty("record_id").GetString() == validNormalRecord.record_id);
        Assert.DoesNotContain(pulledRecords, r => r.GetProperty("record_id").GetString() == recordWithUnknownMedia.record_id);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SyncDbContext>();
        Assert.False(await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.AnyAsync(
            db.Records, r => r.RecordId == recordWithUnknownMedia.record_id));
    }

    // TeamLead tekshiruvi (2026-10-01): `media_ref_invalid` hech bir testda
    // yo'q edi — format tekshiruvi olib tashlansa ham suite yashil qolardi.
    // Format xatosi `media_hash_missing` dan farqlanishi shart: serverda BOR
    // blob'ga noto'g'ri nonce bilan havola qilgan yozuv saqlanmasligi kerak.
    [Theory]
    [InlineData("uppercase_hash")]
    [InlineData("short_nonce")]
    [InlineData("long_nonce")]
    public async Task Push_record_with_malformed_media_ref_returns_media_ref_invalid(string variant)
    {
        var (client, deviceId, _) = await EnrolDeviceAsync();
        var peer = $"peer_{Guid.NewGuid():N}";
        var hash = NewValidHash();

        // Blob serverda bor — xato faqat havolaning shaklida.
        using (var put = new HttpRequestMessage(HttpMethod.Put, $"/api/v1/media/{hash}") { Content = new ByteArrayContent(new byte[16]) })
        {
            put.Headers.Add("X-Nonce", Convert.ToBase64String(new byte[12]));
            Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(put)).StatusCode);
        }

        object mediaRef = variant switch
        {
            "uppercase_hash" => new { hash = hash.ToUpperInvariant(), size = 16L, nonce = Convert.ToBase64String(new byte[12]) },
            "short_nonce" => new { hash, size = 16L, nonce = Convert.ToBase64String(new byte[11]) },
            _ => new { hash, size = 16L, nonce = Convert.ToBase64String(new byte[13]) },
        };

        var recordId = RecordId.Compute("deleted", "acc01", peer, 1, 1753900030L);
        var pushResp = await client.PostAsJsonAsync("/api/v1/sync/push", new
        {
            records = new object[]
            {
                new
                {
                    record_id = recordId,
                    kind = "deleted",
                    account_hash = "acc01",
                    peer_hash = peer,
                    msg_id = 1L,
                    occurred_at = 1753900030L,
                    observed_at = 1753900031L,
                    device_id = deviceId,
                    nonce = Convert.ToBase64String(new byte[12]),
                    payload = Convert.ToBase64String(new byte[] { 1, 2 }),
                    media = new[] { mediaRef }
                }
            }
        });
        Assert.Equal(HttpStatusCode.OK, pushResp.StatusCode);

        var result = (await pushResp.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("results").EnumerateArray().Single();
        Assert.Equal("error", result.GetProperty("status").GetString());
        Assert.Equal("media_ref_invalid", result.GetProperty("message").GetString());

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SyncDbContext>();
        Assert.False(await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.AnyAsync(
            db.Records, r => r.RecordId == recordId));
    }

    [Fact]
    public async Task Pushed_record_with_media_hashes_survives_round_trip()
    {
        var (client, _, _) = await EnrolDeviceAsync();
        var peer = $"peer_media_{Guid.NewGuid():N}";
        var hash1 = NewValidHash();
        var hash2 = NewValidHash();

        var nonce1 = Convert.ToBase64String(new byte[12]);
        var nonce2 = Convert.ToBase64String(new byte[12]);

        // Upload media blobs first (contract requirement)
        using var put1 = new HttpRequestMessage(HttpMethod.Put, $"/api/v1/media/{hash1}") { Content = new ByteArrayContent(new byte[100]) };
        put1.Headers.Add("X-Nonce", nonce1);
        var put1Resp = await client.SendAsync(put1);
        Assert.Equal(HttpStatusCode.OK, put1Resp.StatusCode);

        using var put2 = new HttpRequestMessage(HttpMethod.Put, $"/api/v1/media/{hash2}") { Content = new ByteArrayContent(new byte[200]) };
        put2.Headers.Add("X-Nonce", nonce2);
        var put2Resp = await client.SendAsync(put2);
        Assert.Equal(HttpStatusCode.OK, put2Resp.StatusCode);

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
                new { hash = hash1, size = 100L, nonce = nonce1 },
                new { hash = hash2, size = 200L, nonce = nonce2 }
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
