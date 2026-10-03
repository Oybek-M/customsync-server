using System.Collections.Concurrent;
using System.Globalization;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using CustomSync.Api.Auth;
using CustomSync.Capture.Capture;
using CustomSync.Capture.Maintenance;
using CustomSync.Capture.Media;
using CustomSync.Capture.Sync;
using CustomSync.Capture.Tdlib;
using CustomSync.Core;
using CustomSync.Core.Contracts;
using CustomSync.Data;
using CustomSync.Services;
using CustomSync.Tests.Fixtures;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace CustomSync.Tests;

public abstract class CaptureContractTestBase : IDisposable
{
    protected static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true
    };

    protected readonly CustomSyncWebApplicationFactory _factory;
    protected readonly List<string> _tempPaths = new();

    protected CaptureContractTestBase(CustomSyncWebApplicationFactory factory)
    {
        _factory = factory;
    }

    protected string TempPath(string suffix)
    {
        var p = Path.Combine(Path.GetTempPath(), $"cs-contract-{Guid.NewGuid():N}{suffix}");
        _tempPaths.Add(p);
        return p;
    }

    protected string TempDir()
    {
        var p = TempPath("-dir");
        Directory.CreateDirectory(p);
        return p;
    }

    public virtual void Dispose()
    {
        SqliteConnection.ClearAllPools();
        foreach (var p in _tempPaths)
        {
            foreach (var f in new[] { p, p + "-wal", p + "-shm" })
            {
                try
                {
                    if (File.Exists(f)) File.Delete(f);
                    else if (Directory.Exists(f)) Directory.Delete(f, true);
                }
                catch { }
            }
        }
    }

    // ---------- Test yordamchilari ----------

    public sealed class DynamicConsolePrompt : IConsolePrompt
    {
        private readonly Func<string, bool, string?> _handler;
        public DynamicConsolePrompt(Func<string, bool, string?> handler) => _handler = handler;
        public string? Prompt(string message, bool isSecret = false) => _handler(message, isSecret);
    }

    public sealed class RequestCountingHandler : DelegatingHandler
    {
        private int _requestCount;
        public int RequestCount => Volatile.Read(ref _requestCount);

        public RequestCountingHandler(HttpMessageHandler innerHandler) : base(innerHandler)
        {
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _requestCount);
            return base.SendAsync(request, cancellationToken);
        }
    }

    public sealed class QueueTransport : ITdTransport
    {
        private readonly BlockingCollection<string> _incoming = new();
        public readonly ConcurrentQueue<string> Sent = new();
        public Func<JsonObject, JsonObject?>? Reply { get; set; }

        public int CreateClientId() => 1;

        public void Send(int clientId, string requestJson)
        {
            Sent.Enqueue(requestJson);
            var req = JsonNode.Parse(requestJson)!.AsObject();
            var resp = Reply?.Invoke(req);
            if (resp is not null)
            {
                resp["@extra"] = req["@extra"]?.GetValue<string>();
                _incoming.Add(resp.ToJsonString());
            }
        }

        public void PushUpdate(string json) => _incoming.Add(json);

        public string? Receive(double timeoutSeconds)
            => _incoming.TryTake(out var item, TimeSpan.FromSeconds(Math.Min(timeoutSeconds, 0.05))) ? item : null;

        public string? Execute(string requestJson) => null;
        public void Dispose() { }
    }

    protected static async Task<bool> WaitAsync(Func<bool> condition, int timeoutMs = 4000)
    {
        for (var waited = 0; waited < timeoutMs; waited += 25)
        {
            if (condition()) return true;
            await Task.Delay(25);
        }
        return condition();
    }

    public sealed record CaptureHarness(
        string Directory,
        IConfiguration Config,
        ServiceProvider Provider,
        QueueTransport Transport,
        MessageCache Cache,
        CaptureSyncRunner Runner,
        CaptureUpdateHandler Handler,
        SyncedScopeSettingsSource SettingsSource,
        RequestCountingHandler CountingHandler,
        string StatePath,
        string KeyPath,
        string DeviceId) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            await Provider.DisposeAsync();
        }
    }

    protected async Task<CaptureHarness> CreateCaptureHarnessAsync(
        string? serverUrl = "http://localhost",
        TimeProvider? timeProvider = null,
        int pushBatchSize = 500,
        bool autoAuthorize = true,
        string accountId = "12345",
        string? preEnrolledDeviceId = null,
        byte[]? masterKey = null,
        Action<Dictionary<string, string?>>? configureSettings = null)
    {
        var dir = TempDir();
        var statePath = Path.Combine(dir, "device-state.json");
        var keyPath = Path.Combine(dir, "master.key");
        var dbPath = Path.Combine(dir, "cache.db");

        var settingsDict = new Dictionary<string, string?>
        {
            ["Telegram:ApiId"] = "12345",
            ["Telegram:ApiHash"] = "test_hash",
            ["Telegram:DatabaseDirectory"] = dir,
            ["Telegram:FilesDirectory"] = dir,
            ["Capture:CacheDatabasePath"] = dbPath,
            ["Capture:Sync:Enabled"] = "true",
            ["Capture:Sync:ServerUrl"] = serverUrl,
            ["Capture:Sync:StatePath"] = statePath,
            ["Capture:Sync:MasterKeyPath"] = keyPath,
            ["Capture:Sync:PushBatchSize"] = pushBatchSize.ToString(CultureInfo.InvariantCulture),
            ["Capture:Scope:DefaultEnabled"] = "true",
            ["Capture:Activity:TrackAllContacts"] = "true",
            ["Capture:Activity:Include:0"] = "222",
            ["Capture:Media:Enabled"] = "false",
        };
        configureSettings?.Invoke(settingsDict);

        var config = new ConfigurationBuilder().AddInMemoryCollection(settingsDict).Build();

        var transport = new QueueTransport
        {
            Reply = req => req["@type"]!.GetValue<string>() switch
            {
                "getMe" => new JsonObject { ["@type"] = "user", ["id"] = long.Parse(accountId), ["first_name"] = "Me" },
                _ => null
            }
        };

        var countingHandler = new RequestCountingHandler(_factory.Server.CreateHandler());

        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(config);
        if (timeProvider != null)
        {
            services.AddSingleton(timeProvider);
        }
        services.AddTdlibClient(config);
        services.AddMessageCache(config);
        services.AddCaptureHandlers();
        services.AddCaptureSyncClient(config);

        services.RemoveAll<ITdTransport>();
        services.AddSingleton<ITdTransport>(transport);
        services.RemoveAll<IDiskSpaceProbe>();
        services.AddSingleton<IDiskSpaceProbe>(FixedDiskSpaceProbe.Ample());
        services.RemoveAll<HttpMessageHandler>();
        services.AddSingleton<HttpMessageHandler>(countingHandler);

        var provider = services.BuildServiceProvider();
        var cache = provider.GetRequiredService<MessageCache>();
        cache.Initialize();

        var handler = provider.GetRequiredService<CaptureUpdateHandler>();
        _ = provider.GetRequiredService<ITdClient>();
        var runner = provider.GetRequiredService<CaptureSyncRunner>();
        var settingsSource = provider.GetRequiredService<SyncedScopeSettingsSource>();

        string deviceId = preEnrolledDeviceId ?? "";
        if (preEnrolledDeviceId == null)
        {
            using var scope = _factory.Services.CreateScope();
            var devices = scope.ServiceProvider.GetRequiredService<DeviceService>();
            var code = await devices.CreateEnrollmentCodeAsync(DeviceService.RoleDevice);
            var devName = $"cap-{Guid.NewGuid():N}";
            var enrollPrompt = new DynamicConsolePrompt((msg, _) => msg.Contains("code") ? code : devName);
            var http = provider.GetRequiredService<HttpClient>();
            var enrollCode = await SyncCliCommands.EnrollAsync(config, enrollPrompt, http);
            Assert.Equal(0, enrollCode);

            var state = DeviceCredentials.LoadDeviceState(statePath);
            Assert.NotNull(state);
            deviceId = state.DeviceId;
        }

        if (masterKey != null)
        {
            DeviceCredentials.SaveMasterKey(keyPath, masterKey);
        }

        if (autoAuthorize)
        {
            transport.PushUpdate("""{ "@type": "updateAuthorizationState", "authorization_state": { "@type": "authorizationStateReady" } }""");
            Assert.True(await WaitAsync(() => handler.AccountId == accountId));
        }

        return new CaptureHarness(
            dir, config, provider, transport, cache, runner, handler, settingsSource, countingHandler, statePath, keyPath, deviceId);
    }

    protected async Task<(HttpClient Client, string DeviceId, string Token)> CreateEnrolledDeviceAsync(string role = "device")
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

    protected async Task<(HttpClient Client, string Token)> CreateAdminClientAsync()
    {
        var (client, _, token) = await CreateEnrolledDeviceAsync(role: DeviceService.RoleAdmin);
        return (client, token);
    }

    protected async Task<long> GetCurrentMaxSeqAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SyncDbContext>();
        return await db.Records.MaxAsync(r => (long?)r.Seq) ?? 0;
    }

    protected static (byte[] MasterKey, string Passphrase, string WrongPassphrase, int Iterations, string Salt, string Nonce, string WrappedKey, string MasterHex, string Fingerprint) LoadVectorCase1()
    {
        var cases = TestVectors.Get("key_wrap").GetProperty("cases");
        var vector = cases.EnumerateArray().First(c => c.GetProperty("iterations").GetInt32() == 1000);
        var passphrase = vector.GetProperty("passphrase").GetString()!;
        var wrongPassphrase = vector.GetProperty("wrong_passphrase").GetString()!;
        var iterations = vector.GetProperty("iterations").GetInt32();
        var salt = vector.GetProperty("salt").GetString()!;
        var nonce = vector.GetProperty("nonce").GetString()!;
        var wrappedKey = vector.GetProperty("wrapped_key").GetString()!;
        var masterHex = vector.GetProperty("master_hex").GetString()!;
        var fingerprint = vector.GetProperty("fingerprint").GetString()!;
        var masterKey = Convert.FromHexString(masterHex);
        return (masterKey, passphrase, wrongPassphrase, iterations, salt, nonce, wrappedKey, masterHex, fingerprint);
    }

    protected static long ComputeCanonicalPeerId(long tdlibChatId)
    {
        // Must independently mirror tdesktop data_peer_id.h PeerId calculation:
        // > 0: user (shift 0) -> id
        // < 0 and > -1_000_000_000_000L: chat (shift 1) -> (-id) | (1L << 48)
        // <= -1_000_000_000_000L: channel (shift 2) -> (-1_000_000_000_000L - id) | (2L << 48)
        if (tdlibChatId > 0)
        {
            return tdlibChatId;
        }

        if (tdlibChatId > -1_000_000_000_000L)
        {
            var bare = -tdlibChatId;
            return bare | (1L << 48);
        }

        {
            var bare = -1_000_000_000_000L - tdlibChatId;
            return bare | (2L << 48);
        }
    }
}
