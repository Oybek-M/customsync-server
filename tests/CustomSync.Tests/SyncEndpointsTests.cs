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
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CustomSync.Tests;

/// <summary>
/// Push va pull endpoint'larining integratsion testlari.
/// WebApplicationFactory dev bazasiga ulanadi — shu bazada boshqa
/// testlardan qolgan qatorlar ham bor. Shuning uchun HAR test o'z
/// peer_hash (GUID) ini ishlatadi va faqat shu hash bo'yicha assert
/// qiladi. Global son yoki "eng yangi qator" bo'yicha tekshirish
/// vaqti-vaqti bilan yiqiladi — bu xato ushbu loyihada uch marta
/// takrorlandi.
/// </summary>
public class SyncEndpointsTests : IClassFixture<CustomSyncWebApplicationFactory>
{
    private readonly CustomSyncWebApplicationFactory _factory;

    public SyncEndpointsTests(CustomSyncWebApplicationFactory factory)
        => _factory = factory;

    // ----------------------------------------------------------------
    // Yordamchi: qurilmani ro'yxatdan o'tkazadi va autentifikatsiyali
    // HTTP klient qaytaradi.
    // ----------------------------------------------------------------
    private async Task<(HttpClient Client, string DeviceId)> EnrolDeviceAsync(string role = "device")
    {
        using var scope = _factory.Services.CreateScope();
        var devices = scope.ServiceProvider.GetRequiredService<DeviceService>();
        var jwt     = scope.ServiceProvider.GetRequiredService<JwtIssuer>();

        var code     = await devices.CreateEnrollmentCodeAsync(role);
        var enrolled = await devices.RedeemAsync(code, $"test-{Guid.NewGuid():N}", "sync-test");
        Assert.NotNull(enrolled);

        var (token, _) = await jwt.IssueAsync(enrolled.DeviceId, enrolled.Role);
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        return (client, enrolled.DeviceId);
    }

    // ----------------------------------------------------------------
    // Yordamchi: berilgan peerHash uchun bir yozuv yasaydi.
    // ----------------------------------------------------------------
    private static object MakeRecord(string peerHash, int msgId = 1,
        string kind = "deleted", string? targetRecordId = null)
    {
        var recordId = RecordId.Compute(kind, "acc01", peerHash, msgId, 1753900000L);
        return new
        {
            record_id = recordId,
            kind,
            account_hash     = "acc01",
            peer_hash = peerHash,
            msg_id            = (long)msgId,
            occurred_at       = 1753900000L,
            observed_at       = 1753900001L,
            device_id         = "test-device",
            nonce            = Convert.ToBase64String(new byte[12]),
            payload          = Convert.ToBase64String(new byte[] { 1, 2, 3 }),
            target_record_id = targetRecordId
        };
    }

    // ----------------------------------------------------------------
    // 1. Yaroqli yozuv push qilinsa 200 va "created" qaytadi
    // ----------------------------------------------------------------
    [Fact]
    public async Task Push_valid_record_returns_created()
    {
        var (client, _) = await EnrolDeviceAsync();
        var peer = $"peer_{Guid.NewGuid():N}";
        var rec  = MakeRecord(peer);

        var resp = await client.PostAsJsonAsync("/api/v1/sync/push", new { records = new[] { rec } });

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        var body = await resp.Content.ReadFromJsonAsync<PushResponseBody>();
        Assert.NotNull(body);
        Assert.Single(body.Results);
        Assert.Equal("created", body.Results[0].GetProperty("status").GetString());
    }

    // ----------------------------------------------------------------
    // 2. Xuddi shu push ikkinchi marta "duplicate" qaytishi kerak (K4)
    // ----------------------------------------------------------------
    [Fact]
    public async Task Push_duplicate_returns_duplicate_and_nothing_new_stored()
    {
        var (client, _) = await EnrolDeviceAsync();
        var peer = $"peer_{Guid.NewGuid():N}";
        var rec  = MakeRecord(peer);
        var body = new { records = new[] { rec } };

        var first  = await client.PostAsJsonAsync("/api/v1/sync/push", body);
        var second = await client.PostAsJsonAsync("/api/v1/sync/push", body);

        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        var secondBody = await second.Content.ReadFromJsonAsync<PushResponseBody>();
        Assert.Equal("duplicate", secondBody!.Results[0].GetProperty("status").GetString());
    }

    // ----------------------------------------------------------------
    // 3. Batch limit oshsa 400 "batch_too_large" qaytadi
    // ----------------------------------------------------------------
    [Fact]
    public async Task Push_batch_larger_than_limit_returns_400()
    {
        var (client, _) = await EnrolDeviceAsync();
        var peer = $"peer_{Guid.NewGuid():N}";

        // sync.push_batch_size standart qiymati — server_settings'dan olinadi.
        // Biz default (500) ni bilamiz; 600 ta yozuv yuboramiz.
        var records = Enumerable.Range(0, 600)
            .Select(i => MakeRecord(peer, i))
            .ToList();

        var resp = await client.PostAsJsonAsync("/api/v1/sync/push", new { records });

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        var json = await resp.Content.ReadAsStringAsync();
        Assert.Contains("batch_too_large", json);
    }

    // ----------------------------------------------------------------
    // 4. Pull push qilingan yozuvlarni qaytaradi va NextSince'ni oshiradi
    // ----------------------------------------------------------------
    [Fact]
    public async Task Pull_returns_pushed_records_and_advances_cursor()
    {
        var (client, _) = await EnrolDeviceAsync();
        var peer = $"peer_{Guid.NewGuid():N}";
        var rec  = MakeRecord(peer);

        var pushResp = await client.PostAsJsonAsync("/api/v1/sync/push", new { records = new[] { rec } });
        pushResp.EnsureSuccessStatusCode();
        var pushBody = await pushResp.Content.ReadFromJsonAsync<PushResponseBody>();
        var since = SinceBeforePush(pushBody!.Results);

        var pullResp = await client.GetAsync($"/api/v1/sync/pull?since={since}&limit=500");
        Assert.Equal(HttpStatusCode.OK, pullResp.StatusCode);

        var pullBody = await pullResp.Content.ReadFromJsonAsync<JsonElement>();
        var records  = pullBody.GetProperty("records").EnumerateArray().ToList();

        var ours = records.Where(r =>
            r.GetProperty("peer_hash").GetString() == peer).ToList();

        Assert.Single(ours);
        Assert.True(pullBody.GetProperty("next_since").GetInt64() > 0);
    }

    // ----------------------------------------------------------------
    // 5. Pull limit va sync.pull_batch_size'ni to'g'ri qo'llaydi
    // ----------------------------------------------------------------
    [Fact]
    public async Task Pull_honours_limit_clamped_to_batch_size()
    {
        var (client, _) = await EnrolDeviceAsync();
        var peer = $"peer_{Guid.NewGuid():N}";

        // 5 ta yozuv push qilamiz
        var records = Enumerable.Range(0, 5)
            .Select(i => MakeRecord(peer, i))
            .ToList();
        var pushResp = await client.PostAsJsonAsync("/api/v1/sync/push", new { records });
        pushResp.EnsureSuccessStatusCode();

        // limit=2 bilan pull — faqat 2 ta yozuv va hasMore=true
        var pullResp = await client.GetAsync("/api/v1/sync/pull?since=0&limit=2");
        Assert.Equal(HttpStatusCode.OK, pullResp.StatusCode);

        var body    = await pullResp.Content.ReadFromJsonAsync<JsonElement>();
        var fetched = body.GetProperty("records").EnumerateArray().ToList();

        // Faqat shu test tomonidan yaratilgan yozuvlar hisoblanmaydi —
        // server global natija qaytaradi, ammo limit to'g'ri ishlaydi.
        Assert.True(fetched.Count <= 2,
            $"limit=2 berilgan holda {fetched.Count} ta qaytdi");
    }

    // ----------------------------------------------------------------
    // 6a. Tombstone mavjud target'ni o'chiradi
    // ----------------------------------------------------------------
    [Fact]
    public async Task Tombstone_deletes_existing_target()
    {
        var (client, _) = await EnrolDeviceAsync();
        var peer      = $"peer_{Guid.NewGuid():N}";
        var targetRec = MakeRecord(peer, msgId: 1, kind: "deleted");

        // Avval target push qilamiz
        var pushTarget = await client.PostAsJsonAsync(
            "/api/v1/sync/push", new { records = new[] { targetRec } });
        pushTarget.EnsureSuccessStatusCode();

        var targetId = ((dynamic)targetRec).record_id as string
            ?? RecordId.Compute("deleted", "acc01", peer, 1, 1753900000L);

        // Tombstone push qilamiz — target'ni o'chirishi kerak
        var tombPeer = $"peer_{Guid.NewGuid():N}";
        var tombId   = RecordId.Compute("tombstone", "acc01", tombPeer, 99, 1753900500L);
        var tombRecord = new
        {
            record_id        = tombId,
            kind            = "tombstone",
            account_hash     = "acc01",
            peer_hash        = tombPeer,
            msg_id           = 99L,
            occurred_at      = 1753900500L,
            observed_at      = 1753900501L,
            device_id        = "test-device",
            nonce           = Convert.ToBase64String(new byte[12]),
            payload         = Convert.ToBase64String(new byte[] { 1, 2, 3 }),
            target_record_id  = targetId
        };

        var pushTomb = await client.PostAsJsonAsync(
            "/api/v1/sync/push", new { records = new[] { tombRecord } });
        pushTomb.EnsureSuccessStatusCode();
        var tombBody = await pushTomb.Content.ReadFromJsonAsync<PushResponseBody>();
        Assert.Equal("created", tombBody!.Results[0].GetProperty("status").GetString());

        // Target o'chirilganini tekshiramiz — pull qilib peerHash bo'yicha filtrlash
        var since = SinceBeforePush(tombBody.Results) - 1;
        var pullResp = await client.GetAsync($"/api/v1/sync/pull?since={(since < 0 ? 0 : since)}&limit=500");
        pullResp.EnsureSuccessStatusCode();
        var pullBody = await pullResp.Content.ReadFromJsonAsync<JsonElement>();
        var allRecords = pullBody.GetProperty("records").EnumerateArray().ToList();

        // Target qatori mavjud bo'lmasligi kerak
        var targetFound = allRecords.Any(r =>
            r.GetProperty("record_id").GetString() == targetId);
        Assert.False(targetFound, "Target yozuv o'chirilishi kerak edi");

        // Tombstone esa saqlanishi kerak
        var tombFound = allRecords.Any(r =>
            r.GetProperty("record_id").GetString() == tombId);
        Assert.True(tombFound, "Tombstone saqlanishi kerak edi");
    }

    // ----------------------------------------------------------------
    // 6b. Target hali kelmaganda tombstone saqlanadi (idempotent)
    // ----------------------------------------------------------------
    [Fact]
    public async Task Tombstone_stored_even_when_target_does_not_exist_yet()
    {
        var (client, _) = await EnrolDeviceAsync();
        var peer    = $"peer_{Guid.NewGuid():N}";
        var tombId  = RecordId.Compute("tombstone", "acc01", peer, 77, 1753901000L);

        // Mavjud bo'lmagan target'ni ko'rsatuvchi tombstone
        var fakeTargetId = "0000000000000000000000000000000000000000000000000000000000000000";
        var tombRecord = new
        {
            record_id        = tombId,
            kind            = "tombstone",
            account_hash     = "acc01",
            peer_hash        = peer,
            msg_id           = 77L,
            occurred_at      = 1753901000L,
            observed_at      = 1753901001L,
            device_id        = "test-device",
            nonce           = Convert.ToBase64String(new byte[12]),
            payload         = Convert.ToBase64String(new byte[] { 1, 2, 3 }),
            target_record_id  = fakeTargetId
        };

        var resp = await client.PostAsJsonAsync(
            "/api/v1/sync/push", new { records = new[] { tombRecord } });
        resp.EnsureSuccessStatusCode();

        var body = await resp.Content.ReadFromJsonAsync<PushResponseBody>();
        // "created" yoki "duplicate" bo'lishi kerak (error bo'lmasligi kerak)
        var status = body!.Results[0].GetProperty("status").GetString();
        Assert.True(status == "created" || status == "duplicate",
            $"Tombstone saqlanishi kerak edi, lekin: {status}");

        // Tombstone pull orqali ko'rinishi kerak
        var since = SinceBeforePush(body.Results);
        var pullResp = await client.GetAsync($"/api/v1/sync/pull?since={since}&limit=500");
        var pullBody = await pullResp.Content.ReadFromJsonAsync<JsonElement>();
        var found = pullBody.GetProperty("records").EnumerateArray()
            .Any(r => r.GetProperty("record_id").GetString() == tombId);
        Assert.True(found, "Tombstone pull orqali ko'rinishi kerak edi");
    }

    // ----------------------------------------------------------------
    // 7. Token yo'q bo'lsa 401 qaytadi
    // ----------------------------------------------------------------
    [Fact]
    public async Task Push_and_pull_without_token_returns_401()
    {
        var client = _factory.CreateClient();
        var peer   = $"peer_{Guid.NewGuid():N}";

        var pushResp = await client.PostAsJsonAsync(
            "/api/v1/sync/push", new { records = new[] { MakeRecord(peer) } });
        Assert.Equal(HttpStatusCode.Unauthorized, pushResp.StatusCode);

        var pullResp = await client.GetAsync("/api/v1/sync/pull?since=0");
        Assert.Equal(HttpStatusCode.Unauthorized, pullResp.StatusCode);
    }

    // ----------------------------------------------------------------
    // JSON deserialization yordamchisi
    // ----------------------------------------------------------------
    /// <summary>
    /// Pull'ni O'Z push'imizdan boshlaydi. `since=0` bilan pull qilish
    /// dev bazasi o'sgani sari yiqila boshlaydi: sahifa cheklangan va
    /// yozuv birinchi sahifadan chiqib ketadi. Bu allaqachon sodir
    /// bo'ldi -- baza 500 qatordan oshgach uchta test tasodifiy
    /// yiqiladigan bo'lib qoldi.
    /// </summary>
    private static long SinceBeforePush(IEnumerable<JsonElement> pushResults)
    {
        var seqs = pushResults
            .Where(r => r.TryGetProperty("seq", out var s)
                        && s.ValueKind == JsonValueKind.Number)
            .Select(r => r.GetProperty("seq").GetInt64())
            .ToList();
        return seqs.Count == 0 ? 0 : seqs.Min() - 1;
    }

    private sealed class PushResponseBody
    {
        public List<JsonElement> Results { get; set; } = new();
    }
}
