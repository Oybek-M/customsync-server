using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using CustomSync.Capture.Capture;
using CustomSync.Capture.Maintenance;
using CustomSync.Capture.Media;
using CustomSync.Capture.Sync;
using CustomSync.Capture.Tdlib;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace CustomSync.Tests;

public class CaptureHealthReportingTests : IDisposable
{
    private readonly List<string> _tempPaths = new();

    private string TempPath(string suffix)
    {
        var p = Path.Combine(Path.GetTempPath(), $"cs-t9b-{Guid.NewGuid():N}{suffix}");
        _tempPaths.Add(p);
        return p;
    }

    private string TempDir()
    {
        var p = TempPath("-dir");
        Directory.CreateDirectory(p);
        return p;
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        foreach (var p in _tempPaths)
        {
            foreach (var f in new[] { p, p + "-wal", p + "-shm" })
            {
                try { if (File.Exists(f)) File.Delete(f); else if (Directory.Exists(f)) Directory.Delete(f, true); } catch { }
            }
        }
    }

    private sealed class LevelLogger<T> : ILogger<T>
    {
        private readonly List<(LogLevel Level, string Text)> _entries = new();
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            lock (_entries) _entries.Add((logLevel, formatter(state, exception) + (exception is null ? "" : " | " + exception)));
        }
        public List<(LogLevel Level, string Text)> Entries { get { lock (_entries) return _entries.ToList(); } }
        public string AllText => string.Join("\n", Entries.Select(e => e.Text));
    }

    private sealed class MockHttpMessageHandler : HttpMessageHandler
    {
        public readonly List<(HttpRequestMessage Request, string? Body)> Requests = new();
        public Func<HttpRequestMessage, HttpResponseMessage>? ResponseFunc { get; set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string? body = null;
            if (request.Content != null)
            {
                body = await request.Content.ReadAsStringAsync(cancellationToken);
            }
            Requests.Add((request, body));
            if (ResponseFunc != null)
            {
                return ResponseFunc(request);
            }
            return new HttpResponseMessage(HttpStatusCode.NoContent);
        }
    }

    private sealed class QueueTransport : ITdTransport
    {
        private readonly System.Collections.Concurrent.BlockingCollection<string> _incoming = new();
        public readonly System.Collections.Concurrent.ConcurrentQueue<string> Sent = new();
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

    private static JsonObject? StorageReplies(JsonObject req) => req["@type"]!.GetValue<string>() switch
    {
        "optimizeStorage" => new JsonObject { ["@type"] = "storageStatistics", ["size"] = 0, ["count"] = 0, ["by_chat"] = new JsonArray() },
        "getStorageStatisticsFast" => new JsonObject
        {
            ["@type"] = "storageStatisticsFast", ["files_size"] = 0, ["file_count"] = 0, ["database_size"] = 0,
            ["language_pack_database_size"] = 0, ["log_size"] = 0
        },
        _ => null
    };

    private static StorageSnapshot CreateSampleSnapshot(long? memLimit = 1_000_000_000L, long? freeDisk = 50_000_000_000L)
    {
        return new StorageSnapshot(
            TakenAt: DateTimeOffset.UtcNow,
            ProcessRssBytes: 123_456_789L,
            MemoryLimitBytes: memLimit,
            CacheDatabaseBytes: 2_345_678L,
            MediaStoreBytes: 45_678_901L,
            MediaStoreFiles: 89,
            TdlibFilesBytes: 12_345_678L,
            TdlibDatabaseBytes: 3_456_789L,
            FreeDiskBytes: freeDisk,
            OptimizeFreedBytes: 10_000L,
            OptimizeDeletedFiles: 2,
            MediaRowsPruned: 5,
            MediaFilesDeleted: 3
        );
    }

    private (IConfiguration Config, string StatePath, string KeyPath, string DbPath) CreateSyncTestEnv(bool enabled = true)
    {
        var keyPath = TempPath(".key");
        File.WriteAllText(keyPath, new string('a', 64));

        var statePath = TempPath(".state.json");
        var stateJson = JsonSerializer.Serialize(new { device_id = "test-device-id", refresh_token = "refresh-token-xyz" });
        File.WriteAllText(statePath, stateJson);

        var dbPath = TempPath(".db");
        new MessageCache(dbPath).Initialize();

        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Capture:Sync:Enabled"] = enabled ? "true" : "false",
            ["Capture:Sync:ServerUrl"] = "http://localhost:5000",
            ["Capture:Sync:MasterKeyPath"] = keyPath,
            ["Capture:Sync:StatePath"] = statePath,
            ["Capture:CacheDatabasePath"] = dbPath
        }).Build();

        return (config, statePath, keyPath, dbPath);
    }

    // 10. PostHealthAsync sends POST /api/v1/devices/health with the bearer token and a body
    // whose property set is exactly the §1 names, with the snapshot's values (nulls as null).
    [Fact]
    public async Task Test10_PostHealthAsync_sends_exact_body_and_bearer_token()
    {
        var handler = new MockHttpMessageHandler();
        var httpClient = new HttpClient(handler);
        var client = new CaptureSyncHttpClient(httpClient);

        var snapshot = CreateSampleSnapshot(memLimit: null, freeDisk: 99_000_000L);
        var status = await client.PostHealthAsync("http://customsync.local", "test-bearer-token", snapshot);

        Assert.Equal(PostHealthStatus.Success, status);
        var (req, bodyJson) = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, req.Method);
        Assert.Equal("http://customsync.local/api/v1/devices/health", req.RequestUri?.ToString());
        Assert.Equal("Bearer", req.Headers.Authorization?.Scheme);
        Assert.Equal("test-bearer-token", req.Headers.Authorization?.Parameter);

        Assert.NotNull(bodyJson);
        using var doc = JsonDocument.Parse(bodyJson);
        var root = doc.RootElement;

        // Verify property set is EXACTLY the §1 names
        var propNames = root.EnumerateObject().Select(p => p.Name).OrderBy(n => n).ToList();
        var expectedNames = new List<string>
        {
            "cache_db_bytes",
            "free_disk_bytes",
            "media_store_bytes",
            "media_store_files",
            "memory_limit_bytes",
            "rss_bytes",
            "tdlib_database_bytes",
            "tdlib_files_bytes"
        };
        Assert.Equal(expectedNames, propNames);

        // Verify values
        Assert.Equal(snapshot.ProcessRssBytes, root.GetProperty("rss_bytes").GetInt64());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("memory_limit_bytes").ValueKind);
        Assert.Equal(snapshot.CacheDatabaseBytes, root.GetProperty("cache_db_bytes").GetInt64());
        Assert.Equal(snapshot.MediaStoreBytes, root.GetProperty("media_store_bytes").GetInt64());
        Assert.Equal(snapshot.MediaStoreFiles, root.GetProperty("media_store_files").GetInt32());
        Assert.Equal(snapshot.TdlibFilesBytes, root.GetProperty("tdlib_files_bytes").GetInt64());
        Assert.Equal(snapshot.TdlibDatabaseBytes, root.GetProperty("tdlib_database_bytes").GetInt64());
        Assert.Equal(snapshot.FreeDiskBytes, root.GetProperty("free_disk_bytes").GetInt64());
    }

    // 11. 401 -> one refresh and one retry; a second 401 -> false; count the requests (no loop).
    [Fact]
    public async Task Test11_ReportAsync_401_refreshes_once_and_retries_second_401_returns_false()
    {
        var (config, _, _, dbPath) = CreateSyncTestEnv(enabled: true);
        var handler = new MockHttpMessageHandler();
        int healthRequests = 0;
        int refreshRequests = 0;

        handler.ResponseFunc = req =>
        {
            var uri = req.RequestUri?.ToString() ?? "";
            if (uri.Contains("/devices/health"))
            {
                healthRequests++;
                return new HttpResponseMessage(HttpStatusCode.Unauthorized);
            }
            if (uri.Contains("/devices/refresh"))
            {
                refreshRequests++;
                var resp = new
                {
                    refresh_token = "new-refresh-token",
                    access_token = "new-access-token",
                    expires_at = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds()
                };
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(JsonSerializer.Serialize(resp))
                };
            }
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        };

        var httpClient = new HttpClient(handler);
        var syncClient = new CaptureSyncHttpClient(httpClient);
        var cache = new MessageCache(dbPath);
        var runner = new CaptureSyncRunner(cache, syncClient, config);
        var reporter = new CaptureHealthReporter(runner);

        var snapshot = CreateSampleSnapshot();
        var success = await reporter.ReportAsync(snapshot);

        Assert.False(success);
        Assert.Equal(2, healthRequests); // 1 initial + 1 retry after 401
        Assert.Equal(2, refreshRequests); // 1 initial + 1 forced refresh after 401
    }

    // 12. 5xx and a network error -> false, no exception; the log has the exception type and no URL, token or path.
    [Fact]
    public async Task Test12_ReportAsync_server_error_and_network_error_returns_false_and_logs_exception_type_only()
    {
        var (config, _, _, dbPath) = CreateSyncTestEnv(enabled: true);

        // 12a: 500 Internal Server Error
        var handler500 = new MockHttpMessageHandler
        {
            ResponseFunc = req =>
            {
                if (req.RequestUri?.ToString().Contains("/devices/refresh") == true)
                {
                    var resp = new { refresh_token = "r", access_token = "a", expires_at = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds() };
                    return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(resp)) };
                }
                return new HttpResponseMessage(HttpStatusCode.InternalServerError);
            }
        };
        var syncClient500 = new CaptureSyncHttpClient(new HttpClient(handler500));
        var runner500 = new CaptureSyncRunner(new MessageCache(dbPath), syncClient500, config);
        var reporter500 = new CaptureHealthReporter(runner500);

        var res500 = await reporter500.ReportAsync(CreateSampleSnapshot());
        Assert.False(res500);

        // 12b: Network error (HttpRequestException)
        var logger = new LevelLogger<CaptureSyncHttpClient>();
        var handlerNetErr = new MockHttpMessageHandler
        {
            ResponseFunc = req =>
            {
                if (req.RequestUri?.ToString().Contains("/devices/refresh") == true)
                {
                    var resp = new { refresh_token = "r", access_token = "a", expires_at = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds() };
                    return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(resp)) };
                }
                throw new HttpRequestException("connection refused");
            }
        };
        var syncClientNetErr = new CaptureSyncHttpClient(new HttpClient(handlerNetErr), logger);
        var runnerNetErr = new CaptureSyncRunner(new MessageCache(dbPath), syncClientNetErr, config);
        var reporterNetErr = new CaptureHealthReporter(runnerNetErr);

        var resNet = await reporterNetErr.ReportAsync(CreateSampleSnapshot());
        Assert.False(resNet);

        // Check logger
        Assert.Contains(logger.Entries, e => e.Text.Contains("HttpRequestException"));
        Assert.DoesNotContain(logger.AllText, "http://");
        Assert.DoesNotContain(logger.AllText, "localhost");
        Assert.DoesNotContain(logger.AllText, "Bearer");
        Assert.DoesNotContain(logger.AllText, "token");
    }

    // 13. Capture:Sync:Enabled false -> no request at all.
    [Fact]
    public async Task Test13_ReportAsync_sync_disabled_sends_no_requests()
    {
        var (config, _, _, dbPath) = CreateSyncTestEnv(enabled: false);
        var handler = new MockHttpMessageHandler();
        var syncClient = new CaptureSyncHttpClient(new HttpClient(handler));
        var runner = new CaptureSyncRunner(new MessageCache(dbPath), syncClient, config);
        var reporter = new CaptureHealthReporter(runner);

        var result = await reporter.ReportAsync(CreateSampleSnapshot());
        Assert.False(result);
        Assert.Empty(handler.Requests);
    }

    // 14. Real registrations: AddCaptureHandlers + AddCaptureSyncClient -> the reporter is the real one;
    // AddCaptureHandlers alone -> the null one.
    [Fact]
    public void Test14_Real_registrations_wire_null_and_real_reporters()
    {
        var (config, _, _, _) = CreateSyncTestEnv(enabled: true);

        // 14a: AddCaptureHandlers alone -> NullCaptureHealthReporter
        var services1 = new ServiceCollection();
        services1.AddSingleton<IConfiguration>(config);
        services1.AddSingleton<ITdTransport>(new QueueTransport());
        services1.AddSingleton<ITdClient, TdClient>();
        CaptureCacheRegistration.AddMessageCache(services1, config);
        CaptureHandlerRegistration.AddCaptureHandlers(services1);

        var sp1 = services1.BuildServiceProvider();
        var reporter1 = sp1.GetRequiredService<ICaptureHealthReporter>();
        Assert.IsType<NullCaptureHealthReporter>(reporter1);

        // 14b: AddCaptureHandlers + AddCaptureSyncClient -> CaptureHealthReporter
        var services2 = new ServiceCollection();
        services2.AddSingleton<IConfiguration>(config);
        services2.AddSingleton<ITdTransport>(new QueueTransport());
        services2.AddSingleton<ITdClient, TdClient>();
        CaptureCacheRegistration.AddMessageCache(services2, config);
        CaptureHandlerRegistration.AddCaptureHandlers(services2);
        CaptureSyncRegistration.AddCaptureSyncClient(services2, config);

        var sp2 = services2.BuildServiceProvider();
        var reporter2 = sp2.GetRequiredService<ICaptureHealthReporter>();
        Assert.IsType<CaptureHealthReporter>(reporter2);
    }

    private sealed class MockReporter : ICaptureHealthReporter
    {
        public readonly List<StorageSnapshot> Reported = new();
        public Func<StorageSnapshot, CancellationToken, Task<bool>>? Callback { get; set; }

        public Task<bool> ReportAsync(StorageSnapshot snapshot, CancellationToken ct = default)
        {
            Reported.Add(snapshot);
            if (Callback != null) return Callback(snapshot, ct);
            return Task.FromResult(true);
        }
    }

    // 15. A StorageMaintenance run sends exactly one report carrying that run's snapshot;
    // a reporter that throws does not stop the loop (the next run happens);
    // a shutdown while the report is in flight ends the loop with no warning or error logged.
    [Fact]
    public async Task Test15_StorageMaintenance_sends_report_and_handles_exception_and_cancellation()
    {
        var transport = new QueueTransport { Reply = StorageReplies };
        using var client = new TdClient(transport);
        var cache = new MessageCache(TempPath(".db"));
        cache.Initialize();
        var store = new MediaStore(TempDir());
        var logger = new LevelLogger<StorageMaintenance>();

        // 15a: One run sends exactly one report carrying that run's snapshot
        var reporter = new MockReporter();
        var maintenance = new StorageMaintenance(
            cache, store, client, FixedDiskSpaceProbe.Ample(),
            new MediaCaptureConfig(), logger: logger, reporter: reporter);

        await maintenance.RunOnceAsync();
        var snapshot = Assert.Single(reporter.Reported);
        Assert.Equal(maintenance.LatestSnapshot, snapshot);

        // 15b: A reporter that throws does not stop the loop (the next run happens)
        var throwReporter = new MockReporter
        {
            Callback = (s, ct) => throw new InvalidOperationException("mock reporter failure")
        };
        using var cts = new CancellationTokenSource();
        int runs = 0;
        var loopMaintenance = new StorageMaintenance(
            cache, store, client, FixedDiskSpaceProbe.Ample(),
            new MediaCaptureConfig(), logger: logger, reporter: throwReporter,
            delay: (_, ct) =>
            {
                runs++;
                if (runs >= 2) cts.Cancel();
                return Task.CompletedTask;
            });

        await loopMaintenance.RunLoopAsync(cts.Token);
        Assert.True(runs >= 2, "Loop must continue even if reporter throws");
        Assert.Contains(logger.Entries, e => e.Text.Contains("InvalidOperationException"));
        Assert.DoesNotContain(logger.Entries, e => e.Level == LogLevel.Error);

        // 15c: A shutdown while the report is in flight ends the loop with no warning or error logged
        var cancelLogger = new LevelLogger<StorageMaintenance>();
        var cancelCache = new MessageCache(TempPath(".db"));
        cancelCache.Initialize();
        using var shutdownCts = new CancellationTokenSource();
        var inflightReporter = new MockReporter
        {
            Callback = async (s, ct) =>
            {
                shutdownCts.Cancel();
                await Task.Delay(50, ct);
                return true;
            }
        };

        var shutdownMaintenance = new StorageMaintenance(
            cancelCache, store, client, FixedDiskSpaceProbe.Ample(),
            new MediaCaptureConfig(), logger: cancelLogger, reporter: inflightReporter);

        await shutdownMaintenance.RunLoopAsync(shutdownCts.Token);
        Assert.DoesNotContain(cancelLogger.Entries, e => e.Level >= LogLevel.Warning);
    }
}
