using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using CustomSync.Capture.Tdlib;
using Xunit;

namespace CustomSync.Tests;

public class FakeTdTransport : ITdTransport
{
    private readonly BlockingCollection<string> _incomingMessages = new();
    public readonly ConcurrentQueue<(int ClientId, string RequestJson)> OutgoingRequests = new();
    public Func<string, string?>? OnExecute { get; set; }
    public Action<int, string>? OnSend { get; set; }
    public bool IsDisposed { get; private set; }

    public int CreateClientId() => 1;

    public void Send(int clientId, string requestJson)
    {
        OutgoingRequests.Enqueue((clientId, requestJson));
        OnSend?.Invoke(clientId, requestJson);
    }

    public string? Receive(double timeoutSeconds)
    {
        if (IsDisposed) return null;
        try
        {
            var timeout = TimeSpan.FromSeconds(Math.Max(0.01, timeoutSeconds));
            if (_incomingMessages.TryTake(out var msg, timeout))
            {
                return msg;
            }
        }
        catch (Exception) { }
        return null;
    }

    public void EnqueueIncoming(string json)
    {
        if (!IsDisposed)
        {
            _incomingMessages.Add(json);
        }
    }

    public string? Execute(string requestJson) => OnExecute?.Invoke(requestJson);

    public void Dispose()
    {
        IsDisposed = true;
        try
        {
            _incomingMessages.CompleteAdding();
            _incomingMessages.Dispose();
        }
        catch { }
    }
}

public class CaptureClientTests
{
    [Fact]
    public async Task Test1_Two_concurrent_requests_get_own_reply_matched_by_extra()
    {
        var transport = new FakeTdTransport();
        transport.OnSend = (clientId, reqJson) =>
        {
            var node = JsonNode.Parse(reqJson)!;
            var extra = node["@extra"]!.ToString();
            var method = node["@type"]!.ToString();
            var reply = new JsonObject
            {
                ["@type"] = "testResult",
                ["@extra"] = extra,
                ["method"] = method
            }.ToJsonString();
            transport.EnqueueIncoming(reply);
        };

        using var client = new TdClient(transport);

        var taskA = client.SendAsync("{\"@type\":\"methodA\"}");
        var taskB = client.SendAsync("{\"@type\":\"methodB\"}");

        var resA = await taskA;
        var resB = await taskB;

        var nodeA = JsonNode.Parse(resA)!;
        var nodeB = JsonNode.Parse(resB)!;

        Assert.Equal("methodA", nodeA["method"]?.ToString());
        Assert.Equal("methodB", nodeB["method"]?.ToString());
    }

    [Fact]
    public async Task Test2_Payload_with_no_extra_delivered_as_update()
    {
        var transport = new FakeTdTransport();
        using var client = new TdClient(transport);

        var updateTcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        client.UpdateReceived += update => updateTcs.TrySetResult(update);

        var updateJson = "{\"@type\":\"updateOption\",\"name\":\"version\",\"value\":{\"@type\":\"optionValueString\",\"value\":\"1.8.0\"}}";
        transport.EnqueueIncoming(updateJson);

        var received = await Task.WhenAny(updateTcs.Task, Task.Delay(2000));
        Assert.Same(updateTcs.Task, received);
        Assert.Equal(updateJson, await updateTcs.Task);
    }

    [Fact]
    public async Task Test3_Error_reply_fails_request_with_code_and_message()
    {
        var transport = new FakeTdTransport();
        transport.OnSend = (clientId, reqJson) =>
        {
            var node = JsonNode.Parse(reqJson)!;
            var extra = node["@extra"]!.ToString();
            var err = new JsonObject
            {
                ["@type"] = "error",
                ["code"] = 404,
                ["message"] = "Not Found",
                ["@extra"] = extra
            }.ToJsonString();
            transport.EnqueueIncoming(err);
        };

        using var client = new TdClient(transport);

        var ex = await Assert.ThrowsAsync<TdException>(async () =>
        {
            await client.SendAsync("{\"@type\":\"getNonExistent\"}");
        });

        Assert.Equal(404, ex.Code);
        Assert.Equal("Not Found", ex.TdMessage);
    }

    [Fact]
    public async Task Test4_Request_with_no_reply_times_out_and_pending_entry_is_removed()
    {
        var transport = new FakeTdTransport();
        // Do not reply to request
        using var client = new TdClient(transport, defaultTimeout: TimeSpan.FromMilliseconds(50));

        await Assert.ThrowsAsync<TimeoutException>(async () =>
        {
            await client.SendAsync("{\"@type\":\"hangingRequest\"}");
        });

        Assert.Equal(0, client.PendingRequestCount);
    }

    [Fact]
    public async Task Test5_Dispose_fails_pending_requests_and_stops_loop_double_dispose_is_noop()
    {
        var transport = new FakeTdTransport();
        var client = new TdClient(transport, defaultTimeout: TimeSpan.FromSeconds(60));

        var reqTask = client.SendAsync("{\"@type\":\"pendingBeforeDispose\"}");
        Assert.Equal(1, client.PendingRequestCount);

        client.Dispose();

        await Assert.ThrowsAsync<ObjectDisposedException>(async () =>
        {
            await reqTask;
        });

        Assert.Equal(0, client.PendingRequestCount);

        // Double dispose must not throw
        client.Dispose();
    }
}
