using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CustomSync.Capture;
using CustomSync.Capture.Capture;
using CustomSync.Capture.Media;
using CustomSync.Capture.Preflight;
using CustomSync.Capture.Sync;
using CustomSync.Capture.Tdlib;
using CustomSync.Core;
using CustomSync.Core.Contracts;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Xunit;

namespace CustomSync.Tests;

public class CaptureMediaTests : IDisposable
{
    private readonly List<string> _tempFiles = new();

    private string CreateTempFile(string? extension = null)
    {
        var path = Path.Combine(Path.GetTempPath(), $"capture_media_test_{Guid.NewGuid():N}{extension ?? ".tmp"}");
        _tempFiles.Add(path);
        return path;
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        foreach (var file in _tempFiles)
        {
            try
            {
                if (File.Exists(file)) File.Delete(file);
                if (Directory.Exists(file)) Directory.Delete(file, true);
            }
            catch { }
        }
    }

    private static void InsertOutboxRow(
        string dbPath,
        string kind,
        string accountId,
        string peerId,
        long msgId,
        long occurredAt,
        long observedAt,
        string payloadJson)
    {
        using var conn = new SqliteConnection($"Data Source={dbPath}");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            INSERT INTO capture_outbox (kind, account_id, peer_id, msg_id, occurred_at, observed_at, payload_json, created_at)
            VALUES (@kind, @acc, @peer, @msg, @occ, @obs, @payload, @created);";
        cmd.Parameters.AddWithValue("@kind", kind);
        cmd.Parameters.AddWithValue("@acc", accountId);
        cmd.Parameters.AddWithValue("@peer", peerId);
        cmd.Parameters.AddWithValue("@msg", msgId);
        cmd.Parameters.AddWithValue("@occ", occurredAt);
        cmd.Parameters.AddWithValue("@obs", observedAt);
        cmd.Parameters.AddWithValue("@payload", payloadJson);
        cmd.Parameters.AddWithValue("@created", observedAt);
        cmd.ExecuteNonQuery();
    }

    private class TestLoggerProvider : ILoggerProvider
    {
        public readonly List<string> Messages = new();
        public ILogger CreateLogger(string categoryName) => new TestLogger(this);
        public void Dispose() { }

        private class TestLogger : ILogger
        {
            private readonly TestLoggerProvider _p;
            public TestLogger(TestLoggerProvider p) => _p = p;
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                lock (_p.Messages)
                {
                    _p.Messages.Add(formatter(state, exception) + (exception != null ? $" [EX: {exception}]" : ""));
                }
            }
        }
    }

    private class MockHttpMessageHandler : HttpMessageHandler
    {
        public Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> Handler { get; set; }
        public MockHttpMessageHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler)
        {
            Handler = handler;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Handler(request, cancellationToken);
    }

    private class MockTdClient : ITdClient
    {
        public int ClientId => 1;
        public int PendingRequestCount => 0;
        public event Action<string>? UpdateReceived;
        public readonly List<string> SentRequests = new();
        public Func<string, string>? RequestHandler { get; set; }

        public Task<string> SendAsync(string requestJson, TimeSpan? timeout = null, CancellationToken ct = default)
        {
            SentRequests.Add(requestJson);
            if (RequestHandler != null)
            {
                return Task.FromResult(RequestHandler(requestJson));
            }
            return Task.FromResult("{\"@type\":\"ok\"}");
        }

        public string? Execute(string requestJson) => null;
        public void EmitUpdate(string updateJson) => UpdateReceived?.Invoke(updateJson);
        public void Dispose() { }
    }

    private (string keyPath, string statePath, byte[] masterKey) SetupCredentials()
    {
        var keyPath = CreateTempFile(".key");
        var statePath = CreateTempFile(".json");
        var masterKey = new byte[32];
        RandomNumberGenerator.Fill(masterKey);
        File.WriteAllText(keyPath, Convert.ToHexString(masterKey).ToLowerInvariant());
        File.WriteAllText(statePath, JsonSerializer.Serialize(new { device_id = "test-device", refresh_token = "ref-123" }));
        return (keyPath, statePath, masterKey);
    }

    // Test 04: Deleted record with downloaded media calls HEAD; on 200 uses X-Nonce from server; pushes record with media ref; on created marks uploaded (break k)
    [Fact]
    public async Task Test04_Deleted_record_with_downloaded_media_calls_Head_uses_X_Nonce_and_pushes_with_media_ref()
    {
        var dbPath = CreateTempFile(".db");
        var cache = new MessageCache(dbPath);
        cache.Initialize();

        var (keyPath, statePath, masterKey) = SetupCredentials();

        var mediaFile = CreateTempFile(".bin");
        var mediaBytes = Encoding.UTF8.GetBytes("sample secret image bytes");
        await File.WriteAllBytesAsync(mediaFile, mediaBytes);
        var sha256 = Convert.ToHexString(SHA256.HashData(mediaBytes)).ToLowerInvariant();

        var payload = JsonSerializer.Serialize(new { account_id = "acc1", peer_id = "user123", text = "hello" });
        InsertOutboxRow(dbPath, "deleted", "acc1", "user123", 100, 1000, 1000, payload);

        cache.QueueCapturedMedia("user123", 100, 555, 100, "messagePhoto", mediaBytes.Length);
        cache.UpdateMediaDownloaded("user123", 100, mediaFile, sha256, mediaBytes.Length);

        var serverNonce = RandomNumberGenerator.GetBytes(12);
        var serverNonceB64 = Convert.ToBase64String(serverNonce);
        bool headCalled = false;
        bool putCalled = false;
        SyncRecord? pushedRecord = null;

        var handler = new MockHttpMessageHandler(async (req, ct) =>
        {
            if (req.Method == HttpMethod.Post && req.RequestUri!.AbsolutePath.EndsWith("/api/v1/devices/refresh"))
            {
                var resp = new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(JsonSerializer.Serialize(new { refresh_token = "ref-123", access_token = "acc-token", expires_at = 9999999999L }))
                };
                return resp;
            }
            if (req.Method == HttpMethod.Head && req.RequestUri!.AbsolutePath.Contains($"/api/v1/media/{sha256}"))
            {
                headCalled = true;
                var resp = new HttpResponseMessage(HttpStatusCode.OK);
                resp.Headers.Add("X-Nonce", serverNonceB64);
                return resp;
            }
            if (req.Method == HttpMethod.Put && req.RequestUri!.AbsolutePath.Contains("/api/v1/media/"))
            {
                putCalled = true;
                return new HttpResponseMessage(HttpStatusCode.Created);
            }
            if (req.Method == HttpMethod.Post && req.RequestUri!.AbsolutePath.EndsWith("/api/v1/sync/push"))
            {
                var json = await req.Content!.ReadAsStringAsync(ct);
                using var doc = JsonDocument.Parse(json);
                var recordsElem = doc.RootElement.GetProperty("records");
                var rec = recordsElem[0];
                var mediaElem = rec.GetProperty("media");
                var m0 = mediaElem[0];
                pushedRecord = new SyncRecord
                {
                    RecordId = rec.GetProperty("record_id").GetString()!,
                    Kind = rec.GetProperty("kind").GetString()!,
                    AccountHash = rec.GetProperty("account_hash").GetString()!,
                    PeerHash = rec.GetProperty("peer_hash").GetString()!,
                    MsgId = rec.GetProperty("msg_id").GetInt64(),
                    OccurredAt = rec.GetProperty("occurred_at").GetInt64(),
                    ObservedAt = rec.GetProperty("observed_at").GetInt64(),
                    DeviceId = rec.GetProperty("device_id").GetString()!,
                    Nonce = Convert.FromBase64String(rec.GetProperty("nonce").GetString()!),
                    Payload = Convert.FromBase64String(rec.GetProperty("payload").GetString()!),
                    Media = new[]
                    {
                        new MediaRef
                        {
                            Hash = m0.GetProperty("hash").GetString()!,
                            Size = m0.GetProperty("size").GetInt64(),
                            Nonce = Convert.FromBase64String(m0.GetProperty("nonce").GetString()!)
                        }
                    }
                };

                var results = new[] { new { record_id = pushedRecord.RecordId, status = "created", seq = 1L } };
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(JsonSerializer.Serialize(new { results }))
                };
            }
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Capture:Sync:Enabled"] = "true",
            ["Capture:Sync:ServerUrl"] = "https://localhost:5001",
            ["Capture:Sync:MasterKeyPath"] = keyPath,
            ["Capture:Sync:StatePath"] = statePath
        }).Build();

        var loggerProvider = new TestLoggerProvider();
        var loggerFactory = LoggerFactory.Create(b => b.AddProvider(loggerProvider).SetMinimumLevel(LogLevel.Trace));
        var client = new CaptureSyncHttpClient(new HttpClient(handler), loggerFactory.CreateLogger<CaptureSyncHttpClient>());
        var runner = new CaptureSyncRunner(cache, client, config, logger: loggerFactory.CreateLogger<CaptureSyncRunner>());

        var pushOk = await runner.PushCycleAsync();
        Assert.True(pushOk, string.Join(" | ", loggerProvider.Messages));
        Assert.True(headCalled);
        Assert.False(putCalled); // HEAD 200 -> no PUT needed!
        Assert.NotNull(pushedRecord);
        var mediaRef = Assert.Single(pushedRecord.Media);
        Assert.Equal(sha256, mediaRef.Hash);
        Assert.Equal(mediaBytes.Length, mediaRef.Size);
        // Break k check: must use the server nonce from X-Nonce, NOT a fresh one!
        Assert.Equal(serverNonce, mediaRef.Nonce);

        // Media row is marked uploaded
        var mRow = cache.GetCapturedMedia("user123", 100);
        Assert.NotNull(mRow);
        Assert.Equal("uploaded", mRow.Status);
    }

    // Test 05: On HEAD 404, encrypts media with media key, fresh nonce, calls PUT; on 201/200 attaches media ref and marks uploaded
    [Fact]
    public async Task Test05_Deleted_record_with_Head_404_encrypts_and_Puts_media_and_pushes_record()
    {
        var dbPath = CreateTempFile(".db");
        var cache = new MessageCache(dbPath);
        cache.Initialize();

        var (keyPath, statePath, masterKey) = SetupCredentials();

        var mediaFile = CreateTempFile(".bin");
        var mediaBytes = Encoding.UTF8.GetBytes("another secret payload data");
        await File.WriteAllBytesAsync(mediaFile, mediaBytes);
        var sha256 = Convert.ToHexString(SHA256.HashData(mediaBytes)).ToLowerInvariant();

        var payload = JsonSerializer.Serialize(new { account_id = "acc1", peer_id = "user123", text = "hello" });
        InsertOutboxRow(dbPath, "deleted", "acc1", "user123", 101, 1000, 1000, payload);

        cache.QueueCapturedMedia("user123", 101, 555, 101, "messageVideo", mediaBytes.Length);
        cache.UpdateMediaDownloaded("user123", 101, mediaFile, sha256, mediaBytes.Length);

        bool headCalled = false;
        bool putCalled = false;
        byte[]? putWireBlob = null;
        string? putNonceB64 = null;
        SyncRecord? pushedRecord = null;

        var handler = new MockHttpMessageHandler(async (req, ct) =>
        {
            if (req.Method == HttpMethod.Post && req.RequestUri!.AbsolutePath.EndsWith("/api/v1/devices/refresh"))
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(JsonSerializer.Serialize(new { refresh_token = "ref-123", access_token = "acc-token", expires_at = 9999999999L }))
                };
            }
            if (req.Method == HttpMethod.Head && req.RequestUri!.AbsolutePath.Contains($"/api/v1/media/{sha256}"))
            {
                headCalled = true;
                return new HttpResponseMessage(HttpStatusCode.NotFound);
            }
            if (req.Method == HttpMethod.Put && req.RequestUri!.AbsolutePath.Contains($"/api/v1/media/{sha256}"))
            {
                putCalled = true;
                putWireBlob = await req.Content!.ReadAsByteArrayAsync(ct);
                putNonceB64 = req.Headers.GetValues("X-Nonce").FirstOrDefault();
                return new HttpResponseMessage(HttpStatusCode.Created);
            }
            if (req.Method == HttpMethod.Post && req.RequestUri!.AbsolutePath.EndsWith("/api/v1/sync/push"))
            {
                var json = await req.Content!.ReadAsStringAsync(ct);
                using var doc = JsonDocument.Parse(json);
                var rec = doc.RootElement.GetProperty("records")[0];
                var m0 = rec.GetProperty("media")[0];
                pushedRecord = new SyncRecord
                {
                    RecordId = rec.GetProperty("record_id").GetString()!,
                    Kind = rec.GetProperty("kind").GetString()!,
                    AccountHash = rec.GetProperty("account_hash").GetString()!,
                    PeerHash = rec.GetProperty("peer_hash").GetString()!,
                    MsgId = rec.GetProperty("msg_id").GetInt64(),
                    OccurredAt = rec.GetProperty("occurred_at").GetInt64(),
                    ObservedAt = rec.GetProperty("observed_at").GetInt64(),
                    DeviceId = rec.GetProperty("device_id").GetString()!,
                    Nonce = Convert.FromBase64String(rec.GetProperty("nonce").GetString()!),
                    Payload = Convert.FromBase64String(rec.GetProperty("payload").GetString()!),
                    Media = new[]
                    {
                        new MediaRef
                        {
                            Hash = m0.GetProperty("hash").GetString()!,
                            Size = m0.GetProperty("size").GetInt64(),
                            Nonce = Convert.FromBase64String(m0.GetProperty("nonce").GetString()!)
                        }
                    }
                };

                var results = new[] { new { record_id = pushedRecord.RecordId, status = "created", seq = 1L } };
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(JsonSerializer.Serialize(new { results }))
                };
            }
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Capture:Sync:Enabled"] = "true",
            ["Capture:Sync:ServerUrl"] = "https://localhost:5001",
            ["Capture:Sync:MasterKeyPath"] = keyPath,
            ["Capture:Sync:StatePath"] = statePath
        }).Build();

        var client = new CaptureSyncHttpClient(new HttpClient(handler));
        var runner = new CaptureSyncRunner(cache, client, config);

        var pushOk = await runner.PushCycleAsync();
        Assert.True(pushOk);
        Assert.True(headCalled);
        Assert.True(putCalled);
        Assert.NotNull(putWireBlob);
        Assert.NotNull(putNonceB64);

        // Verify PUT ciphertext can be decrypted with derived media key
        var mediaKey = SyncCrypto.DeriveMediaKey(masterKey);
        var putNonce = Convert.FromBase64String(putNonceB64);
        var decrypted = SyncCrypto.DecryptPayload(mediaKey, putNonce, putWireBlob);
        Assert.Equal(mediaBytes, decrypted);

        Assert.NotNull(pushedRecord);
        var mediaRef = Assert.Single(pushedRecord.Media);
        Assert.Equal(sha256, mediaRef.Hash);
        Assert.Equal(putNonce, mediaRef.Nonce);

        var mRow = cache.GetCapturedMedia("user123", 101);
        Assert.NotNull(mRow);
        Assert.Equal("uploaded", mRow.Status);
    }

    // Test 06: On PUT 507 Insufficient Storage, keeps outbox row for retry, does not push record now
    [Fact]
    public async Task Test06_On_Put_507_insufficient_storage_keeps_outbox_row_for_retry()
    {
        var dbPath = CreateTempFile(".db");
        var cache = new MessageCache(dbPath);
        cache.Initialize();

        var (keyPath, statePath, masterKey) = SetupCredentials();

        var mediaFile = CreateTempFile(".bin");
        var mediaBytes = Encoding.UTF8.GetBytes("sample 507 storage content");
        await File.WriteAllBytesAsync(mediaFile, mediaBytes);
        var sha256 = Convert.ToHexString(SHA256.HashData(mediaBytes)).ToLowerInvariant();

        var payload = JsonSerializer.Serialize(new { account_id = "acc1", peer_id = "user123", text = "hello" });
        InsertOutboxRow(dbPath, "deleted", "acc1", "user123", 102, 1000, 1000, payload);

        cache.QueueCapturedMedia("user123", 102, 555, 102, "messageDocument", mediaBytes.Length);
        cache.UpdateMediaDownloaded("user123", 102, mediaFile, sha256, mediaBytes.Length);

        bool pushCalled = false;

        var handler = new MockHttpMessageHandler((req, ct) =>
        {
            if (req.Method == HttpMethod.Post && req.RequestUri!.AbsolutePath.EndsWith("/api/v1/devices/refresh"))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(JsonSerializer.Serialize(new { refresh_token = "ref-123", access_token = "acc-token", expires_at = 9999999999L }))
                });
            }
            if (req.Method == HttpMethod.Head)
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
            }
            if (req.Method == HttpMethod.Put)
            {
                return Task.FromResult(new HttpResponseMessage((HttpStatusCode)507));
            }
            if (req.Method == HttpMethod.Post && req.RequestUri!.AbsolutePath.EndsWith("/api/v1/sync/push"))
            {
                pushCalled = true;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        });

        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Capture:Sync:Enabled"] = "true",
            ["Capture:Sync:ServerUrl"] = "https://localhost:5001",
            ["Capture:Sync:MasterKeyPath"] = keyPath,
            ["Capture:Sync:StatePath"] = statePath
        }).Build();

        var client = new CaptureSyncHttpClient(new HttpClient(handler));
        var runner = new CaptureSyncRunner(cache, client, config);

        var pushResult = await runner.PushCycleAsync();
        Assert.True(pushResult); // Empty batch returns true
        Assert.False(pushCalled); // Record must NOT have been pushed!

        // Outbox row kept!
        var rows = cache.GetOutboxRows();
        Assert.Single(rows);
        Assert.Equal(102, rows[0].MsgId);
    }

    // Test 07: MediaDownloader gets message, calls downloadFile, validates size, hashes, marks downloaded
    [Fact]
    public async Task Test07_MediaDownloader_downloads_file_computes_sha256_and_marks_downloaded()
    {
        var dbPath = CreateTempFile(".db");
        var cache = new MessageCache(dbPath);
        cache.Initialize();

        var mediaFile = CreateTempFile(".bin");
        var fileBytes = Encoding.UTF8.GetBytes("sample photo content from tdlib");
        await File.WriteAllBytesAsync(mediaFile, fileBytes);
        var expectedSha256 = Convert.ToHexString(SHA256.HashData(fileBytes)).ToLowerInvariant();

        var storeDir = Path.Combine(Path.GetTempPath(), "test-store-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(storeDir);
        var mediaStore = new MediaStore(storeDir);

        cache.QueueCapturedMedia("456", 200, 777, 200, "messagePhoto", fileBytes.Length);

        var tdClient = new MockTdClient
        {
            RequestHandler = reqJson =>
            {
                using var doc = JsonDocument.Parse(reqJson);
                var type = doc.RootElement.GetProperty("@type").GetString();
                if (type == "getMessage")
                {
                    return $$"""
                    {
                        "@type": "message",
                        "id": 200,
                        "chat_id": 777,
                        "content": {
                            "@type": "messagePhoto",
                            "photo": {
                                "sizes": [
                                    {
                                        "photo": {
                                            "@type": "file",
                                            "id": 1234,
                                            "size": {{fileBytes.Length}},
                                            "expected_size": {{fileBytes.Length}},
                                            "local": {
                                                "path": "",
                                                "is_downloading_completed": false
                                            }
                                        }
                                    }
                                ]
                            }
                        }
                    }
                    """;
                }
                if (type == "downloadFile")
                {
                    var escaped = mediaFile.Replace("\\", "\\\\");
                    return $$"""
                    {
                        "@type": "file",
                        "id": 1234,
                        "size": {{fileBytes.Length}},
                        "local": {
                            "path": "{{escaped}}",
                            "is_downloading_completed": true
                        }
                    }
                    """;
                }
                return "{\"@type\":\"ok\"}";
            }
        };

        var config = new MediaCaptureConfig
        {
            Enabled = true,
            PeerIds = new HashSet<string> { "456" },
            MaxBytes = 10485760,
            StorageDirectory = storeDir
        };

        var loggerProvider = new TestLoggerProvider();
        var loggerFactory = LoggerFactory.Create(b => b.AddProvider(loggerProvider).SetMinimumLevel(LogLevel.Trace));
        var downloader = new MediaDownloader(tdClient, cache, config, mediaStore, new MockDiskSpaceProbe(100L * 1024 * 1024 * 1024), logger: loggerFactory.CreateLogger<MediaDownloader>());
        bool processed = await downloader.ProcessPendingOnceAsync();
        Assert.True(processed, string.Join(" | ", loggerProvider.Messages));

        var mediaRow = cache.GetCapturedMedia("456", 200);
        Assert.NotNull(mediaRow);
        Assert.True(mediaRow.Status == "downloaded", $"Status was {mediaRow.Status}, attempts: {mediaRow.Attempts}, logs: {string.Join(" | ", loggerProvider.Messages)}");
        Assert.Equal(mediaStore.PathFor("456", 200), mediaRow.LocalPath);
        Assert.Equal(expectedSha256, mediaRow.Sha256);
        Assert.Equal(fileBytes.Length, mediaRow.Size);
    }

    private class SpyingMediaDownloader : MediaDownloader
    {
        private readonly List<string> _timeline;

        public SpyingMediaDownloader(
            ITdClient client,
            MessageCache cache,
            MediaCaptureConfig config,
            List<string> timeline)
            : base(client, cache, config)
        {
            _timeline = timeline;
        }

        public override void Start(CancellationToken ct = default)
        {
            lock (_timeline)
            {
                _timeline.Add("downloader:start");
            }
            base.Start(ct);
        }
    }

    // Test 08: Worker starts downloader only AFTER invisibility check (break l)
    [Fact]
    public async Task Test08_Worker_starts_downloader_only_after_invisibility_check()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"cm_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Telegram:ApiId"] = "12345",
                ["Telegram:ApiHash"] = "test_hash",
                ["Telegram:DatabaseDirectory"] = tempDir,
                ["Telegram:FilesDirectory"] = tempDir,
                ["Capture:CacheDatabasePath"] = Path.Combine(tempDir, "cache.db"),
                ["Capture:SessionInvisibilityTimeoutSeconds"] = "5",
                ["Capture:Scope:DefaultEnabled"] = "true",
                ["Capture:Sync:Enabled"] = "false",
                ["Capture:Media:Enabled"] = "true",
                ["Capture:Media:PeerIds"] = "456"
            }).Build();

            var transport = new FakeRecordingTdTransport { AutoRepeatReadyState = true };
            transport.IncomingQueue.Enqueue(new JsonObject
            {
                ["@type"] = "updateAuthorizationState",
                ["authorization_state"] = new JsonObject { ["@type"] = "authorizationStateReady" }
            }.ToJsonString());

            var timeline = new List<string>();
            var syncRoot = new object();

            transport.OnSend = (c, req) =>
            {
                using var d = JsonDocument.Parse(req);
                var root = d.RootElement;
                var type = root.GetProperty("@type").GetString();
                var extra = root.TryGetProperty("@extra", out var ex) ? ex.GetString() : null;

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
            services.AddLogging();
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

            // Replace MediaDownloader with SpyingMediaDownloader
            services.Remove(services.First(d => d.ServiceType == typeof(MediaDownloader)));
            services.AddSingleton<MediaDownloader>(sp => new SpyingMediaDownloader(
                sp.GetRequiredService<ITdClient>(),
                sp.GetRequiredService<MessageCache>(),
                sp.GetRequiredService<MediaCaptureConfig>(),
                timeline));

            using var sp = services.BuildServiceProvider();
            sp.GetRequiredService<MessageCache>().Initialize();

            var lifetime = new FakeHostLifetime();
            var logger = new CapturingLogger<Worker>();

            var worker = new Worker(config, sp, lifetime, logger);

            transport.IncomingQueue.Enqueue(new JsonObject
            {
                ["@type"] = "updateAuthorizationState",
                ["authorization_state"] = new JsonObject { ["@type"] = "authorizationStateReady" }
            }.ToJsonString());

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            await worker.StartAsync(cts.Token);

            for (int i = 0; i < 50; i++)
            {
                if (logger.Logs.Any(l => l.Message.Contains("Capture service authorized and running")))
                    break;
                await Task.Delay(50);
            }

            await worker.StopAsync(CancellationToken.None);

            var logMessages = string.Join(" | ", logger.Logs.Select(l => l.Message));

            // Break l check: invisibility setOption MUST come before downloader:start!
            lock (syncRoot)
            {
                int invisIdx = timeline.IndexOf("transport:setOption");
                int dlIdx = timeline.IndexOf("downloader:start");

                Assert.True(invisIdx >= 0, $"Invisibility setOption was never called. Timeline: {string.Join(", ", timeline)}. Logs: {logMessages}");
                Assert.True(dlIdx >= 0, $"Downloader was never started. Timeline: {string.Join(", ", timeline)}");
                Assert.True(invisIdx < dlIdx, "Downloader must not start before invisibility check succeeds!");
            }
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    // Test 09: On PUT 413 Payload Too Large, marks media row skipped, pushes record without media (break i)
    [Fact]
    public async Task Test09_On_Put_413_payload_too_large_marks_media_skipped_and_pushes_without_media()
    {
        var dbPath = CreateTempFile(".db");
        var cache = new MessageCache(dbPath);
        cache.Initialize();

        var (keyPath, statePath, masterKey) = SetupCredentials();

        var mediaFile = CreateTempFile(".bin");
        var mediaBytes = Encoding.UTF8.GetBytes("sample 413 oversized content");
        await File.WriteAllBytesAsync(mediaFile, mediaBytes);
        var sha256 = Convert.ToHexString(SHA256.HashData(mediaBytes)).ToLowerInvariant();

        var payload = JsonSerializer.Serialize(new { account_id = "acc1", peer_id = "user123", text = "hello" });
        InsertOutboxRow(dbPath, "deleted", "acc1", "user123", 103, 1000, 1000, payload);

        cache.QueueCapturedMedia("user123", 103, 555, 103, "messageDocument", mediaBytes.Length);
        cache.UpdateMediaDownloaded("user123", 103, mediaFile, sha256, mediaBytes.Length);

        bool pushedWithoutMedia = false;

        var handler = new MockHttpMessageHandler(async (req, ct) =>
        {
            if (req.Method == HttpMethod.Post && req.RequestUri!.AbsolutePath.EndsWith("/api/v1/devices/refresh"))
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(JsonSerializer.Serialize(new { refresh_token = "ref-123", access_token = "acc-token", expires_at = 9999999999L }))
                };
            }
            if (req.Method == HttpMethod.Head)
            {
                return new HttpResponseMessage(HttpStatusCode.NotFound);
            }
            if (req.Method == HttpMethod.Put)
            {
                return new HttpResponseMessage((HttpStatusCode)413);
            }
            if (req.Method == HttpMethod.Post && req.RequestUri!.AbsolutePath.EndsWith("/api/v1/sync/push"))
            {
                var json = await req.Content!.ReadAsStringAsync(ct);
                using var doc = JsonDocument.Parse(json);
                var rec = doc.RootElement.GetProperty("records")[0];
                var mediaArray = rec.GetProperty("media");
                pushedWithoutMedia = mediaArray.GetArrayLength() == 0;

                var results = new[] { new { record_id = rec.GetProperty("record_id").GetString()!, status = "created", seq = 1L } };
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(JsonSerializer.Serialize(new { results }))
                };
            }
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Capture:Sync:Enabled"] = "true",
            ["Capture:Sync:ServerUrl"] = "https://localhost:5001",
            ["Capture:Sync:MasterKeyPath"] = keyPath,
            ["Capture:Sync:StatePath"] = statePath
        }).Build();

        var client = new CaptureSyncHttpClient(new HttpClient(handler));
        var runner = new CaptureSyncRunner(cache, client, config);

        var pushResult = await runner.PushCycleAsync();
        Assert.True(pushResult);
        // Break i check: must push without media rather than keeping outbox row forever!
        Assert.True(pushedWithoutMedia);

        var mediaRow = cache.GetCapturedMedia("user123", 103);
        Assert.NotNull(mediaRow);
        Assert.Equal("skipped", mediaRow.Status);

        Assert.Empty(cache.GetOutboxRows());
    }

    // Test 10: On missing local file, does NOT keep record forever, pushes record without media (break j)
    [Fact]
    public async Task Test10_On_missing_local_file_pushes_record_without_media_and_does_not_block()
    {
        var dbPath = CreateTempFile(".db");
        var cache = new MessageCache(dbPath);
        cache.Initialize();

        var (keyPath, statePath, masterKey) = SetupCredentials();

        var nonExistentPath = Path.Combine(Path.GetTempPath(), $"missing_file_{Guid.NewGuid():N}.bin");

        var payload = JsonSerializer.Serialize(new { account_id = "acc1", peer_id = "user123", text = "hello" });
        InsertOutboxRow(dbPath, "deleted", "acc1", "user123", 104, 1000, 1000, payload);

        cache.QueueCapturedMedia("user123", 104, 555, 104, "messagePhoto", 5000);
        cache.UpdateMediaDownloaded("user123", 104, nonExistentPath, "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef", 5000);

        bool pushedWithoutMedia = false;

        var handler = new MockHttpMessageHandler(async (req, ct) =>
        {
            if (req.Method == HttpMethod.Post && req.RequestUri!.AbsolutePath.EndsWith("/api/v1/devices/refresh"))
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(JsonSerializer.Serialize(new { refresh_token = "ref-123", access_token = "acc-token", expires_at = 9999999999L }))
                };
            }
            if (req.Method == HttpMethod.Post && req.RequestUri!.AbsolutePath.EndsWith("/api/v1/sync/push"))
            {
                var json = await req.Content!.ReadAsStringAsync(ct);
                using var doc = JsonDocument.Parse(json);
                var rec = doc.RootElement.GetProperty("records")[0];
                var mediaArray = rec.GetProperty("media");
                pushedWithoutMedia = mediaArray.GetArrayLength() == 0;

                var results = new[] { new { record_id = rec.GetProperty("record_id").GetString()!, status = "created", seq = 1L } };
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(JsonSerializer.Serialize(new { results }))
                };
            }
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Capture:Sync:Enabled"] = "true",
            ["Capture:Sync:ServerUrl"] = "https://localhost:5001",
            ["Capture:Sync:MasterKeyPath"] = keyPath,
            ["Capture:Sync:StatePath"] = statePath
        }).Build();

        var client = new CaptureSyncHttpClient(new HttpClient(handler));
        var runner = new CaptureSyncRunner(cache, client, config);

        var pushResult = await runner.PushCycleAsync();
        Assert.True(pushResult);
        // Break j check: must push without media rather than blocking forever
        Assert.True(pushedWithoutMedia);
        Assert.Empty(cache.GetOutboxRows());
    }

    // Test 11: Capture queueing adds to captured_media when enabled and peer allowed
    [Fact]
    public void Test11_Capture_queueing_adds_to_captured_media_when_enabled_and_peer_allowed()
    {
        var dbPath = CreateTempFile(".db");
        var cache = new MessageCache(dbPath);
        cache.Initialize();

        var scope = new AllowAllCaptureScope();
        var mediaConfig = new MediaCaptureConfig
        {
            Enabled = true,
            PeerIds = new HashSet<string> { "111" },
            MaxBytes = 10485760
        };

        var handler = new CaptureUpdateHandler(cache, scope, accountId: "acc1", mediaConfig: mediaConfig);
        handler.HandleUpdate(CreatePhotoUpdate(1000, 111, 1024));

        var mediaRow = cache.GetCapturedMedia("111", 1000);
        Assert.NotNull(mediaRow);
        Assert.Equal("pending", mediaRow.Status);
        Assert.Equal(1024, mediaRow.Size);
        Assert.Equal("messagePhoto", mediaRow.ContentType);
    }

    // Test 12: Capture queueing rejects: disabled (break h), peer not in PeerIds (break h), photo all sizes > MaxBytes, non-photo > MaxBytes, size <= 0, disallowed content type
    [Fact]
    public void Test12_Capture_queueing_ignores_when_disabled_or_peer_not_allowed_or_oversize()
    {
        var dbPath = CreateTempFile(".db");
        var cache = new MessageCache(dbPath);
        cache.Initialize();

        var scope = new AllowAllCaptureScope();

        // 1. Break h: Disabled config
        var disabledConfig = new MediaCaptureConfig { Enabled = false, PeerIds = new HashSet<string> { "111" } };
        var handler1 = new CaptureUpdateHandler(cache, scope, accountId: "acc1", mediaConfig: disabledConfig);
        handler1.HandleUpdate(CreatePhotoUpdate(1001, 111, 1024));
        Assert.Null(cache.GetCapturedMedia("111", 1001));

        // 2. Break h: Peer not in PeerIds
        var peerNotAllowedConfig = new MediaCaptureConfig { Enabled = true, PeerIds = new HashSet<string> { "222" } };
        var handler2 = new CaptureUpdateHandler(cache, scope, accountId: "acc1", mediaConfig: peerNotAllowedConfig);
        handler2.HandleUpdate(CreatePhotoUpdate(1002, 111, 1024));
        Assert.Null(cache.GetCapturedMedia("111", 1002));

        // 3. Oversized media
        var enabledConfig = new MediaCaptureConfig { Enabled = true, PeerIds = new HashSet<string> { "111" }, MaxBytes = 500 };
        var handler3 = new CaptureUpdateHandler(cache, scope, accountId: "acc1", mediaConfig: enabledConfig);
        handler3.HandleUpdate(CreatePhotoUpdate(1003, 111, 1024)); // 1024 > 500
        Assert.Null(cache.GetCapturedMedia("111", 1003));

        // 4. Non-allowed content type (messageText)
        var textUpdate = JsonSerializer.Serialize(new
        {
            @type = "updateNewMessage",
            message = new
            {
                id = 1004L << 20,
                chat_id = 111L,
                date = 1700000000L,
                sender_id = new { @type = "messageSenderUser", user_id = 111L },
                is_outgoing = false,
                content = new
                {
                    @type = "messageText",
                    text = new { text = "not a media" }
                }
            }
        });
        handler3.HandleUpdate(textUpdate);
        Assert.Null(cache.GetCapturedMedia("111", 1004));
    }

    private static string CreatePhotoUpdate(long msgId, long chatId, long size)
    {
        return $$"""
        {
            "@type": "updateNewMessage",
            "message": {
                "id": {{msgId << 20}},
                "chat_id": {{chatId}},
                "date": 1700000000,
                "sender_id": { "@type": "messageSenderUser", "user_id": {{chatId}} },
                "is_outgoing": false,
                "content": {
                    "@type": "messagePhoto",
                    "photo": {
                        "sizes": [
                            {
                                "photo": {
                                    "@type": "file",
                                    "id": 55,
                                    "size": {{size}},
                                    "expected_size": {{size}}
                                }
                            }
                        ]
                    }
                }
            }
        }
        """;
    }

    // Test 13: Non-deleted record or record without downloaded media pushes without media ref
    [Fact]
    public async Task Test13_Non_deleted_record_or_record_without_downloaded_media_pushes_without_media_ref()
    {
        var dbPath = CreateTempFile(".db");
        var cache = new MessageCache(dbPath);
        cache.Initialize();

        var (keyPath, statePath, masterKey) = SetupCredentials();

        // An edited record with downloaded media
        var payloadEdited = JsonSerializer.Serialize(new { account_id = "acc1", peer_id = "user123", old_text = "a", new_text = "b" });
        InsertOutboxRow(dbPath, "edited", "acc1", "user123", 201, 1000, 1000, payloadEdited);
        cache.QueueCapturedMedia("user123", 201, 555, 201, "messagePhoto", 1000);
        cache.UpdateMediaDownloaded("user123", 201, CreateTempFile(".bin"), "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef", 1000);

        // A deleted record with media in pending status
        var payloadDeleted = JsonSerializer.Serialize(new { account_id = "acc1", peer_id = "user123", text = "hello" });
        InsertOutboxRow(dbPath, "deleted", "acc1", "user123", 202, 1000, 1000, payloadDeleted);
        cache.QueueCapturedMedia("user123", 202, 555, 202, "messagePhoto", 1000); // status is 'pending'

        var handler = new MockHttpMessageHandler(async (req, ct) =>
        {
            if (req.Method == HttpMethod.Post && req.RequestUri!.AbsolutePath.EndsWith("/api/v1/devices/refresh"))
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(JsonSerializer.Serialize(new { refresh_token = "ref-123", access_token = "acc-token", expires_at = 9999999999L }))
                };
            }
            if (req.Method == HttpMethod.Post && req.RequestUri!.AbsolutePath.EndsWith("/api/v1/sync/push"))
            {
                var json = await req.Content!.ReadAsStringAsync(ct);
                using var doc = JsonDocument.Parse(json);
                var records = doc.RootElement.GetProperty("records");
                var results = new List<object>();

                foreach (var rec in records.EnumerateArray())
                {
                    var msgId = rec.GetProperty("msg_id").GetInt64();
                    var mediaLen = rec.GetProperty("media").GetArrayLength();
                    var recordId = rec.GetProperty("record_id").GetString()!;
                    if (msgId == 201)
                    {
                        Assert.Equal(0, mediaLen); // edited record does NOT attach media!
                    }
                    if (msgId == 202)
                    {
                        Assert.Equal(0, mediaLen); // pending media does NOT attach media!
                    }
                    results.Add(new { record_id = recordId, status = "created", seq = 1L });
                }

                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(JsonSerializer.Serialize(new { results }))
                };
            }
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Capture:Sync:Enabled"] = "true",
            ["Capture:Sync:ServerUrl"] = "https://localhost:5001",
            ["Capture:Sync:MasterKeyPath"] = keyPath,
            ["Capture:Sync:StatePath"] = statePath
        }).Build();

        var client = new CaptureSyncHttpClient(new HttpClient(handler));
        var runner = new CaptureSyncRunner(cache, client, config);

        var pushResult = await runner.PushCycleAsync();
        Assert.True(pushResult);
    }

    // Test 16: Privacy logging test: file names, file paths, sensitive fields never logged (break m)
    [Fact]
    public async Task Test16_Privacy_logging_never_logs_file_name_path_text_or_secrets()
    {
        var dbPath = CreateTempFile(".db");
        var cache = new MessageCache(dbPath);
        cache.Initialize();

        var (keyPath, statePath, masterKey) = SetupCredentials();

        var secretFileName = "super_secret_personal_photo_9999.jpg";
        var mediaFile = Path.Combine(Path.GetTempPath(), secretFileName);
        _tempFiles.Add(mediaFile);
        var mediaBytes = Encoding.UTF8.GetBytes("secret payload data");
        await File.WriteAllBytesAsync(mediaFile, mediaBytes);
        var sha256 = Convert.ToHexString(SHA256.HashData(mediaBytes)).ToLowerInvariant();

        var secretText = "confidential_passport_info_12345";
        var payload = JsonSerializer.Serialize(new { account_id = "acc1", peer_id = "user777888", text = secretText });
        InsertOutboxRow(dbPath, "deleted", "acc1", "user777888", 999, 1000, 1000, payload);

        cache.QueueCapturedMedia("user777888", 999, 555, 999, "messagePhoto", mediaBytes.Length);
        cache.UpdateMediaDownloaded("user777888", 999, mediaFile, sha256, mediaBytes.Length);

        var loggerProvider = new TestLoggerProvider();
        var loggerFactory = LoggerFactory.Create(b => b.AddProvider(loggerProvider).SetMinimumLevel(LogLevel.Trace));

        var handler = new MockHttpMessageHandler(async (req, ct) =>
        {
            if (req.Method == HttpMethod.Post && req.RequestUri!.AbsolutePath.EndsWith("/api/v1/devices/refresh"))
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(JsonSerializer.Serialize(new { refresh_token = "ref-123", access_token = "acc-token", expires_at = 9999999999L }))
                };
            }
            if (req.Method == HttpMethod.Head)
            {
                return new HttpResponseMessage(HttpStatusCode.OK);
            }
            if (req.Method == HttpMethod.Post && req.RequestUri!.AbsolutePath.EndsWith("/api/v1/sync/push"))
            {
                var json = await req.Content!.ReadAsStringAsync(ct);
                using var doc = JsonDocument.Parse(json);
                var rec = doc.RootElement.GetProperty("records")[0];
                var results = new[] { new { record_id = rec.GetProperty("record_id").GetString()!, status = "created", seq = 1L } };
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(JsonSerializer.Serialize(new { results }))
                };
            }
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Capture:Sync:Enabled"] = "true",
            ["Capture:Sync:ServerUrl"] = "https://localhost:5001",
            ["Capture:Sync:MasterKeyPath"] = keyPath,
            ["Capture:Sync:StatePath"] = statePath
        }).Build();

        var client = new CaptureSyncHttpClient(new HttpClient(handler), loggerFactory.CreateLogger<CaptureSyncHttpClient>());
        var runner = new CaptureSyncRunner(cache, client, config, logger: loggerFactory.CreateLogger<CaptureSyncRunner>());

        await runner.PushCycleAsync();

        // Also test downloader logs
        var tdClient = new MockTdClient
        {
            RequestHandler = _ => JsonSerializer.Serialize(new
            {
                @type = "error",
                code = 404,
                message = "Not found"
            })
        };
        var dlConfig = new MediaCaptureConfig { Enabled = true, PeerIds = new HashSet<string> { "user777888" } };
        var downloader = new MediaDownloader(tdClient, cache, dlConfig, logger: loggerFactory.CreateLogger<MediaDownloader>());
        await downloader.ProcessPendingOnceAsync();

        // Break m check: verify no secret markers appeared in ANY log message
        foreach (var log in loggerProvider.Messages)
        {
            Assert.DoesNotContain(secretFileName, log);
            Assert.DoesNotContain(mediaFile, log);
            Assert.DoesNotContain(secretText, log);
            Assert.DoesNotContain("ref-123", log);
            Assert.DoesNotContain("acc-token", log);
        }
    }

    private class MockNativeProbe(bool canLoad) : INativeLibraryProbe
    {
        public bool CanLoad(string? path = null) => canLoad;
    }

    private class MockConsolePrompt : IConsolePrompt
    {
        public string? Prompt(string message, bool isSecret = false) => "12345";
    }

    private class MockHostApplicationLifetime : IHostApplicationLifetime
    {
        public CancellationToken ApplicationStarted => CancellationToken.None;
        public CancellationToken ApplicationStopping => CancellationToken.None;
        public CancellationToken ApplicationStopped => CancellationToken.None;
        public void StopApplication() { }
    }

    private class MockDiskSpaceProbe(long? freeBytes) : IDiskSpaceProbe
    {
        public long? GetAvailableFreeBytes(string path) => freeBytes;
    }
}
