using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CustomSync.Capture.Capture;
using CustomSync.Capture.Preflight;
using CustomSync.Capture.Sync;
using CustomSync.Capture.Tdlib;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace CustomSync.Tests;

public class CaptureKeyWrapTests
{
    private class TestLoggerProvider : ILoggerProvider
    {
        public readonly List<string> Messages = new();

        public ILogger CreateLogger(string categoryName) => new TestLogger(this);
        public void Dispose() { }

        private class TestLogger : ILogger
        {
            private readonly TestLoggerProvider _provider;
            public TestLogger(TestLoggerProvider provider) => _provider = provider;
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                lock (_provider.Messages)
                {
                    _provider.Messages.Add(formatter(state, exception));
                }
            }
        }
    }

    private class MockConsolePrompt : IConsolePrompt
    {
        private readonly Queue<string?> _inputs = new();
        public readonly List<string> PromptsAsked = new();

        public void Enqueue(string? input) => _inputs.Enqueue(input);
        public string? Prompt(string message, bool isSecret = false)
        {
            PromptsAsked.Add(message);
            return _inputs.Count > 0 ? _inputs.Dequeue() : null;
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

    private static void EnqueueOutbox(string dbPath, string kind, string accountId, string peerId, long msgId, long occurredAt, long observedAt, string payloadJson)
    {
        using var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={dbPath}");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            INSERT INTO capture_outbox (kind, account_id, peer_id, msg_id, occurred_at, observed_at, payload_json, created_at)
            VALUES (@k, @acc, @peer, @msg, @occ, @obs, @payload, @created);";
        cmd.Parameters.AddWithValue("@k", kind);
        cmd.Parameters.AddWithValue("@acc", accountId);
        cmd.Parameters.AddWithValue("@peer", peerId);
        cmd.Parameters.AddWithValue("@msg", msgId);
        cmd.Parameters.AddWithValue("@occ", occurredAt);
        cmd.Parameters.AddWithValue("@obs", observedAt);
        cmd.Parameters.AddWithValue("@payload", payloadJson);
        cmd.Parameters.AddWithValue("@created", observedAt);
        cmd.ExecuteNonQuery();
    }

    // 1. key_wrap vectors: both cases unwrap to master_hex; each wrong_passphrase gives "wrong passphrase", not an exception.
    [Fact]
    public void Test01_Key_wrap_vectors_unwrap_to_master_hex_and_wrong_passphrase_fails_without_exception()
    {
        var cases = TestVectors.Get("key_wrap").GetProperty("cases");
        foreach (var c in cases.EnumerateArray())
        {
            string passphrase = c.GetProperty("passphrase").GetString()!;
            string wrongPassphrase = c.GetProperty("wrong_passphrase").GetString()!;
            int iterations = c.GetProperty("iterations").GetInt32();
            byte[] salt = Convert.FromBase64String(c.GetProperty("salt").GetString()!);
            byte[] nonce = Convert.FromBase64String(c.GetProperty("nonce").GetString()!);
            byte[] wrappedKey = Convert.FromBase64String(c.GetProperty("wrapped_key").GetString()!);
            string expectedMasterHex = c.GetProperty("master_hex").GetString()!;

            // Correct passphrase unwraps successfully
            var result = SyncCrypto.UnwrapMasterKey(passphrase, salt, iterations, nonce, wrappedKey);
            Assert.Equal(KeyUnwrapStatus.Success, result.Status);
            Assert.NotNull(result.MasterKey);
            Assert.Equal(expectedMasterHex, Convert.ToHexString(result.MasterKey).ToLowerInvariant());

            // Wrong passphrase returns WrongPassphrase status, does not throw
            var wrongResult = SyncCrypto.UnwrapMasterKey(wrongPassphrase, salt, iterations, nonce, wrappedKey);
            Assert.Equal(KeyUnwrapStatus.WrongPassphrase, wrongResult.Status);
            Assert.Null(wrongResult.MasterKey);
        }
    }

    // 2. fingerprint vectors: all 3 cases; and each key_wrap case's master gives that case's fingerprint.
    [Fact]
    public void Test02_Fingerprint_vectors_and_key_wrap_case_fingerprints()
    {
        var fpCases = TestVectors.Get("fingerprint").GetProperty("cases");
        foreach (var c in fpCases.EnumerateArray())
        {
            byte[] master = Convert.FromHexString(c.GetProperty("master_hex").GetString()!);
            string expectedFp = c.GetProperty("fingerprint").GetString()!;

            string fp = SyncCrypto.Fingerprint(master);
            Assert.Equal(expectedFp, fp);
        }

        var wrapCases = TestVectors.Get("key_wrap").GetProperty("cases");
        foreach (var c in wrapCases.EnumerateArray())
        {
            byte[] master = Convert.FromHexString(c.GetProperty("master_hex").GetString()!);
            string expectedFp = c.GetProperty("fingerprint").GetString()!;

            string fp = SyncCrypto.Fingerprint(master);
            Assert.Equal(expectedFp, fp);
        }
    }

    // 3. Malformed wrap: wrong salt/nonce/wrapped_key length, iterations 0, negative, and above MaxWrapIterations -> rejected before PBKDF2 runs.
    [Fact]
    public void Test03_Malformed_wrap_rejected_before_pbkdf2_runs()
    {
        byte[] validSalt = new byte[16];
        byte[] validNonce = new byte[12];
        byte[] validWrapped = new byte[48];

        // Salt lengths
        Assert.Equal(KeyUnwrapStatus.InvalidWrap, SyncCrypto.UnwrapMasterKey("pw", new byte[15], 1000, validNonce, validWrapped).Status);
        Assert.Equal(KeyUnwrapStatus.InvalidWrap, SyncCrypto.UnwrapMasterKey("pw", new byte[17], 1000, validNonce, validWrapped).Status);
        Assert.Equal(KeyUnwrapStatus.InvalidWrap, SyncCrypto.UnwrapMasterKey("pw", null!, 1000, validNonce, validWrapped).Status);

        // Nonce lengths
        Assert.Equal(KeyUnwrapStatus.InvalidWrap, SyncCrypto.UnwrapMasterKey("pw", validSalt, 1000, new byte[11], validWrapped).Status);
        Assert.Equal(KeyUnwrapStatus.InvalidWrap, SyncCrypto.UnwrapMasterKey("pw", validSalt, 1000, new byte[13], validWrapped).Status);
        Assert.Equal(KeyUnwrapStatus.InvalidWrap, SyncCrypto.UnwrapMasterKey("pw", validSalt, 1000, null!, validWrapped).Status);

        // Wrapped key lengths
        Assert.Equal(KeyUnwrapStatus.InvalidWrap, SyncCrypto.UnwrapMasterKey("pw", validSalt, 1000, validNonce, new byte[47]).Status);
        Assert.Equal(KeyUnwrapStatus.InvalidWrap, SyncCrypto.UnwrapMasterKey("pw", validSalt, 1000, validNonce, new byte[49]).Status);
        Assert.Equal(KeyUnwrapStatus.InvalidWrap, SyncCrypto.UnwrapMasterKey("pw", validSalt, 1000, validNonce, null!).Status);

        // Iterations <= 0
        Assert.Equal(KeyUnwrapStatus.InvalidWrap, SyncCrypto.UnwrapMasterKey("pw", validSalt, 0, validNonce, validWrapped).Status);
        Assert.Equal(KeyUnwrapStatus.InvalidWrap, SyncCrypto.UnwrapMasterKey("pw", validSalt, -100, validNonce, validWrapped).Status);

        // Iterations above MaxWrapIterations (default 10_000_000)
        var sw = Stopwatch.StartNew();
        var hugeResult = SyncCrypto.UnwrapMasterKey("pw", validSalt, 50_000_000, validNonce, validWrapped, maxIterations: 10_000_000);
        sw.Stop();

        Assert.Equal(KeyUnwrapStatus.InvalidWrap, hugeResult.Status);
        Assert.True(sw.ElapsedMilliseconds < 100, $"Unwrap took {sw.ElapsedMilliseconds}ms, proving PBKDF2 did not run.");
    }

    // 4. Full CLI flow against a fake server that answers with the server's real JSON shapes
    [Fact]
    public async Task Test04_Full_cli_flow_with_server_real_json_shapes()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"sync_test_cli_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        var statePath = Path.Combine(tempDir, "device-state.json");
        var keyPath = Path.Combine(tempDir, "master.key");

        DeviceCredentials.SaveDeviceState(statePath, new DeviceState("dev-1", "rt-initial"));

        var wrapCase = TestVectors.Get("key_wrap").GetProperty("cases")[0];
        string expectedMasterHex = wrapCase.GetProperty("master_hex").GetString()!;
        string expectedFp = wrapCase.GetProperty("fingerprint").GetString()!;

        var handler = new MockHttpMessageHandler((req, ct) =>
        {
            var path = req.RequestUri!.AbsolutePath;
            if (path.EndsWith("/api/v1/devices/refresh"))
            {
                var body = JsonSerializer.Serialize(new
                {
                    device_id = "dev-1",
                    refresh_token = "rt-rotated-1",
                    access_token = "at-token-1",
                    expires_at = DateTime.UtcNow.AddHours(1)
                });
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(body, Encoding.UTF8, "application/json")
                });
            }
            if (path == "/api/v1/keys/wraps")
            {
                var body = JsonSerializer.Serialize(new[]
                {
                    new
                    {
                        wrap_id = "wrap-1",
                        wrap_type = "passphrase",
                        label = "TDesktop Primary",
                        created_at = DateTime.UtcNow.AddDays(-1),
                        last_used_at = (DateTime?)null
                    }
                });
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(body, Encoding.UTF8, "application/json")
                });
            }
            if (path == "/api/v1/keys/wraps/wrap-1")
            {
                var body = JsonSerializer.Serialize(new
                {
                    wrap_id = "wrap-1",
                    wrap_type = "passphrase",
                    label = "TDesktop Primary",
                    salt = wrapCase.GetProperty("salt").GetString()!,
                    nonce = wrapCase.GetProperty("nonce").GetString()!,
                    wrapped_key = wrapCase.GetProperty("wrapped_key").GetString()!,
                    iterations = wrapCase.GetProperty("iterations").GetInt32()
                });
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(body, Encoding.UTF8, "application/json")
                });
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        });

        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Capture:Sync:ServerUrl"] = "http://localhost:5000",
            ["Capture:Sync:StatePath"] = statePath,
            ["Capture:Sync:MasterKeyPath"] = keyPath
        }).Build();

        var prompt = new MockConsolePrompt();
        prompt.Enqueue(wrapCase.GetProperty("passphrase").GetString()!); // passphrase
        prompt.Enqueue("y"); // confirm FP

        using var client = new HttpClient(handler);
        var output = new StringWriter();

        int exitCode = await SyncCliCommands.SetKeyAsync(config, prompt, client, output);

        Assert.Equal(0, exitCode);
        Assert.True(File.Exists(keyPath));
        var savedKey = DeviceCredentials.LoadMasterKey(keyPath);
        Assert.NotNull(savedKey);
        Assert.Equal(expectedMasterHex, Convert.ToHexString(savedKey).ToLowerInvariant());
        Assert.Equal(expectedFp, SyncCrypto.Fingerprint(savedKey));

        // Rotated refresh token must be persisted on disk
        var savedState = DeviceCredentials.LoadDeviceState(statePath);
        Assert.NotNull(savedState);
        Assert.Equal("rt-rotated-1", savedState.RefreshToken);
    }

    // 5. Wrong passphrase twice, right on the third try -> success; the wrap was fetched exactly once.
    [Fact]
    public async Task Test05_Wrong_passphrase_twice_right_on_third_try_fetches_wrap_once()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"sync_test_cli5_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        var statePath = Path.Combine(tempDir, "device-state.json");
        var keyPath = Path.Combine(tempDir, "master.key");

        DeviceCredentials.SaveDeviceState(statePath, new DeviceState("dev-1", "rt-1"));

        var wrapCase = TestVectors.Get("key_wrap").GetProperty("cases")[1]; // 1000 iterations for test speed
        int wrapFetchCount = 0;

        var handler = new MockHttpMessageHandler((req, ct) =>
        {
            var path = req.RequestUri!.AbsolutePath;
            if (path.EndsWith("/api/v1/devices/refresh"))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(JsonSerializer.Serialize(new
                    {
                        device_id = "dev-1",
                        refresh_token = "rt-rotated-2",
                        access_token = "at-token-2",
                        expires_at = DateTime.UtcNow.AddHours(1)
                    }), Encoding.UTF8, "application/json")
                });
            }
            if (path == "/api/v1/keys/wraps")
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(JsonSerializer.Serialize(new[]
                    {
                        new { wrap_id = "wrap-utf8", wrap_type = "passphrase", label = "UTF8", created_at = DateTime.UtcNow, last_used_at = (DateTime?)null }
                    }), Encoding.UTF8, "application/json")
                });
            }
            if (path == "/api/v1/keys/wraps/wrap-utf8")
            {
                Interlocked.Increment(ref wrapFetchCount);
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(JsonSerializer.Serialize(new
                    {
                        wrap_id = "wrap-utf8",
                        wrap_type = "passphrase",
                        label = "UTF8",
                        salt = wrapCase.GetProperty("salt").GetString()!,
                        nonce = wrapCase.GetProperty("nonce").GetString()!,
                        wrapped_key = wrapCase.GetProperty("wrapped_key").GetString()!,
                        iterations = wrapCase.GetProperty("iterations").GetInt32()
                    }), Encoding.UTF8, "application/json")
                });
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        });

        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Capture:Sync:ServerUrl"] = "http://localhost:5000",
            ["Capture:Sync:StatePath"] = statePath,
            ["Capture:Sync:MasterKeyPath"] = keyPath
        }).Build();

        var prompt = new MockConsolePrompt();
        prompt.Enqueue("wrong-one");
        prompt.Enqueue("wrong-two");
        prompt.Enqueue(wrapCase.GetProperty("passphrase").GetString()!); // 3rd try right
        prompt.Enqueue("y"); // confirm FP

        using var client = new HttpClient(handler);
        var output = new StringWriter();

        int exitCode = await SyncCliCommands.SetKeyAsync(config, prompt, client, output);

        Assert.Equal(0, exitCode);
        Assert.Equal(1, wrapFetchCount); // Fetched EXACTLY once
        Assert.True(File.Exists(keyPath));
    }

    // 6. Three wrong passphrases -> exit 1, no key file, wrap fetched once.
    [Fact]
    public async Task Test06_Three_wrong_passphrases_exits_1_no_key_file_fetches_wrap_once()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"sync_test_cli6_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        var statePath = Path.Combine(tempDir, "device-state.json");
        var keyPath = Path.Combine(tempDir, "master.key");

        DeviceCredentials.SaveDeviceState(statePath, new DeviceState("dev-1", "rt-1"));

        var wrapCase = TestVectors.Get("key_wrap").GetProperty("cases")[1];
        int wrapFetchCount = 0;

        var handler = new MockHttpMessageHandler((req, ct) =>
        {
            var path = req.RequestUri!.AbsolutePath;
            if (path.EndsWith("/api/v1/devices/refresh"))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(JsonSerializer.Serialize(new
                    {
                        device_id = "dev-1",
                        refresh_token = "rt-rotated-3",
                        access_token = "at-3",
                        expires_at = DateTime.UtcNow.AddHours(1)
                    }), Encoding.UTF8, "application/json")
                });
            }
            if (path == "/api/v1/keys/wraps")
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(JsonSerializer.Serialize(new[]
                    {
                        new { wrap_id = "wrap-6", wrap_type = "passphrase", label = "Test6", created_at = DateTime.UtcNow, last_used_at = (DateTime?)null }
                    }), Encoding.UTF8, "application/json")
                });
            }
            if (path == "/api/v1/keys/wraps/wrap-6")
            {
                Interlocked.Increment(ref wrapFetchCount);
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(JsonSerializer.Serialize(new
                    {
                        wrap_id = "wrap-6",
                        wrap_type = "passphrase",
                        label = "Test6",
                        salt = wrapCase.GetProperty("salt").GetString()!,
                        nonce = wrapCase.GetProperty("nonce").GetString()!,
                        wrapped_key = wrapCase.GetProperty("wrapped_key").GetString()!,
                        iterations = wrapCase.GetProperty("iterations").GetInt32()
                    }), Encoding.UTF8, "application/json")
                });
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        });

        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Capture:Sync:ServerUrl"] = "http://localhost:5000",
            ["Capture:Sync:StatePath"] = statePath,
            ["Capture:Sync:MasterKeyPath"] = keyPath
        }).Build();

        var prompt = new MockConsolePrompt();
        prompt.Enqueue("wrong-1");
        prompt.Enqueue("wrong-2");
        prompt.Enqueue("wrong-3");

        using var client = new HttpClient(handler);
        var output = new StringWriter();

        int exitCode = await SyncCliCommands.SetKeyAsync(config, prompt, client, output);

        Assert.Equal(1, exitCode);
        Assert.Equal(1, wrapFetchCount); // Fetched once
        Assert.False(File.Exists(keyPath)); // No key written
    }

    // 7. FP not confirmed -> no key file.
    [Fact]
    public async Task Test07_Fp_not_confirmed_exits_1_no_key_file()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"sync_test_cli7_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        var statePath = Path.Combine(tempDir, "device-state.json");
        var keyPath = Path.Combine(tempDir, "master.key");

        DeviceCredentials.SaveDeviceState(statePath, new DeviceState("dev-1", "rt-1"));

        var wrapCase = TestVectors.Get("key_wrap").GetProperty("cases")[1];

        var handler = new MockHttpMessageHandler((req, ct) =>
        {
            var path = req.RequestUri!.AbsolutePath;
            if (path.EndsWith("/api/v1/devices/refresh"))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(JsonSerializer.Serialize(new
                    {
                        device_id = "dev-1",
                        refresh_token = "rt-rotated-7",
                        access_token = "at-7",
                        expires_at = DateTime.UtcNow.AddHours(1)
                    }), Encoding.UTF8, "application/json")
                });
            }
            if (path == "/api/v1/keys/wraps")
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(JsonSerializer.Serialize(new[]
                    {
                        new { wrap_id = "wrap-7", wrap_type = "passphrase", label = "Test7", created_at = DateTime.UtcNow, last_used_at = (DateTime?)null }
                    }), Encoding.UTF8, "application/json")
                });
            }
            if (path == "/api/v1/keys/wraps/wrap-7")
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(JsonSerializer.Serialize(new
                    {
                        wrap_id = "wrap-7",
                        wrap_type = "passphrase",
                        label = "Test7",
                        salt = wrapCase.GetProperty("salt").GetString()!,
                        nonce = wrapCase.GetProperty("nonce").GetString()!,
                        wrapped_key = wrapCase.GetProperty("wrapped_key").GetString()!,
                        iterations = wrapCase.GetProperty("iterations").GetInt32()
                    }), Encoding.UTF8, "application/json")
                });
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        });

        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Capture:Sync:ServerUrl"] = "http://localhost:5000",
            ["Capture:Sync:StatePath"] = statePath,
            ["Capture:Sync:MasterKeyPath"] = keyPath
        }).Build();

        var prompt = new MockConsolePrompt();
        prompt.Enqueue(wrapCase.GetProperty("passphrase").GetString()!); // Correct passphrase
        prompt.Enqueue("no"); // Rejected FP

        using var client = new HttpClient(handler);
        var output = new StringWriter();

        int exitCode = await SyncCliCommands.SetKeyAsync(config, prompt, client, output);

        Assert.Equal(1, exitCode);
        Assert.False(File.Exists(keyPath)); // No key written
    }

    // 8. No passphrase wrap -> exit 1 with the tdesktop hint; several wraps -> the chosen one is fetched.
    [Fact]
    public async Task Test08_No_passphrase_wrap_exits_1_and_multiple_wraps_selects_chosen()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"sync_test_cli8_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        var statePath = Path.Combine(tempDir, "device-state.json");
        var keyPath = Path.Combine(tempDir, "master.key");

        DeviceCredentials.SaveDeviceState(statePath, new DeviceState("dev-1", "rt-1"));

        // Part A: No passphrase wraps
        var handlerEmpty = new MockHttpMessageHandler((req, ct) =>
        {
            var path = req.RequestUri!.AbsolutePath;
            if (path.EndsWith("/api/v1/devices/refresh"))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(JsonSerializer.Serialize(new
                    {
                        device_id = "dev-1",
                        refresh_token = "rt-r8",
                        access_token = "at-8",
                        expires_at = DateTime.UtcNow.AddHours(1)
                    }), Encoding.UTF8, "application/json")
                });
            }
            if (path == "/api/v1/keys/wraps")
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(JsonSerializer.Serialize(new[]
                    {
                        new { wrap_id = "wrap-other", wrap_type = "recovery_code", label = "RC", created_at = DateTime.UtcNow, last_used_at = (DateTime?)null }
                    }), Encoding.UTF8, "application/json")
                });
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        });

        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Capture:Sync:ServerUrl"] = "http://localhost:5000",
            ["Capture:Sync:StatePath"] = statePath,
            ["Capture:Sync:MasterKeyPath"] = keyPath
        }).Build();

        var promptEmpty = new MockConsolePrompt();
        using (var clientEmpty = new HttpClient(handlerEmpty))
        {
            var outputEmpty = new StringWriter();
            int exitCodeEmpty = await SyncCliCommands.SetKeyAsync(config, promptEmpty, clientEmpty, outputEmpty);
            Assert.Equal(1, exitCodeEmpty);
            var text = outputEmpty.ToString();
            Assert.Contains("tdesktop", text, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("archive password", text, StringComparison.OrdinalIgnoreCase);
        }

        // Part B: Several wraps -> select chosen one
        var wrapCase = TestVectors.Get("key_wrap").GetProperty("cases")[1];
        var fetchedIds = new List<string>();

        var handlerMultiple = new MockHttpMessageHandler((req, ct) =>
        {
            var path = req.RequestUri!.AbsolutePath;
            if (path.EndsWith("/api/v1/devices/refresh"))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(JsonSerializer.Serialize(new
                    {
                        device_id = "dev-1",
                        refresh_token = "rt-r8b",
                        access_token = "at-8b",
                        expires_at = DateTime.UtcNow.AddHours(1)
                    }), Encoding.UTF8, "application/json")
                });
            }
            if (path == "/api/v1/keys/wraps")
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(JsonSerializer.Serialize(new[]
                    {
                        new { wrap_id = "wrap-A", wrap_type = "passphrase", label = "Laptop Wrap", created_at = DateTime.UtcNow.AddDays(-2), last_used_at = (DateTime?)null },
                        new { wrap_id = "wrap-B", wrap_type = "passphrase", label = "Desktop Wrap", created_at = DateTime.UtcNow.AddDays(-1), last_used_at = (DateTime?)null }
                    }), Encoding.UTF8, "application/json")
                });
            }
            if (path.StartsWith("/api/v1/keys/wraps/"))
            {
                var id = path.Substring("/api/v1/keys/wraps/".Length);
                fetchedIds.Add(id);
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(JsonSerializer.Serialize(new
                    {
                        wrap_id = id,
                        wrap_type = "passphrase",
                        label = "Selected",
                        salt = wrapCase.GetProperty("salt").GetString()!,
                        nonce = wrapCase.GetProperty("nonce").GetString()!,
                        wrapped_key = wrapCase.GetProperty("wrapped_key").GetString()!,
                        iterations = wrapCase.GetProperty("iterations").GetInt32()
                    }), Encoding.UTF8, "application/json")
                });
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        });

        var promptMultiple = new MockConsolePrompt();
        promptMultiple.Enqueue("2"); // Select 2nd wrap ("wrap-B")
        promptMultiple.Enqueue(wrapCase.GetProperty("passphrase").GetString()!);
        promptMultiple.Enqueue("y");

        using (var clientMultiple = new HttpClient(handlerMultiple))
        {
            var outputMultiple = new StringWriter();
            int exitCodeMultiple = await SyncCliCommands.SetKeyAsync(config, promptMultiple, clientMultiple, outputMultiple);
            Assert.Equal(0, exitCodeMultiple);
            Assert.Single(fetchedIds);
            Assert.Equal("wrap-B", fetchedIds[0]);
        }
    }

    // 9. 429 on the wrap fetch -> exit 1, the rate-limit message.
    [Fact]
    public async Task Test09_Rate_limit_429_on_wrap_fetch_exits_1_with_message()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"sync_test_cli9_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        var statePath = Path.Combine(tempDir, "device-state.json");
        var keyPath = Path.Combine(tempDir, "master.key");

        DeviceCredentials.SaveDeviceState(statePath, new DeviceState("dev-1", "rt-1"));

        var handler = new MockHttpMessageHandler((req, ct) =>
        {
            var path = req.RequestUri!.AbsolutePath;
            if (path.EndsWith("/api/v1/devices/refresh"))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(JsonSerializer.Serialize(new
                    {
                        device_id = "dev-1",
                        refresh_token = "rt-r9",
                        access_token = "at-9",
                        expires_at = DateTime.UtcNow.AddHours(1)
                    }), Encoding.UTF8, "application/json")
                });
            }
            if (path == "/api/v1/keys/wraps")
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(JsonSerializer.Serialize(new[]
                    {
                        new { wrap_id = "wrap-9", wrap_type = "passphrase", label = "Test9", created_at = DateTime.UtcNow, last_used_at = (DateTime?)null }
                    }), Encoding.UTF8, "application/json")
                });
            }
            if (path == "/api/v1/keys/wraps/wrap-9")
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.TooManyRequests)
                {
                    Content = new StringContent("{\"error\":\"rate_limit_exceeded\"}", Encoding.UTF8, "application/json")
                });
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        });

        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Capture:Sync:ServerUrl"] = "http://localhost:5000",
            ["Capture:Sync:StatePath"] = statePath,
            ["Capture:Sync:MasterKeyPath"] = keyPath
        }).Build();

        var prompt = new MockConsolePrompt();
        using var client = new HttpClient(handler);
        var output = new StringWriter();

        int exitCode = await SyncCliCommands.SetKeyAsync(config, prompt, client, output);

        Assert.Equal(1, exitCode);
        Assert.False(File.Exists(keyPath));
        var text = output.ToString();
        Assert.Contains("limit", text, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(prompt.PromptsAsked); // Did not ask for passphrase!
    }

    // 10. Not enrolled -> exit 1, no HTTP.
    [Fact]
    public async Task Test10_Not_enrolled_exits_1_no_http()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"sync_test_cli10_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        var statePath = Path.Combine(tempDir, "non_existent_state.json");
        var keyPath = Path.Combine(tempDir, "master.key");

        bool httpAttempted = false;
        var handler = new MockHttpMessageHandler((req, ct) =>
        {
            httpAttempted = true;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        });

        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Capture:Sync:ServerUrl"] = "http://localhost:5000",
            ["Capture:Sync:StatePath"] = statePath,
            ["Capture:Sync:MasterKeyPath"] = keyPath
        }).Build();

        var prompt = new MockConsolePrompt();
        using var client = new HttpClient(handler);
        var output = new StringWriter();

        int exitCode = await SyncCliCommands.SetKeyAsync(config, prompt, client, output);

        Assert.Equal(1, exitCode);
        Assert.False(httpAttempted, "No HTTP call should be made when device is not enrolled.");
        Assert.Contains("enroll", output.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    // 11. Existing key file: same FP -> not rewritten; different FP -> replaced only after the second confirmation.
    [Fact]
    public async Task Test11_Existing_key_file_same_fp_not_rewritten_different_fp_confirms_replacement()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"sync_test_cli11_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        var statePath = Path.Combine(tempDir, "device-state.json");
        var keyPath = Path.Combine(tempDir, "master.key");

        DeviceCredentials.SaveDeviceState(statePath, new DeviceState("dev-1", "rt-1"));

        var wrapCase = TestVectors.Get("key_wrap").GetProperty("cases")[1];
        byte[] expectedMaster = Convert.FromHexString(wrapCase.GetProperty("master_hex").GetString()!);

        var handler = new MockHttpMessageHandler((req, ct) =>
        {
            var path = req.RequestUri!.AbsolutePath;
            if (path.EndsWith("/api/v1/devices/refresh"))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(JsonSerializer.Serialize(new
                    {
                        device_id = "dev-1",
                        refresh_token = "rt-r11",
                        access_token = "at-11",
                        expires_at = DateTime.UtcNow.AddHours(1)
                    }), Encoding.UTF8, "application/json")
                });
            }
            if (path == "/api/v1/keys/wraps")
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(JsonSerializer.Serialize(new[]
                    {
                        new { wrap_id = "wrap-11", wrap_type = "passphrase", label = "Test11", created_at = DateTime.UtcNow, last_used_at = (DateTime?)null }
                    }), Encoding.UTF8, "application/json")
                });
            }
            if (path == "/api/v1/keys/wraps/wrap-11")
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(JsonSerializer.Serialize(new
                    {
                        wrap_id = "wrap-11",
                        wrap_type = "passphrase",
                        label = "Test11",
                        salt = wrapCase.GetProperty("salt").GetString()!,
                        nonce = wrapCase.GetProperty("nonce").GetString()!,
                        wrapped_key = wrapCase.GetProperty("wrapped_key").GetString()!,
                        iterations = wrapCase.GetProperty("iterations").GetInt32()
                    }), Encoding.UTF8, "application/json")
                });
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        });

        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Capture:Sync:ServerUrl"] = "http://localhost:5000",
            ["Capture:Sync:StatePath"] = statePath,
            ["Capture:Sync:MasterKeyPath"] = keyPath
        }).Build();

        // Part A: Key file exists with SAME FP
        DeviceCredentials.SaveMasterKey(keyPath, expectedMaster);
        var writeTimeBefore = File.GetLastWriteTimeUtc(keyPath);

        var promptSame = new MockConsolePrompt();
        promptSame.Enqueue(wrapCase.GetProperty("passphrase").GetString()!);
        promptSame.Enqueue("y");

        using (var client = new HttpClient(handler))
        {
            var output = new StringWriter();
            int exitCode = await SyncCliCommands.SetKeyAsync(config, promptSame, client, output);
            Assert.Equal(0, exitCode);
            Assert.Contains("same fingerprint", output.ToString(), StringComparison.OrdinalIgnoreCase);
        }

        // Part B: Key file exists with DIFFERENT FP
        byte[] differentKey = new byte[32]; // 32 zeros
        DeviceCredentials.SaveMasterKey(keyPath, differentKey);

        // B.1: User says 'n' to replacement
        var promptDiffReject = new MockConsolePrompt();
        promptDiffReject.Enqueue(wrapCase.GetProperty("passphrase").GetString()!);
        promptDiffReject.Enqueue("y"); // Confirm FP matches tdesktop
        promptDiffReject.Enqueue("n"); // Reject replacing existing key

        using (var client = new HttpClient(handler))
        {
            var output = new StringWriter();
            int exitCode = await SyncCliCommands.SetKeyAsync(config, promptDiffReject, client, output);
            Assert.Equal(1, exitCode);
            var loadedKey = DeviceCredentials.LoadMasterKey(keyPath);
            Assert.Equal(differentKey, loadedKey); // Not replaced
        }

        // B.2: User says 'y' to replacement
        var promptDiffAccept = new MockConsolePrompt();
        promptDiffAccept.Enqueue(wrapCase.GetProperty("passphrase").GetString()!);
        promptDiffAccept.Enqueue("y"); // Confirm FP matches tdesktop
        promptDiffAccept.Enqueue("y"); // Accept replacing existing key

        using (var client = new HttpClient(handler))
        {
            var output = new StringWriter();
            int exitCode = await SyncCliCommands.SetKeyAsync(config, promptDiffAccept, client, output);
            Assert.Equal(0, exitCode);
            var loadedKey = DeviceCredentials.LoadMasterKey(keyPath);
            Assert.Equal(expectedMaster, loadedKey); // Replaced successfully
        }
    }

    // 12. Runner: refresh 401 + a newer token in the state file -> retries and pushes; 401 with the current token -> stops.
    [Fact]
    public async Task Test12_Runner_refresh_401_retries_with_new_state_file_and_stops_on_current_token()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"sync_test_runner12_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        var statePath = Path.Combine(tempDir, "device-state.json");
        var keyPath = Path.Combine(tempDir, "master.key");
        var dbPath = Path.Combine(tempDir, "capture.db");

        DeviceCredentials.SaveDeviceState(statePath, new DeviceState("dev-1", "rt-old"));
        DeviceCredentials.SaveMasterKey(keyPath, new byte[32]);

        var cache = new MessageCache(dbPath);
        cache.Initialize();
        EnqueueOutbox(dbPath, "deleted", "acc-1", "peer-1", 100, 100, 100, "{\"account_id\":\"acc-1\",\"peer_id\":\"peer-1\"}");

        // Scenario 1: State file has newer token -> re-reads, retries refresh, succeeds and pushes
        int refreshCalls = 0;
        bool pushCalled = false;
        var handlerRetry = new MockHttpMessageHandler(async (req, ct) =>
        {
            var path = req.RequestUri!.AbsolutePath;
            if (path.EndsWith("/api/v1/devices/refresh"))
            {
                refreshCalls++;
                var content = await req.Content!.ReadAsStringAsync(ct);
                using var doc = JsonDocument.Parse(content);
                string token = doc.RootElement.GetProperty("refresh_token").GetString()!;

                if (token == "rt-old")
                {
                    // Simulated state rotation by --set-key happening in background
                    DeviceCredentials.SaveDeviceState(statePath, new DeviceState("dev-1", "rt-newer"));
                    return new HttpResponseMessage(HttpStatusCode.Unauthorized);
                }

                if (token == "rt-newer")
                {
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent(JsonSerializer.Serialize(new
                        {
                            device_id = "dev-1",
                            refresh_token = "rt-rotated-new",
                            access_token = "at-valid",
                            expires_at = DateTime.UtcNow.AddHours(1)
                        }), Encoding.UTF8, "application/json")
                    };
                }
            }
            if (path.EndsWith("/api/v1/sync/push"))
            {
                pushCalled = true;
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{\"results\":[{\"record_id\":\"irrelevant\",\"status\":\"created\"}]}", Encoding.UTF8, "application/json")
                };
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Capture:Sync:Enabled"] = "true",
            ["Capture:Sync:ServerUrl"] = "http://localhost:5000",
            ["Capture:Sync:StatePath"] = statePath,
            ["Capture:Sync:MasterKeyPath"] = keyPath
        }).Build();

        using var clientRetry = new HttpClient(handlerRetry);
        var syncHttpClient = new CaptureSyncHttpClient(clientRetry);
        var runner = new CaptureSyncRunner(cache, syncHttpClient, config);

        var pushResult = await runner.PushCycleAsync();
        Assert.True(pushResult);
        Assert.True(pushCalled);
        Assert.False(runner.IsStopped);
        Assert.Equal(2, refreshCalls);

        // Scenario 2: State file still has current token -> 401 stops sync
        DeviceCredentials.SaveDeviceState(statePath, new DeviceState("dev-1", "rt-same"));
        EnqueueOutbox(dbPath, "deleted", "acc-2", "peer-2", 200, 200, 200, "{\"account_id\":\"acc-2\",\"peer_id\":\"peer-2\"}");
        var handlerStop = new MockHttpMessageHandler((req, ct) =>
        {
            if (req.RequestUri!.AbsolutePath.EndsWith("/api/v1/devices/refresh"))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized));
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        });

        using var clientStop = new HttpClient(handlerStop);
        var syncHttpClientStop = new CaptureSyncHttpClient(clientStop);
        var runnerStop = new CaptureSyncRunner(cache, syncHttpClientStop, config);

        var pushResultStop = await runnerStop.PushCycleAsync();
        Assert.False(pushResultStop);
        Assert.True(runnerStop.IsStopped);
    }

    // 13. The refresh logic exists once: runner and CLI both go through it (a test that fails if either path skips persisting the rotated token).
    [Fact]
    public async Task Test13_Refresh_logic_exists_once_persisting_rotated_token_in_both_paths()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"sync_test_shared_refresh_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        var statePathCli = Path.Combine(tempDir, "state_cli.json");
        var statePathRunner = Path.Combine(tempDir, "state_runner.json");
        var keyPathCli = Path.Combine(tempDir, "master_cli.key");
        var keyPathRunner = Path.Combine(tempDir, "master_runner.key");
        var dbPath = Path.Combine(tempDir, "capture.db");

        DeviceCredentials.SaveDeviceState(statePathCli, new DeviceState("dev-cli", "rt-cli-old"));
        DeviceCredentials.SaveDeviceState(statePathRunner, new DeviceState("dev-runner", "rt-runner-old"));
        DeviceCredentials.SaveMasterKey(keyPathRunner, new byte[32]);

        var wrapCase = TestVectors.Get("key_wrap").GetProperty("cases")[1];

        var handler = new MockHttpMessageHandler(async (req, ct) =>
        {
            var path = req.RequestUri!.AbsolutePath;
            if (path.EndsWith("/api/v1/devices/refresh"))
            {
                var content = await req.Content!.ReadAsStringAsync(ct);
                using var doc = JsonDocument.Parse(content);
                string token = doc.RootElement.GetProperty("refresh_token").GetString()!;
                string deviceId = doc.RootElement.GetProperty("device_id").GetString()!;

                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(JsonSerializer.Serialize(new
                    {
                        device_id = deviceId,
                        refresh_token = $"rotated_{token}",
                        access_token = $"at_{token}",
                        expires_at = DateTime.UtcNow.AddHours(1)
                    }), Encoding.UTF8, "application/json")
                };
            }
            if (path == "/api/v1/keys/wraps")
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(JsonSerializer.Serialize(new[]
                    {
                        new { wrap_id = "wrap-13", wrap_type = "passphrase", label = "Test13", created_at = DateTime.UtcNow, last_used_at = (DateTime?)null }
                    }), Encoding.UTF8, "application/json")
                };
            }
            if (path == "/api/v1/keys/wraps/wrap-13")
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(JsonSerializer.Serialize(new
                    {
                        wrap_id = "wrap-13",
                        wrap_type = "passphrase",
                        label = "Test13",
                        salt = wrapCase.GetProperty("salt").GetString()!,
                        nonce = wrapCase.GetProperty("nonce").GetString()!,
                        wrapped_key = wrapCase.GetProperty("wrapped_key").GetString()!,
                        iterations = wrapCase.GetProperty("iterations").GetInt32()
                    }), Encoding.UTF8, "application/json")
                };
            }
            if (path.EndsWith("/api/v1/sync/push"))
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{\"results\":[{\"record_id\":\"irrelevant\",\"status\":\"created\"}]}", Encoding.UTF8, "application/json")
                };
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        // 1. Verify CLI path persists rotated token to disk
        var configCli = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Capture:Sync:ServerUrl"] = "http://localhost:5000",
            ["Capture:Sync:StatePath"] = statePathCli,
            ["Capture:Sync:MasterKeyPath"] = keyPathCli
        }).Build();

        var prompt = new MockConsolePrompt();
        prompt.Enqueue(wrapCase.GetProperty("passphrase").GetString()!);
        prompt.Enqueue("y");

        using var client = new HttpClient(handler);
        var output = new StringWriter();
        int cliExit = await SyncCliCommands.SetKeyAsync(configCli, prompt, client, output);
        Assert.Equal(0, cliExit);

        var stateAfterCli = DeviceCredentials.LoadDeviceState(statePathCli);
        Assert.NotNull(stateAfterCli);
        Assert.Equal("rotated_rt-cli-old", stateAfterCli.RefreshToken);

        // 2. Verify Runner path persists rotated token to disk
        var cache = new MessageCache(dbPath);
        cache.Initialize();
        EnqueueOutbox(dbPath, "deleted", "acc-1", "peer-1", 100, 100, 100, "{\"account_id\":\"acc-1\",\"peer_id\":\"peer-1\"}");

        var configRunner = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Capture:Sync:Enabled"] = "true",
            ["Capture:Sync:ServerUrl"] = "http://localhost:5000",
            ["Capture:Sync:StatePath"] = statePathRunner,
            ["Capture:Sync:MasterKeyPath"] = keyPathRunner
        }).Build();

        var syncHttpClient = new CaptureSyncHttpClient(client);
        var runner = new CaptureSyncRunner(cache, syncHttpClient, configRunner);
        bool pushOk = await runner.PushCycleAsync();
        Assert.True(pushOk);

        var stateAfterRunner = DeviceCredentials.LoadDeviceState(statePathRunner);
        Assert.NotNull(stateAfterRunner);
        Assert.Equal("rotated_rt-runner-old", stateAfterRunner.RefreshToken);
    }

    // 14. Output and logs of every test above contain no passphrase, KEK, master hex, wrapped_key or token.
    [Fact]
    public async Task Test14_Output_and_logs_contain_no_passphrase_kek_master_hex_wrapped_key_or_token()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"sync_test_privacy_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        var statePath = Path.Combine(tempDir, "device-state.json");
        var keyPath = Path.Combine(tempDir, "master.key");

        string secretRefreshToken = "secret-refresh-token-xyz-123456";
        string secretAccessToken = "secret-access-token-abc-789012";
        DeviceCredentials.SaveDeviceState(statePath, new DeviceState("dev-privacy", secretRefreshToken));

        var wrapCase = TestVectors.Get("key_wrap").GetProperty("cases")[1];
        string secretPassphrase = wrapCase.GetProperty("passphrase").GetString()!;
        string secretMasterHex = wrapCase.GetProperty("master_hex").GetString()!;
        string secretWrappedKey = wrapCase.GetProperty("wrapped_key").GetString()!;

        var handler = new MockHttpMessageHandler((req, ct) =>
        {
            var path = req.RequestUri!.AbsolutePath;
            if (path.EndsWith("/api/v1/devices/refresh"))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(JsonSerializer.Serialize(new
                    {
                        device_id = "dev-privacy",
                        refresh_token = "secret-rotated-rt-888",
                        access_token = secretAccessToken,
                        expires_at = DateTime.UtcNow.AddHours(1)
                    }), Encoding.UTF8, "application/json")
                });
            }
            if (path == "/api/v1/keys/wraps")
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(JsonSerializer.Serialize(new[]
                    {
                        new { wrap_id = "wrap-priv", wrap_type = "passphrase", label = "Priv", created_at = DateTime.UtcNow, last_used_at = (DateTime?)null }
                    }), Encoding.UTF8, "application/json")
                });
            }
            if (path == "/api/v1/keys/wraps/wrap-priv")
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(JsonSerializer.Serialize(new
                    {
                        wrap_id = "wrap-priv",
                        wrap_type = "passphrase",
                        label = "Priv",
                        salt = wrapCase.GetProperty("salt").GetString()!,
                        nonce = wrapCase.GetProperty("nonce").GetString()!,
                        wrapped_key = secretWrappedKey,
                        iterations = wrapCase.GetProperty("iterations").GetInt32()
                    }), Encoding.UTF8, "application/json")
                });
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        });

        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Capture:Sync:ServerUrl"] = "http://localhost:5000",
            ["Capture:Sync:StatePath"] = statePath,
            ["Capture:Sync:MasterKeyPath"] = keyPath
        }).Build();

        var prompt = new MockConsolePrompt();
        prompt.Enqueue(secretPassphrase);
        prompt.Enqueue("y");

        var loggerProvider = new TestLoggerProvider();
        using var client = new HttpClient(handler);
        var output = new StringWriter();

        int exitCode = await SyncCliCommands.SetKeyAsync(config, prompt, client, output);
        Assert.Equal(0, exitCode);

        string consoleOutput = output.ToString();
        var allLogs = string.Join("\n", loggerProvider.Messages);

        // Verify none of the secrets are logged or printed (case-insensitive)
        Assert.DoesNotContain(secretPassphrase, consoleOutput, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(secretPassphrase, allLogs, StringComparison.OrdinalIgnoreCase);

        Assert.DoesNotContain(secretMasterHex, consoleOutput, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(secretMasterHex, allLogs, StringComparison.OrdinalIgnoreCase);

        Assert.DoesNotContain(secretWrappedKey, consoleOutput, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(secretWrappedKey, allLogs, StringComparison.OrdinalIgnoreCase);

        Assert.DoesNotContain(secretAccessToken, consoleOutput, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(secretAccessToken, allLogs, StringComparison.OrdinalIgnoreCase);

        Assert.DoesNotContain(secretRefreshToken, consoleOutput, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(secretRefreshToken, allLogs, StringComparison.OrdinalIgnoreCase);

        Assert.DoesNotContain("secret-rotated-rt-888", consoleOutput, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("secret-rotated-rt-888", allLogs, StringComparison.OrdinalIgnoreCase);
    }
}
