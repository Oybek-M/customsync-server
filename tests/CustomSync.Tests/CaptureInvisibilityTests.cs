using System.Collections.Concurrent;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using CustomSync.Capture;
using CustomSync.Capture.Capture;
using CustomSync.Capture.Sync;
using CustomSync.Capture.Tdlib;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CustomSync.Tests;

public class FakeRecordingTdTransport : ITdTransport
{
    public ConcurrentQueue<string> SentPayloads { get; } = new();
    public ConcurrentQueue<string> ExecuteCalls { get; } = new();
    public ConcurrentQueue<string> IncomingQueue { get; } = new();
    public Func<int, string, string?>? OnSend { get; set; }
    public Func<string, string?>? OnExecute { get; set; }

    public int CreateClientId() => 1;

    public void Send(int clientId, string requestJson)
    {
        SentPayloads.Enqueue(requestJson);
        if (OnSend != null)
        {
            var reply = OnSend(clientId, requestJson);
            if (reply != null)
            {
                IncomingQueue.Enqueue(reply);
            }
        }
    }

    public bool AutoRepeatReadyState { get; set; }

    public string? Receive(double timeoutSeconds)
    {
        if (IncomingQueue.TryDequeue(out var msg))
        {
            if (AutoRepeatReadyState && msg.Contains("updateAuthorizationState"))
            {
                if (!SentPayloads.Any(p => p.Contains("setOption")))
                {
                    IncomingQueue.Enqueue(msg);
                }
            }
            return msg;
        }
        Thread.Sleep(10);
        return null;
    }

    public string? Execute(string requestJson)
    {
        ExecuteCalls.Enqueue(requestJson);
        return OnExecute?.Invoke(requestJson);
    }

    public void Dispose()
    {
    }
}

public class CapturingLogger<T> : ILogger<T>
{
    private readonly List<(LogLevel Level, string Message)> _logs = new();
    private readonly object _lock = new();
    public Action<string>? OnLog { get; set; }

    public IReadOnlyList<(LogLevel Level, string Message)> Logs
    {
        get
        {
            lock (_lock) return _logs.ToList();
        }
    }

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        var msg = formatter(state, exception);
        lock (_lock)
        {
            _logs.Add((logLevel, msg));
            OnLog?.Invoke(msg);
        }
    }
}

public class FakeHostLifetime : IHostApplicationLifetime
{
    private readonly CancellationTokenSource _cts = new();
    public bool StopRequested { get; private set; }
    public CancellationToken ApplicationStarted => CancellationToken.None;
    public CancellationToken ApplicationStopping => _cts.Token;
    public CancellationToken ApplicationStopped => CancellationToken.None;

    public void StopApplication()
    {
        StopRequested = true;
        _cts.Cancel();
    }
}

public class FixedProbe(bool canLoad = true) : INativeLibraryProbe
{
    public bool CanLoad(string? customPath) => canLoad;
}

[Collection(ProcessExitCodeCollection.Name)]
public class CaptureInvisibilityTests : IDisposable
{
    private readonly List<string> _tempDirs = new();

    private string CreateTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"invis_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        _tempDirs.Add(dir);
        return dir;
    }

    public void Dispose()
    {
        foreach (var dir in _tempDirs)
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    private static IConfiguration CreateValidConfig(string tempDir)
    {
        return new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Telegram:ApiId"] = "12345",
                ["Telegram:ApiHash"] = "test_hash",
                ["Telegram:DatabaseDirectory"] = tempDir,
                ["Telegram:FilesDirectory"] = tempDir,
                ["Capture:CacheDatabasePath"] = Path.Combine(tempDir, "cache.db"),
                ["Capture:SessionInvisibilityTimeoutSeconds"] = "5",
                ["Capture:Scope:DefaultEnabled"] = "true"
            })
            .Build();
    }

    [Fact]
    public async Task Test01_Existing_six_request_types_pass_the_gate()
    {
        var tempDir = CreateTempDir();
        var transport = new FakeRecordingTdTransport();

        transport.OnSend = (clientId, reqJson) =>
        {
            using var doc = JsonDocument.Parse(reqJson);
            var root = doc.RootElement;
            var type = root.GetProperty("@type").GetString();
            var extra = root.TryGetProperty("@extra", out var exProp) ? exProp.GetString() : null;

            string replyType = type switch
            {
                "setTdlibParameters" => "ok",
                "setAuthenticationPhoneNumber" => "ok",
                "checkAuthenticationCode" => "ok",
                "checkAuthenticationPassword" => "ok",
                "getMe" => "user",
                "getMessage" => "message",
                _ => "ok"
            };

            var obj = new JsonObject
            {
                ["@type"] = replyType
            };
            if (extra != null) obj["@extra"] = extra;
            if (replyType == "user") obj["id"] = 12345678;
            if (replyType == "message")
            {
                obj["id"] = 42L << 20;
                obj["chat_id"] = 100;
                obj["content"] = new JsonObject
                {
                    ["@type"] = "messageText",
                    ["text"] = new JsonObject { ["text"] = "hello" }
                };
            }
            return obj.ToJsonString();
        };

        using var client = new TdClient(transport);
        var prompt = new FakeConsolePrompt();
        prompt.Inputs.Enqueue("+1234567890");
        prompt.Inputs.Enqueue("12345");
        prompt.Inputs.Enqueue("password123");

        var config = CreateValidConfig(tempDir);
        var auth = new TdAuthenticator(client, config, prompt, isInteractive: true);

        // 1. setTdlibParameters
        var r1 = await auth.ProcessAuthorizationStateAsync(new JsonObject { ["@type"] = "updateAuthorizationState", ["authorization_state"] = new JsonObject { ["@type"] = "authorizationStateWaitTdlibParameters" } }.ToJsonString());
        Assert.NotNull(r1);

        // 2. setAuthenticationPhoneNumber
        var r2 = await auth.ProcessAuthorizationStateAsync(new JsonObject { ["@type"] = "updateAuthorizationState", ["authorization_state"] = new JsonObject { ["@type"] = "authorizationStateWaitPhoneNumber" } }.ToJsonString());
        Assert.NotNull(r2);

        // 3. checkAuthenticationCode
        var r3 = await auth.ProcessAuthorizationStateAsync(new JsonObject { ["@type"] = "updateAuthorizationState", ["authorization_state"] = new JsonObject { ["@type"] = "authorizationStateWaitCode" } }.ToJsonString());
        Assert.NotNull(r3);

        // 4. checkAuthenticationPassword
        var r4 = await auth.ProcessAuthorizationStateAsync(new JsonObject { ["@type"] = "updateAuthorizationState", ["authorization_state"] = new JsonObject { ["@type"] = "authorizationStateWaitPassword" } }.ToJsonString());
        Assert.NotNull(r4);

        // 5 & 6. getMe and getMessage via CaptureUpdateHandler
        var cache = new MessageCache(Path.Combine(tempDir, "cache.db"));
        cache.Initialize();
        var scope = new CaptureScopeEvaluator(config, null);
        var actScope = new ActivityScopeEvaluator(config, null);
        var handler = new CaptureUpdateHandler(cache, scope, actScope);
        handler.Attach(client);

        // Ready state triggers getMe
        transport.IncomingQueue.Enqueue(new JsonObject
        {
            ["@type"] = "updateAuthorizationState",
            ["authorization_state"] = new JsonObject { ["@type"] = "authorizationStateReady" }
        }.ToJsonString());

        // Wait for getMe to process
        for (int i = 0; i < 50; i++)
        {
            if (transport.SentPayloads.Any(p => p.Contains("getMe"))) break;
            await Task.Delay(20);
        }

        // Edit update for uncached message triggers getMessage
        transport.IncomingQueue.Enqueue(new JsonObject
        {
            ["@type"] = "updateMessageContent",
            ["chat_id"] = 100,
            ["message_id"] = 42L << 20,
            ["new_content"] = new JsonObject
            {
                ["@type"] = "messageText",
                ["text"] = new JsonObject { ["text"] = "edited" }
            }
        }.ToJsonString());

        // Wait for getMessage to process
        for (int i = 0; i < 50; i++)
        {
            if (transport.SentPayloads.Any(p => p.Contains("getMessage"))) break;
            await Task.Delay(20);
        }

        var sentTypes = transport.SentPayloads.Select(p =>
        {
            using var d = JsonDocument.Parse(p);
            return d.RootElement.GetProperty("@type").GetString();
        }).ToList();

        Assert.Contains("setTdlibParameters", sentTypes);
        Assert.Contains("setAuthenticationPhoneNumber", sentTypes);
        Assert.Contains("checkAuthenticationCode", sentTypes);
        Assert.Contains("checkAuthenticationPassword", sentTypes);
        Assert.Contains("getMe", sentTypes);
        Assert.Contains("getMessage", sentTypes);
    }

    [Fact]
    public async Task Test02_Forbidden_requests_are_rejected_and_transport_receives_nothing()
    {
        var transport = new FakeRecordingTdTransport();
        using var client = new TdClient(transport);

        var forbidden = new[]
        {
            "viewMessages", "openChat", "closeChat", "openMessageContent",
            "readAllChatMentions", "readAllChatReactions", "readAllMessageThreadMentions",
            "readChatList", "toggleChatIsMarkedAsUnread", "sendChatAction",
            "openStory", "sendMessage", "forwardMessages", "deleteMessages",
            "joinChat", "logOut", "destroy", "terminateAllOtherSessions"
        };

        foreach (var method in forbidden)
        {
            var req = $"{{\"@type\":\"{method}\"}}";
            await Assert.ThrowsAsync<TdRequestNotAllowedException>(() => client.SendAsync(req));
        }

        Assert.Empty(transport.SentPayloads);
    }

    [Fact]
    public void Test03_Allow_list_and_forbidden_list_do_not_intersect()
    {
        var forbidden = new[]
        {
            "viewMessages", "openChat", "closeChat", "openMessageContent",
            "readAllChatMentions", "readAllChatReactions", "readAllMessageThreadMentions",
            "readChatList", "toggleChatIsMarkedAsUnread", "sendChatAction",
            "openStory", "sendMessage", "forwardMessages", "deleteMessages",
            "joinChat", "logOut", "destroy", "terminateAllOtherSessions"
        };

        var intersection = TdRequestPolicy.AllowedRequestTypes.Intersect(forbidden).ToList();
        Assert.Empty(intersection);
    }

    [Fact]
    public void Test15_DownloadFile_allow_list_and_parameter_validation()
    {
        // 1. Valid integer file_id > 0 passes
        var validReq = "{\"@type\":\"downloadFile\",\"file_id\":12345,\"priority\":1,\"synchronous\":true}";
        var normalized = TdRequestPolicy.ValidateAndNormalize(validReq);
        Assert.NotNull(normalized);

        // 2. file_id = 0 rejected
        Assert.Throws<TdRequestNotAllowedException>(() =>
            TdRequestPolicy.ValidateAndNormalize("{\"@type\":\"downloadFile\",\"file_id\":0,\"priority\":1}"));

        // 3. file_id negative rejected
        Assert.Throws<TdRequestNotAllowedException>(() =>
            TdRequestPolicy.ValidateAndNormalize("{\"@type\":\"downloadFile\",\"file_id\":-123,\"priority\":1}"));

        // 4. file_id string rejected
        Assert.Throws<TdRequestNotAllowedException>(() =>
            TdRequestPolicy.ValidateAndNormalize("{\"@type\":\"downloadFile\",\"file_id\":\"12345\",\"priority\":1}"));

        // 5. file_id missing rejected
        Assert.Throws<TdRequestNotAllowedException>(() =>
            TdRequestPolicy.ValidateAndNormalize("{\"@type\":\"downloadFile\",\"priority\":1}"));

        // 6. file_id float rejected
        Assert.Throws<TdRequestNotAllowedException>(() =>
            TdRequestPolicy.ValidateAndNormalize("{\"@type\":\"downloadFile\",\"file_id\":12.34,\"priority\":1}"));
    }

    [Fact]
    public async Task Test04_SetOption_online_false_passes_and_others_rejected()
    {
        var transport = new FakeRecordingTdTransport();
        using var client = new TdClient(transport);

        transport.OnSend = (c, req) =>
        {
            using var d = JsonDocument.Parse(req);
            var extra = d.RootElement.GetProperty("@extra").GetString();
            return new JsonObject { ["@type"] = "ok", ["@extra"] = extra }.ToJsonString();
        };

        // Allowed: online = false
        var allowedReq = "{\"@type\":\"setOption\",\"name\":\"online\",\"value\":{\"@type\":\"optionValueBoolean\",\"value\":false}}";
        var res = await client.SendAsync(allowedReq);
        Assert.Contains("\"ok\"", res);
        Assert.Single(transport.SentPayloads);

        // Rejected: online = true
        await Assert.ThrowsAsync<TdRequestNotAllowedException>(() =>
            client.SendAsync("{\"@type\":\"setOption\",\"name\":\"online\",\"value\":{\"@type\":\"optionValueBoolean\",\"value\":true}}"));

        // Rejected: optionValueEmpty
        await Assert.ThrowsAsync<TdRequestNotAllowedException>(() =>
            client.SendAsync("{\"@type\":\"setOption\",\"name\":\"online\",\"value\":{\"@type\":\"optionValueEmpty\"}}"));

        // Rejected: missing value
        await Assert.ThrowsAsync<TdRequestNotAllowedException>(() =>
            client.SendAsync("{\"@type\":\"setOption\",\"name\":\"online\"}"));

        // Rejected: another option name
        await Assert.ThrowsAsync<TdRequestNotAllowedException>(() =>
            client.SendAsync("{\"@type\":\"setOption\",\"name\":\"foo\",\"value\":{\"@type\":\"optionValueBoolean\",\"value\":false}}"));
    }

    [Fact]
    public async Task Test05_GetOption_online_passes_and_others_rejected()
    {
        var transport = new FakeRecordingTdTransport();
        using var client = new TdClient(transport);

        transport.OnSend = (c, req) =>
        {
            using var d = JsonDocument.Parse(req);
            var extra = d.RootElement.GetProperty("@extra").GetString();
            return new JsonObject { ["@type"] = "optionValueBoolean", ["value"] = false, ["@extra"] = extra }.ToJsonString();
        };

        // Allowed: online
        var allowed = await client.SendAsync("{\"@type\":\"getOption\",\"name\":\"online\"}");
        Assert.Contains("optionValueBoolean", allowed);
        Assert.Single(transport.SentPayloads);

        // Rejected: other name
        await Assert.ThrowsAsync<TdRequestNotAllowedException>(() =>
            client.SendAsync("{\"@type\":\"getOption\",\"name\":\"version\"}"));

        // Rejected: missing name
        await Assert.ThrowsAsync<TdRequestNotAllowedException>(() =>
            client.SendAsync("{\"@type\":\"getOption\"}"));
    }

    [Fact]
    public async Task Test06_Malformed_requests_are_rejected()
    {
        var transport = new FakeRecordingTdTransport();
        using var client = new TdClient(transport);

        // No @type
        await Assert.ThrowsAsync<TdRequestNotAllowedException>(() => client.SendAsync("{}"));

        // Numeric @type
        await Assert.ThrowsAsync<TdRequestNotAllowedException>(() => client.SendAsync("{\"@type\":123}"));

        // JSON array
        await Assert.ThrowsAsync<TdRequestNotAllowedException>(() => client.SendAsync("[{\"@type\":\"getMe\"}]"));

        // Duplicate @type keys
        await Assert.ThrowsAsync<TdRequestNotAllowedException>(() => client.SendAsync("{\"@type\":\"getMe\",\"@type\":\"getMe\"}"));

        // "ViewMessages" with a capital V
        await Assert.ThrowsAsync<TdRequestNotAllowedException>(() => client.SendAsync("{\"@type\":\"ViewMessages\"}"));

        // "GetMe" with a capital G (must be rejected because comparison is case-sensitive)
        await Assert.ThrowsAsync<TdRequestNotAllowedException>(() => client.SendAsync("{\"@type\":\"GetMe\"}"));

        Assert.Empty(transport.SentPayloads);
    }

    [Fact]
    public void Test07_Execute_applies_the_same_gate()
    {
        var transport = new FakeRecordingTdTransport();
        using var client = new TdClient(transport);

        Assert.Throws<TdRequestNotAllowedException>(() => client.Execute("{\"@type\":\"viewMessages\"}"));
        Assert.Empty(transport.ExecuteCalls);
    }

    [Fact]
    public async Task Test08_Rejected_request_leaves_PendingRequestCount_zero_and_does_not_log_payload()
    {
        var transport = new FakeRecordingTdTransport();
        var logger = new CapturingLogger<TdClient>();
        using var client = new TdClient(transport, logger);

        const string secretMarker = "SUPER_SECRET_PAYLOAD_MARKER_98765";
        var forbiddenReq = $"{{\"@type\":\"viewMessages\",\"secret\":\"{secretMarker}\"}}";

        await Assert.ThrowsAsync<TdRequestNotAllowedException>(() => client.SendAsync(forbiddenReq));

        Assert.Equal(0, client.PendingRequestCount);

        var logTexts = logger.Logs.Select(l => l.Message).ToList();
        Assert.Contains(logTexts, m => m.Contains("viewMessages"));
        Assert.DoesNotContain(logTexts, m => m.Contains(secretMarker));
    }

    [Fact]
    public async Task Test09_EnsureAsync_returns_success_only_on_ok_and_false()
    {
        // 1. Success: ok + false
        {
            var transport = new FakeRecordingTdTransport();
            transport.OnSend = (c, req) =>
            {
                using var d = JsonDocument.Parse(req);
                var root = d.RootElement;
                var type = root.GetProperty("@type").GetString();
                var extra = root.GetProperty("@extra").GetString();
                if (type == "setOption")
                    return new JsonObject { ["@type"] = "ok", ["@extra"] = extra }.ToJsonString();
                if (type == "getOption")
                    return new JsonObject { ["@type"] = "optionValueBoolean", ["value"] = false, ["@extra"] = extra }.ToJsonString();
                return null;
            };
            using var client = new TdClient(transport);
            var res = await SessionInvisibility.EnsureAsync(client, TimeSpan.FromSeconds(2));
            Assert.True(res.Success);
            Assert.Null(res.Error);
        }

        // 2. setOption TDLib error
        {
            var transport = new FakeRecordingTdTransport();
            transport.OnSend = (c, req) =>
            {
                using var d = JsonDocument.Parse(req);
                var extra = d.RootElement.GetProperty("@extra").GetString();
                return new JsonObject { ["@type"] = "error", ["code"] = 400, ["message"] = "bad option", ["@extra"] = extra }.ToJsonString();
            };
            using var client = new TdClient(transport);
            var res = await SessionInvisibility.EnsureAsync(client, TimeSpan.FromSeconds(2));
            Assert.False(res.Success);
            Assert.Contains("TDLib error", res.Error);
        }

        // 3. getOption returns optionValueBoolean true
        {
            var transport = new FakeRecordingTdTransport();
            transport.OnSend = (c, req) =>
            {
                using var d = JsonDocument.Parse(req);
                var root = d.RootElement;
                var type = root.GetProperty("@type").GetString();
                var extra = root.GetProperty("@extra").GetString();
                if (type == "setOption")
                    return new JsonObject { ["@type"] = "ok", ["@extra"] = extra }.ToJsonString();
                return new JsonObject { ["@type"] = "optionValueBoolean", ["value"] = true, ["@extra"] = extra }.ToJsonString();
            };
            using var client = new TdClient(transport);
            var res = await SessionInvisibility.EnsureAsync(client, TimeSpan.FromSeconds(2));
            Assert.False(res.Success);
        }

        // 4. getOption returns optionValueEmpty
        {
            var transport = new FakeRecordingTdTransport();
            transport.OnSend = (c, req) =>
            {
                using var d = JsonDocument.Parse(req);
                var root = d.RootElement;
                var type = root.GetProperty("@type").GetString();
                var extra = root.GetProperty("@extra").GetString();
                if (type == "setOption")
                    return new JsonObject { ["@type"] = "ok", ["@extra"] = extra }.ToJsonString();
                return new JsonObject { ["@type"] = "optionValueEmpty", ["@extra"] = extra }.ToJsonString();
            };
            using var client = new TdClient(transport);
            var res = await SessionInvisibility.EnsureAsync(client, TimeSpan.FromSeconds(2));
            Assert.False(res.Success);
        }

        // 5. Timeout
        {
            var transport = new FakeRecordingTdTransport(); // never replies
            using var client = new TdClient(transport);
            var res = await SessionInvisibility.EnsureAsync(client, TimeSpan.FromMilliseconds(50));
            Assert.False(res.Success);
            Assert.Contains("Timed out", res.Error);
        }
    }

    [Fact]
    public async Task Test10_Production_path_rejects_forbidden_requests()
    {
        var tempDir = CreateTempDir();
        var config = CreateValidConfig(tempDir);
        var fakeTransport = new FakeRecordingTdTransport();

        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(config);
        services.AddTdlibClient(config);
        services.AddMessageCache(config);
        services.AddCaptureHandlers();
        services.AddCaptureSyncClient(config);

        // Replace only ITdTransport with fake
        services.RemoveAll<ITdTransport>();
        services.AddSingleton<ITdTransport>(fakeTransport);

        using var provider = services.BuildServiceProvider();
        provider.GetRequiredService<MessageCache>().Initialize();
        var client = provider.GetRequiredService<ITdClient>();

        await Assert.ThrowsAsync<TdRequestNotAllowedException>(() =>
            client.SendAsync("{\"@type\":\"viewMessages\",\"chat_id\":1,\"message_ids\":[1]}"));

        Assert.Empty(fakeTransport.SentPayloads);
    }

    [Fact]
    public void Test11_Architecture_only_TdClient_takes_ITdTransport_in_constructor()
    {
        var captureAssembly = typeof(TdClient).Assembly;
        var violatingTypes = captureAssembly.GetTypes()
            .Where(t => t != typeof(TdClient))
            .Where(t => t.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                .Any(c => c.GetParameters().Any(p => p.ParameterType == typeof(ITdTransport))))
            .ToList();

        Assert.Empty(violatingTypes);
    }

    [Fact]
    public async Task Test12_Worker_success_executes_invisibility_before_authorized_and_running_log()
    {
        var tempDir = CreateTempDir();
        var config = CreateValidConfig(tempDir);
        var transport = new FakeRecordingTdTransport { AutoRepeatReadyState = true };
        var timeline = new List<string>();
        var syncRoot = new object();

        transport.OnSend = (c, req) =>
        {
            using var d = JsonDocument.Parse(req);
            var root = d.RootElement;
            var type = root.GetProperty("@type").GetString();
            var extra = root.GetProperty("@extra").GetString();

            lock (syncRoot)
            {
                timeline.Add($"transport:{type}");
            }

            if (type == "setTdlibParameters")
                return new JsonObject { ["@type"] = "ok", ["@extra"] = extra }.ToJsonString();
            if (type == "setOption")
                return new JsonObject { ["@type"] = "ok", ["@extra"] = extra }.ToJsonString();
            if (type == "getOption")
                return new JsonObject { ["@type"] = "optionValueBoolean", ["value"] = false, ["@extra"] = extra }.ToJsonString();
            return null;
        };

        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(config);
        services.AddSingleton<INativeLibraryProbe>(new FixedProbe(true));
        services.AddSingleton<ITdTransport>(transport);
        services.AddSingleton<ITdClient, TdClient>();
        services.AddSingleton<IConsolePrompt, FakeConsolePrompt>();
        services.AddSingleton(sp => new TdAuthenticator(
            sp.GetRequiredService<ITdClient>(),
            config,
            sp.GetRequiredService<IConsolePrompt>(),
            false,
            null));

        services.AddMessageCache(config);
        services.AddCaptureHandlers();
        services.AddCaptureSyncClient(config);

        var logger = new CapturingLogger<Worker>();
        logger.OnLog = msg =>
        {
            if (msg.Contains("Capture service authorized and running"))
            {
                lock (syncRoot)
                {
                    timeline.Add("log:running");
                }
            }
        };
        var lifetime = new FakeHostLifetime();

        using var provider = services.BuildServiceProvider();
        var worker = new Worker(config, provider, lifetime, logger);

        // Pre-queue ready state so AuthorizationGate finishes
        transport.IncomingQueue.Enqueue(new JsonObject
        {
            ["@type"] = "updateAuthorizationState",
            ["authorization_state"] = new JsonObject { ["@type"] = "authorizationStateReady" }
        }.ToJsonString());

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        await worker.StartAsync(cts.Token);

        // Give worker time to complete gate and invisibility check
        for (int i = 0; i < 50; i++)
        {
            if (logger.Logs.Any(l => l.Message.Contains("Capture service authorized and running")))
                break;
            await Task.Delay(50);
        }

        await worker.StopAsync(CancellationToken.None);

        Assert.False(lifetime.StopRequested);

        var logItems = logger.Logs.Select(l => l.Message).ToList();
        Assert.Contains(logItems, m => m.Contains("Capture service authorized and running"));

        var sentList = transport.SentPayloads.Select(p =>
        {
            using var d = JsonDocument.Parse(p);
            return d.RootElement.GetProperty("@type").GetString();
        }).ToList();

        var setOptionIndex = sentList.IndexOf("setOption");
        var getOptionIndex = sentList.IndexOf("getOption");
        Assert.True(setOptionIndex >= 0, "setOption was never sent");
        Assert.True(getOptionIndex >= 0, "getOption was never sent");
        Assert.True(setOptionIndex < getOptionIndex, "setOption must precede getOption");

        lock (syncRoot)
        {
            var getOptionTimelineIdx = timeline.IndexOf("transport:getOption");
            var logRunningTimelineIdx = timeline.IndexOf("log:running");
            Assert.True(getOptionTimelineIdx >= 0, "getOption was not found in timeline");
            Assert.True(logRunningTimelineIdx >= 0, "log:running was not found in timeline");
            Assert.True(getOptionTimelineIdx < logRunningTimelineIdx, "Invisibility check must complete before running is logged");
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Test13_Worker_failure_sets_ExitCode_stops_host_and_never_logs_running(bool tdlibError)
    {
        int originalExitCode = Environment.ExitCode;
        // Avvalgi test qoldirgan `1` bu tekshiruvni bo'sh qilib qo'ymasin.
        Environment.ExitCode = 0;
        try
        {
            var tempDir = CreateTempDir();
            var config = CreateValidConfig(tempDir);
            var transport = new FakeRecordingTdTransport { AutoRepeatReadyState = true };

            transport.OnSend = (c, req) =>
            {
                using var d = JsonDocument.Parse(req);
                var root = d.RootElement;
                var type = root.GetProperty("@type").GetString();
                var extra = root.GetProperty("@extra").GetString();

                if (type == "setTdlibParameters")
                    return new JsonObject { ["@type"] = "ok", ["@extra"] = extra }.ToJsonString();
                if (type == "setOption")
                    return new JsonObject { ["@type"] = "ok", ["@extra"] = extra }.ToJsonString();
                if (type == "getOption")
                    return tdlibError
                        ? new JsonObject { ["@type"] = "error", ["code"] = 400, ["message"] = "OPTION_FAILED", ["@extra"] = extra }.ToJsonString()
                        : new JsonObject { ["@type"] = "optionValueBoolean", ["value"] = true, ["@extra"] = extra }.ToJsonString(); // fails invisibility!
                return null;
            };

            var services = new ServiceCollection();
            services.AddSingleton<IConfiguration>(config);
            services.AddSingleton<INativeLibraryProbe>(new FixedProbe(true));
            services.AddSingleton<ITdTransport>(transport);
            services.AddSingleton<ITdClient, TdClient>();
            services.AddSingleton<IConsolePrompt, FakeConsolePrompt>();
            services.AddSingleton(sp => new TdAuthenticator(
                sp.GetRequiredService<ITdClient>(),
                config,
                sp.GetRequiredService<IConsolePrompt>(),
                false,
                null));

            services.AddMessageCache(config);
            services.AddCaptureHandlers();
            services.AddCaptureSyncClient(config);

            var logger = new CapturingLogger<Worker>();
            var lifetime = new FakeHostLifetime();

            using var provider = services.BuildServiceProvider();
            var worker = new Worker(config, provider, lifetime, logger);

            transport.IncomingQueue.Enqueue(new JsonObject
            {
                ["@type"] = "updateAuthorizationState",
                ["authorization_state"] = new JsonObject { ["@type"] = "authorizationStateReady" }
            }.ToJsonString());

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            await worker.StartAsync(cts.Token);

            for (int i = 0; i < 50; i++)
            {
                if (lifetime.StopRequested) break;
                await Task.Delay(50);
            }

            Assert.True(lifetime.StopRequested);
            Assert.Equal(1, Environment.ExitCode);

            var logItems = logger.Logs.Select(l => l.Message).ToList();
            Assert.DoesNotContain(logItems, m => m.Contains("Capture service authorized and running"));
        }
        finally
        {
            Environment.ExitCode = originalExitCode;
        }
    }

    [Fact]
    public void Test14_Real_probe_registration_resolves_SystemNativeLibraryProbe()
    {
        var services = new ServiceCollection();
        var config = new ConfigurationBuilder().Build();
        services.AddTdlibClient(config);

        using var provider = services.BuildServiceProvider();
        var probe = provider.GetRequiredService<INativeLibraryProbe>();
        Assert.IsType<SystemNativeLibraryProbe>(probe);
    }
}
