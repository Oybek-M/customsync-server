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

public class StatsTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    public StatsTests(WebApplicationFactory<Program> factory) => _factory = factory;

    private async Task<(HttpClient Client, string DeviceId, string Token)> EnrolDeviceAsync(string role = "admin")
    {
        using var scope = _factory.Services.CreateScope();
        var devices = scope.ServiceProvider.GetRequiredService<DeviceService>();
        var jwt     = scope.ServiceProvider.GetRequiredService<JwtIssuer>();

        var code     = await devices.CreateEnrollmentCodeAsync(role);
        var enrolled = await devices.RedeemAsync(code, $"test-{Guid.NewGuid():N}", "stats-test");
        Assert.NotNull(enrolled);

        var (token, _) = await jwt.IssueAsync(enrolled.DeviceId, enrolled.Role);
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        return (client, enrolled.DeviceId, token);
    }

    private static SyncRecord MakeRecord(string peerHash, int msgId, long occurredAt, byte[] payload) => new()
    {
        RecordId    = RecordId.Compute(RecordKind.Edited, "acc01", peerHash, msgId, occurredAt),
        Kind        = RecordKind.Edited,
        AccountHash = "acc01",
        PeerHash    = peerHash,
        MsgId       = msgId,
        OccurredAt  = occurredAt,
        ObservedAt  = occurredAt,
        DeviceId    = "test-device",
        Nonce       = new byte[12],
        Payload     = payload
    };

    [Fact]
    public async Task Records_endpoint_allows_admin_and_forbids_device_role()
    {
        var (adminClient, _, _) = await EnrolDeviceAsync("admin");
        var (deviceClient, _, _) = await EnrolDeviceAsync("device");

        // Admin -> 200 OK
        var adminResp = await adminClient.GetAsync("/api/v1/records/snapshot");
        Assert.Equal(HttpStatusCode.OK, adminResp.StatusCode);

        // Device role -> 403 Forbidden
        var devSnapshotResp = await deviceClient.GetAsync("/api/v1/records/snapshot");
        Assert.Equal(HttpStatusCode.Forbidden, devSnapshotResp.StatusCode);

        var devRecordsResp = await deviceClient.GetAsync("/api/v1/records?snapshot=0");
        Assert.Equal(HttpStatusCode.Forbidden, devRecordsResp.StatusCode);

        // No token -> 401 Unauthorized
        var anonClient = _factory.CreateClient();
        var anonResp = await anonClient.GetAsync("/api/v1/records/snapshot");
        Assert.Equal(HttpStatusCode.Unauthorized, anonResp.StatusCode);
    }

    [Fact]
    public async Task Stats_endpoints_allow_admin_and_forbid_device_role()
    {
        var (adminClient, _, _) = await EnrolDeviceAsync("admin");
        var (deviceClient, _, _) = await EnrolDeviceAsync("device");

        // Admin -> 200 OK
        var adminPeersResp = await adminClient.GetAsync("/api/v1/stats/peers");
        Assert.Equal(HttpStatusCode.OK, adminPeersResp.StatusCode);

        var adminStorageResp = await adminClient.GetAsync("/api/v1/stats/storage");
        Assert.Equal(HttpStatusCode.OK, adminStorageResp.StatusCode);

        // Device role -> 403 Forbidden
        var devPeersResp = await deviceClient.GetAsync("/api/v1/stats/peers");
        Assert.Equal(HttpStatusCode.Forbidden, devPeersResp.StatusCode);

        var devStorageResp = await deviceClient.GetAsync("/api/v1/stats/storage");
        Assert.Equal(HttpStatusCode.Forbidden, devStorageResp.StatusCode);

        // No token -> 401 Unauthorized
        var anonClient = _factory.CreateClient();
        var anonPeersResp = await anonClient.GetAsync("/api/v1/stats/peers");
        Assert.Equal(HttpStatusCode.Unauthorized, anonPeersResp.StatusCode);
    }

    [Fact]
    public async Task Records_and_stats_page_limits_are_clamped_to_max_page_size()
    {
        var (adminClient, _, _) = await EnrolDeviceAsync("admin");

        using var scope = _factory.Services.CreateScope();
        var settings = scope.ServiceProvider.GetRequiredService<SettingsService>();
        var sync     = scope.ServiceProvider.GetRequiredService<SyncService>();

        var peerHash = $"peer_clamp_{Guid.NewGuid():N}";
        await sync.PushAsync("test-device",
            Enumerable.Range(0, 15).Select(i => MakeRecord(peerHash, i, 1_700_000_000 + i, [1, 2])).ToList());

        // max_page_size ni vaqtincha 5 ga o'rnatamiz
        await settings.SetAsync("api.max_page_size", "5");
        try
        {
            // /records?limit=100 so'ralganda 5 ga qisilishi kerak
            var snapResp = await adminClient.GetAsync("/api/v1/records/snapshot");
            var snapJson = await snapResp.Content.ReadFromJsonAsync<JsonElement>();
            var snapshot = snapJson.GetProperty("snapshot").GetInt64();

            var recordsResp = await adminClient.GetAsync($"/api/v1/records?snapshot={snapshot}&peerHash={peerHash}&limit=100");
            Assert.Equal(HttpStatusCode.OK, recordsResp.StatusCode);
            var recordsJson = await recordsResp.Content.ReadFromJsonAsync<JsonElement>();
            var rows = recordsJson.GetProperty("records").EnumerateArray().ToList();
            Assert.Equal(5, rows.Count);

            // /stats/peers?limit=100 so'ralganda ham 5 ga qisilishi kerak
            var peersResp = await adminClient.GetAsync("/api/v1/stats/peers?limit=100");
            Assert.Equal(HttpStatusCode.OK, peersResp.StatusCode);
            var peersList = await peersResp.Content.ReadFromJsonAsync<List<JsonElement>>();
            Assert.NotNull(peersList);
            Assert.True(peersList.Count <= 5);
        }
        finally
        {
            await settings.SetAsync("api.max_page_size", "200");
        }
    }

    [Fact]
    public async Task Stats_peers_sorts_by_bytes_count_and_recent()
    {
        using var scope = _factory.Services.CreateScope();
        var sync  = scope.ServiceProvider.GetRequiredService<SyncService>();
        var stats = scope.ServiceProvider.GetRequiredService<StatsService>();

        var peerA = $"peer_a_{Guid.NewGuid():N}"; // 1 ta yozuv, 10000 bayt, occurredAt = 2_000_000_100
        var peerB = $"peer_b_{Guid.NewGuid():N}"; // 5 ta yozuv, 100 baytdan = 500 bayt, occurredAt = 2_000_000_200
        var peerC = $"peer_c_{Guid.NewGuid():N}"; // 2 ta yozuv, 1000 baytdan = 2000 bayt, occurredAt = 2_000_000_300

        await sync.PushAsync("test-device", [
            MakeRecord(peerA, 1, 2_000_000_100L, new byte[10000]),
            MakeRecord(peerB, 1, 2_000_000_150L, new byte[100]),
            MakeRecord(peerB, 2, 2_000_000_160L, new byte[100]),
            MakeRecord(peerB, 3, 2_000_000_170L, new byte[100]),
            MakeRecord(peerB, 4, 2_000_000_180L, new byte[100]),
            MakeRecord(peerB, 5, 2_000_000_200L, new byte[100]),
            MakeRecord(peerC, 1, 2_000_000_250L, new byte[1000]),
            MakeRecord(peerC, 2, 2_000_000_300L, new byte[1000])
        ]);

        var ourPeers = new HashSet<string> { peerA, peerB, peerC };

        // 1. Sort by bytes: peerA (10000) > peerC (2000) > peerB (500)
        var byBytes = (await stats.PeersAsync("bytes", 500))
            .Where(p => ourPeers.Contains(p.PeerHash))
            .Select(p => p.PeerHash)
            .ToList();
        Assert.Equal(new[] { peerA, peerC, peerB }, byBytes);

        // 2. Sort by count: peerB (5) > peerC (2) > peerA (1)
        var byCount = (await stats.PeersAsync("count", 500))
            .Where(p => ourPeers.Contains(p.PeerHash))
            .Select(p => p.PeerHash)
            .ToList();
        Assert.Equal(new[] { peerB, peerC, peerA }, byCount);

        // 3. Sort by recent: peerC (300) > peerB (200) > peerA (100)
        var byRecent = (await stats.PeersAsync("recent", 500))
            .Where(p => ourPeers.Contains(p.PeerHash))
            .Select(p => p.PeerHash)
            .ToList();
        Assert.Equal(new[] { peerC, peerB, peerA }, byRecent);
    }
}

