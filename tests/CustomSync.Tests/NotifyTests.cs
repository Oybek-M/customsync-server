using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using CustomSync.Api.Auth;
using CustomSync.Core;
using CustomSync.Core.Contracts;
using CustomSync.Services;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CustomSync.Tests;

public class NotifyTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    public NotifyTests(WebApplicationFactory<Program> factory) => _factory = factory;

    private async Task<(HttpClient Client, string DeviceId, string Token)> EnrolDeviceAsync(string role = "device")
    {
        using var scope = _factory.Services.CreateScope();
        var devices = scope.ServiceProvider.GetRequiredService<DeviceService>();
        var jwt     = scope.ServiceProvider.GetRequiredService<JwtIssuer>();

        var code     = await devices.CreateEnrollmentCodeAsync(role);
        var enrolled = await devices.RedeemAsync(code, $"test-{Guid.NewGuid():N}", "notify-test");
        Assert.NotNull(enrolled);

        var (token, _) = await jwt.IssueAsync(enrolled.DeviceId, enrolled.Role);
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        return (client, enrolled.DeviceId, token);
    }

    private static object MakeRecord(string peerHash, int msgId = 1)
    {
        var recordId = RecordId.Compute("edited", "acc01", peerHash, msgId, 1753900000L);
        return new
        {
            recordId,
            kind        = "edited",
            accountHash = "acc01",
            peerHash,
            msgId       = (long)msgId,
            occurredAt  = 1753900000L,
            observedAt  = 1753900001L,
            deviceId    = "test-device",
            nonce       = Convert.ToBase64String(new byte[12]),
            payload     = Convert.ToBase64String(new byte[] { 1, 2, 3 })
        };
    }

    private static async Task<string?> ReceiveMessageWithTimeoutAsync(WebSocket ws, TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);
        var buffer = new byte[1024];
        try
        {
            var result = await ws.ReceiveAsync(buffer, cts.Token);
            if (result.MessageType == WebSocketMessageType.Close) return null;
            return Encoding.UTF8.GetString(buffer, 0, result.Count);
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (WebSocketException)
        {
            return null;
        }
    }

    [Fact]
    public async Task Connecting_with_valid_token_succeeds()
    {
        var (_, _, token) = await EnrolDeviceAsync();
        var wsClient = _factory.Server.CreateWebSocketClient();

        using var ws = await wsClient.ConnectAsync(
            new Uri($"ws://localhost/ws/notify?access_token={token}"), CancellationToken.None);

        Assert.Equal(WebSocketState.Open, ws.State);
        await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None);
    }

    [Fact]
    public async Task Connecting_without_token_is_rejected()
    {
        var wsClient = _factory.Server.CreateWebSocketClient();

        await Assert.ThrowsAnyAsync<Exception>(async () =>
        {
            using var ws = await wsClient.ConnectAsync(
                new Uri("ws://localhost/ws/notify"), CancellationToken.None);
        });
    }

    [Fact]
    public async Task Push_notifies_other_connected_device_with_changes_and_seq()
    {
        var (_, _, tokenA) = await EnrolDeviceAsync();
        var (clientB, _, _) = await EnrolDeviceAsync();

        var wsClient = _factory.Server.CreateWebSocketClient();
        using var wsA = await wsClient.ConnectAsync(
            new Uri($"ws://localhost/ws/notify?access_token={tokenA}"), CancellationToken.None);

        var peer = $"peer_ws_{Guid.NewGuid():N}";
        var pushResp = await clientB.PostAsJsonAsync("/api/v1/sync/push", new { records = new[] { MakeRecord(peer) } });
        Assert.Equal(HttpStatusCode.OK, pushResp.StatusCode);

        var rawMessage = await ReceiveMessageWithTimeoutAsync(wsA, TimeSpan.FromSeconds(5));
        Assert.NotNull(rawMessage);

        var doc = JsonDocument.Parse(rawMessage);
        Assert.Equal("changes", doc.RootElement.GetProperty("type").GetString());
        Assert.True(doc.RootElement.GetProperty("seq").GetInt64() > 0);

        await wsA.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None);
    }

    [Fact]
    public async Task Pushing_device_does_not_receive_its_own_notification()
    {
        var (clientA, _, tokenA) = await EnrolDeviceAsync();

        var wsClient = _factory.Server.CreateWebSocketClient();
        using var wsA = await wsClient.ConnectAsync(
            new Uri($"ws://localhost/ws/notify?access_token={tokenA}"), CancellationToken.None);

        var peer = $"peer_ws_self_{Guid.NewGuid():N}";
        var pushResp = await clientA.PostAsJsonAsync("/api/v1/sync/push", new { records = new[] { MakeRecord(peer) } });
        Assert.Equal(HttpStatusCode.OK, pushResp.StatusCode);

        // O'z push'i uchun bildirishnoma kelmasligi kerak (timeout bilan kutamiz)
        var rawMessage = await ReceiveMessageWithTimeoutAsync(wsA, TimeSpan.FromMilliseconds(500));
        Assert.Null(rawMessage);

        await wsA.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None);
    }

    [Fact]
    public async Task Duplicate_push_sends_no_notification()
    {
        var (_, _, tokenA) = await EnrolDeviceAsync();
        var (clientB, _, _) = await EnrolDeviceAsync();

        var wsClient = _factory.Server.CreateWebSocketClient();
        using var wsA = await wsClient.ConnectAsync(
            new Uri($"ws://localhost/ws/notify?access_token={tokenA}"), CancellationToken.None);

        var peer = $"peer_ws_dup_{Guid.NewGuid():N}";
        var record = MakeRecord(peer);

        // 1-push (created -> notification keladi)
        var push1 = await clientB.PostAsJsonAsync("/api/v1/sync/push", new { records = new[] { record } });
        Assert.Equal(HttpStatusCode.OK, push1.StatusCode);
        var msg1 = await ReceiveMessageWithTimeoutAsync(wsA, TimeSpan.FromSeconds(5));
        Assert.NotNull(msg1);

        // 2-push (duplicate -> hech narsa o'zgarmadi, bildirishnoma yuborilmasligi kerak)
        var push2 = await clientB.PostAsJsonAsync("/api/v1/sync/push", new { records = new[] { record } });
        Assert.Equal(HttpStatusCode.OK, push2.StatusCode);
        var msg2 = await ReceiveMessageWithTimeoutAsync(wsA, TimeSpan.FromMilliseconds(500));
        Assert.Null(msg2);

        await wsA.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None);
    }

    [Fact]
    public async Task Plain_get_on_notify_returns_400_bad_request()
    {
        var (client, _, _) = await EnrolDeviceAsync();

        // WebSocket bo'lmagan oddiy GET so'rov -> 400 Bad Request
        var resp = await client.GetAsync("/ws/notify");
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }
}
