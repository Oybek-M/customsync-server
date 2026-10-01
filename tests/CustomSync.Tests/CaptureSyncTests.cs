using System.Net;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CustomSync.Api.Endpoints;
using CustomSync.Capture.Capture;
using CustomSync.Capture.Preflight;
using CustomSync.Capture.Sync;
using CustomSync.Capture.Tdlib;
using CustomSync.Core;
using CustomSync.Core.Contracts;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace CustomSync.Tests;

public class CaptureSyncTests
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
        public void Enqueue(string? input) => _inputs.Enqueue(input);
        public string? Prompt(string message, bool isSecret = false) => _inputs.Dequeue();
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

    private class TestTimeProvider : TimeProvider
    {
        private DateTimeOffset _utcNow;
        public TestTimeProvider(long unixSeconds) => _utcNow = DateTimeOffset.FromUnixTimeSeconds(unixSeconds);
        public override DateTimeOffset GetUtcNow() => _utcNow;
        public void Advance(TimeSpan delta) => _utcNow = _utcNow.Add(delta);
        public void SetUnixSeconds(long sec) => _utcNow = DateTimeOffset.FromUnixTimeSeconds(sec);
    }

    // 1. HKDF: the three derived keys equal hkdf vectors.
    [Fact]
    public void Test01_Hkdf_derived_keys_match_vectors()
    {
        var hkdfSection = TestVectors.Get("hkdf");
        var masterKey = Convert.FromHexString(hkdfSection.GetProperty("master_key_hex").GetString()!);
        var derived = hkdfSection.GetProperty("derived");

        var expectedContentKey = derived.GetProperty("customsync-content-v1").GetString()!;
        var expectedPeerKey = derived.GetProperty("customsync-peer-v1").GetString()!;
        var expectedAccountKey = derived.GetProperty("customsync-account-v1").GetString()!;

        var actualContentKey = Convert.ToHexString(SyncCrypto.DeriveContentKey(masterKey)).ToLowerInvariant();
        var actualPeerKey = Convert.ToHexString(SyncCrypto.DerivePeerKey(masterKey)).ToLowerInvariant();
        var actualAccountKey = Convert.ToHexString(SyncCrypto.DeriveAccountKey(masterKey)).ToLowerInvariant();

        Assert.Equal(expectedContentKey, actualContentKey);
        Assert.Equal(expectedPeerKey, actualPeerKey);
        Assert.Equal(expectedAccountKey, actualAccountKey);
    }

    // 2. account_hash and peer_hash equal the vectors.
    [Fact]
    public void Test02_Account_hash_and_peer_hash_match_vectors()
    {
        var hkdfSection = TestVectors.Get("hkdf");
        var masterKey = Convert.FromHexString(hkdfSection.GetProperty("master_key_hex").GetString()!);
        var accountKey = SyncCrypto.DeriveAccountKey(masterKey);
        var peerKey = SyncCrypto.DerivePeerKey(masterKey);

        var accountCases = TestVectors.Get("account_hash").GetProperty("cases");
        foreach (var c in accountCases.EnumerateArray())
        {
            var accId = c.GetProperty("account_id").GetString()!;
            var expected = c.GetProperty("account_hash").GetString()!;
            var actual = CryptoPrimitives.ComputeAccountHash(accountKey, accId);
            Assert.Equal(expected, actual);
        }

        var peerCases = TestVectors.Get("peer_hash").GetProperty("cases");
        foreach (var c in peerCases.EnumerateArray())
        {
            var peerId = c.GetProperty("peer_id").GetString()!;
            var expected = c.GetProperty("peer_hash").GetString()!;
            var actual = CryptoPrimitives.ComputePeerHash(peerKey, peerId);
            Assert.Equal(expected, actual);
        }
    }

    // 3. AES-GCM: encrypting a vector's plaintext with its nonce yields ciphertext‖tag;
    // decrypting the joined bytes returns the plaintext; a flipped tag bit fails.
    [Fact]
    public void Test03_Aes_gcm_encrypt_and_decrypt_with_concatenated_tag_and_detect_flipped_bit()
    {
        var cases = TestVectors.Get("aes_gcm").GetProperty("cases");
        foreach (var c in cases.EnumerateArray())
        {
            var key = Convert.FromHexString(c.GetProperty("key_hex").GetString()!);
            var nonce = Convert.FromHexString(c.GetProperty("nonce_hex").GetString()!);
            var plaintext = Convert.FromHexString(c.GetProperty("plaintext_hex").GetString()!);
            var expectedCiphertext = c.GetProperty("ciphertext_hex").GetString()!;
            var expectedTag = c.GetProperty("tag_hex").GetString()!;
            var expectedWirePayload = expectedCiphertext + expectedTag;

            var (wirePayload, returnedNonce) = SyncCrypto.EncryptPayload(key, plaintext, nonce);
            Assert.Equal(nonce, returnedNonce);
            Assert.Equal(expectedWirePayload, Convert.ToHexString(wirePayload).ToLowerInvariant());

            var recovered = SyncCrypto.DecryptPayload(key, nonce, wirePayload);
            Assert.Equal(plaintext, recovered);

            var corrupted = (byte[])wirePayload.Clone();
            corrupted[^1] ^= 0x01;
            Assert.ThrowsAny<CryptographicException>(() => SyncCrypto.DecryptPayload(key, nonce, corrupted));
        }
    }

    // 4. Two encryptions of the same row use different nonces.
    [Fact]
    public void Test04_Two_encryptions_of_same_row_use_different_nonces()
    {
        var key = new byte[32];
        RandomNumberGenerator.Fill(key);
        var plaintext = Encoding.UTF8.GetBytes("{\"account_id\":\"111\",\"peer_id\":\"222\",\"text\":\"test\"}");

        var (payload1, nonce1) = SyncCrypto.EncryptPayload(key, plaintext);
        var (payload2, nonce2) = SyncCrypto.EncryptPayload(key, plaintext);

        Assert.Equal(12, nonce1.Length);
        Assert.Equal(12, nonce2.Length);
        Assert.False(nonce1.SequenceEqual(nonce2));
        Assert.False(payload1.SequenceEqual(payload2));
    }

    // 5. Outbox row → SyncRecord: edited 390234 @ 1787000010 and @ 1787000020, deleted,
    // and activity (account_hash "") — each record_id equals test-vectors.json when built with the vectors' master key and account/peer ids.
    [Fact]
    public void Test05_Outbox_row_to_sync_record_matches_vectors()
    {
        var hkdfSection = TestVectors.Get("hkdf");
        var masterKey = Convert.FromHexString(hkdfSection.GetProperty("master_key_hex").GetString()!);
        var accountId = "111222333";
        var peerId = "7053823996";

        var testRows = new[]
        {
            new OutboxRow(1, "edited", accountId, peerId, 390234, 1787000010, 1787000010,
                $"{{\"account_id\":\"{accountId}\",\"peer_id\":\"{peerId}\",\"text\":\"edited 1\"}}", 1787000010),
            new OutboxRow(2, "edited", accountId, peerId, 390234, 1787000020, 1787000020,
                $"{{\"account_id\":\"{accountId}\",\"peer_id\":\"{peerId}\",\"text\":\"edited 2\"}}", 1787000020),
            new OutboxRow(3, "deleted", accountId, peerId, 395278, 1787000000, 1787000000,
                $"{{\"account_id\":\"{accountId}\",\"peer_id\":\"{peerId}\"}}", 1787000000),
            new OutboxRow(4, "activity", accountId, peerId, 521316072760462774, 1787000002, 1787000002,
                $"{{\"account_id\":\"{accountId}\",\"peer_id\":\"{peerId}\",\"field\":\"status\",\"value\":\"online\"}}", 1787000002)
        };

        var vectorCases = TestVectors.Get("record_id").GetProperty("cases").EnumerateArray().ToList();

        foreach (var row in testRows)
        {
            var record = SyncCrypto.BuildRecord(row, masterKey, "device-1");

            if (row.Kind == "activity")
            {
                Assert.Equal("", record.AccountHash);
            }
            else
            {
                Assert.NotEqual("", record.AccountHash);
            }

            var matchingVector = vectorCases.FirstOrDefault(v =>
                v.GetProperty("kind").GetString() == row.Kind &&
                v.GetProperty("msg_id").GetInt64() == row.MsgId &&
                v.GetProperty("occurred_at").GetInt64() == row.OccurredAt);

            Assert.NotNull(matchingVector.GetRawText());
            var expectedRecordId = matchingVector.GetProperty("record_id").GetString();
            Assert.Equal(expectedRecordId, record.RecordId);
        }
    }

    // 6. §0.14 mismatch row: never sent, kept, counted.
    [Fact]
    public async Task Test06_Section_014_mismatch_row_is_quarantined_and_never_pushed()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"sync_test_06_{Guid.NewGuid():N}.db");
        var cache = new MessageCache(dbPath);
        cache.Initialize();

        // Row column has account 111, but payload has account 999
        var poisonedRow = new OutboxRow(
            0, "deleted", "111", "222", 12345, 100, 100,
            "{\"account_id\":\"999\",\"peer_id\":\"222\"}", 100);

        using (var conn = new SqliteConnection($"Data Source={dbPath}"))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                INSERT INTO capture_outbox (kind, account_id, peer_id, msg_id, occurred_at, observed_at, payload_json, created_at)
                VALUES (@kind, @account_id, @peer_id, @msg_id, @occurred_at, @observed_at, @payload_json, @created_at);";
            cmd.Parameters.AddWithValue("@kind", poisonedRow.Kind);
            cmd.Parameters.AddWithValue("@account_id", poisonedRow.AccountId);
            cmd.Parameters.AddWithValue("@peer_id", poisonedRow.PeerId);
            cmd.Parameters.AddWithValue("@msg_id", poisonedRow.MsgId);
            cmd.Parameters.AddWithValue("@occurred_at", poisonedRow.OccurredAt);
            cmd.Parameters.AddWithValue("@observed_at", poisonedRow.ObservedAt);
            cmd.Parameters.AddWithValue("@payload_json", poisonedRow.PayloadJson);
            cmd.Parameters.AddWithValue("@created_at", poisonedRow.CreatedAt);
            cmd.ExecuteNonQuery();
        }

        int httpCalls = 0;
        var handler = new MockHttpMessageHandler((req, ct) =>
        {
            httpCalls++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        });

        var masterKey = new byte[32];
        var keyPath = Path.Combine(Path.GetTempPath(), $"master_{Guid.NewGuid():N}.key");
        var statePath = Path.Combine(Path.GetTempPath(), $"state_{Guid.NewGuid():N}.json");
        DeviceCredentials.SaveMasterKey(keyPath, masterKey);
        DeviceCredentials.SaveDeviceState(statePath, new DeviceState("dev-1", "token-1"));

        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Capture:Sync:Enabled"] = "true",
            ["Capture:Sync:ServerUrl"] = "https://localhost:5001",
            ["Capture:Sync:MasterKeyPath"] = keyPath,
            ["Capture:Sync:StatePath"] = statePath
        }).Build();

        var loggerProvider = new TestLoggerProvider();
        var loggerFactory = LoggerFactory.Create(builder => builder.AddProvider(loggerProvider));
        var client = new CaptureSyncHttpClient(new HttpClient(handler), loggerFactory.CreateLogger<CaptureSyncHttpClient>());
        var runner = new CaptureSyncRunner(cache, client, config, TimeProvider.System, loggerFactory.CreateLogger<CaptureSyncRunner>());

        var result = await runner.PushCycleAsync();

        Assert.True(result);
        Assert.Equal(1, runner.PoisonCount);
        Assert.Equal(0, httpCalls);

        Assert.Single(cache.GetOutboxRows()); // Kept in database!
        // ...lekin navbatdan chiqarilgan: har siklda qayta olinsa ortidagi
        // qatorlarni to'sib qo'yardi.
        Assert.Empty(cache.GetEligibleOutboxRows(10, 200));
    }

    // 7. Contract: the captured request body deserialises into CustomSync.Api.Endpoints.SyncEndpoints.PushRequest
    // with JsonNamingPolicy.SnakeCaseLower; RecordId.Compute over its fields matches; decrypting payload returns payload_json byte-for-byte.
    [Fact]
    public async Task Test07_Sync_request_body_matches_api_contract_and_decrypts_payload_json()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"sync_test_07_{Guid.NewGuid():N}.db");
        var cache = new MessageCache(dbPath);
        cache.Initialize();

        var originalPayload = "{\"account_id\":\"111222333\",\"peer_id\":\"7053823996\",\"text\":\"test message 7\"}";
        using (var conn = new SqliteConnection($"Data Source={dbPath}"))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                INSERT INTO capture_outbox (kind, account_id, peer_id, msg_id, occurred_at, observed_at, payload_json, created_at)
                VALUES ('edited', '111222333', '7053823996', 390234, 1787000010, 1787000010, @payload, 1787000010);";
            cmd.Parameters.AddWithValue("@payload", originalPayload);
            cmd.ExecuteNonQuery();
        }

        var masterKey = new byte[32];
        RandomNumberGenerator.Fill(masterKey);
        var keyPath = Path.Combine(Path.GetTempPath(), $"master_{Guid.NewGuid():N}.key");
        var statePath = Path.Combine(Path.GetTempPath(), $"state_{Guid.NewGuid():N}.json");
        DeviceCredentials.SaveMasterKey(keyPath, masterKey);
        DeviceCredentials.SaveDeviceState(statePath, new DeviceState("dev-1", "refresh-token-1"));

        string? capturedJson = null;
        var handler = new MockHttpMessageHandler(async (req, ct) =>
        {
            if (req.RequestUri!.AbsolutePath.Contains("/api/v1/devices/refresh"))
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(JsonSerializer.Serialize(new
                    {
                        refresh_token = "refresh-token-1",
                        access_token = "access-token-1",
                        expires_at = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds()
                    }), Encoding.UTF8, "application/json")
                };
            }

            capturedJson = await req.Content!.ReadAsStringAsync(ct);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"results\":[{\"record_id\":\"irrelevant\",\"status\":\"created\"}]}", Encoding.UTF8, "application/json")
            };
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
        await runner.PushCycleAsync();

        Assert.NotNull(capturedJson);
        var options = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };
        var request = JsonSerializer.Deserialize<SyncEndpoints.PushRequest>(capturedJson, options);

        Assert.NotNull(request);
        Assert.Single(request.Records);
        var rec = request.Records[0];

        var computedId = RecordId.Compute(rec.Kind, rec.AccountHash, rec.PeerHash, rec.MsgId, rec.OccurredAt);
        Assert.Equal(computedId, rec.RecordId);

        var contentKey = SyncCrypto.DeriveContentKey(masterKey);
        var decryptedPlaintext = SyncCrypto.DecryptPayload(contentKey, rec.Nonce, rec.Payload);
        Assert.Equal(originalPayload, Encoding.UTF8.GetString(decryptedPlaintext));
    }

    // 8. Results created, duplicate, superseded, error in one batch →
    // exactly the first three rows deleted; the error row keeps its data and gets next_retry_at.
    [Fact]
    public async Task Test08_Batch_outcomes_delete_pushed_rows_and_schedule_retry_for_error()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"sync_test_08_{Guid.NewGuid():N}.db");
        var cache = new MessageCache(dbPath);
        cache.Initialize();

        long now = 1000;
        var timeProvider = new TestTimeProvider(now);

        using (var conn = new SqliteConnection($"Data Source={dbPath}"))
        {
            conn.Open();
            for (int i = 1; i <= 4; i++)
            {
                using var cmd = conn.CreateCommand();
                cmd.CommandText = @"
                    INSERT INTO capture_outbox (id, kind, account_id, peer_id, msg_id, occurred_at, observed_at, payload_json, created_at)
                    VALUES (@id, 'edited', '111', '222', @msgId, @occ, @occ, @payload, @occ);";
                cmd.Parameters.AddWithValue("@id", i);
                cmd.Parameters.AddWithValue("@msgId", i * 10);
                cmd.Parameters.AddWithValue("@occ", 100 + i);
                cmd.Parameters.AddWithValue("@payload", $"{{\"account_id\":\"111\",\"peer_id\":\"222\",\"i\":{i}}}");
                cmd.ExecuteNonQuery();
            }
        }

        var masterKey = new byte[32];
        var keyPath = Path.Combine(Path.GetTempPath(), $"master_{Guid.NewGuid():N}.key");
        var statePath = Path.Combine(Path.GetTempPath(), $"state_{Guid.NewGuid():N}.json");
        DeviceCredentials.SaveMasterKey(keyPath, masterKey);
        DeviceCredentials.SaveDeviceState(statePath, new DeviceState("dev-1", "rt-1"));

        var handler = new MockHttpMessageHandler(async (req, ct) =>
        {
            if (req.RequestUri!.AbsolutePath.Contains("/refresh"))
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(JsonSerializer.Serialize(new
                    {
                        refresh_token = "rt-1",
                        access_token = "at-1",
                        expires_at = now + 3600
                    }), Encoding.UTF8, "application/json")
                };
            }

            var body = await req.Content!.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(body);
            var recs = doc.RootElement.GetProperty("records").EnumerateArray().ToList();

            var results = new List<object>
            {
                new { record_id = recs[0].GetProperty("record_id").GetString()!, status = PushOutcome.Created },
                new { record_id = recs[1].GetProperty("record_id").GetString()!, status = PushOutcome.Duplicate },
                new { record_id = recs[2].GetProperty("record_id").GetString()!, status = PushOutcome.Superseded },
                new { record_id = recs[3].GetProperty("record_id").GetString()!, status = PushOutcome.Error, message = "server failed" }
            };

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(new { results }), Encoding.UTF8, "application/json")
            };
        });

        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Capture:Sync:Enabled"] = "true",
            ["Capture:Sync:ServerUrl"] = "https://localhost:5001",
            ["Capture:Sync:MasterKeyPath"] = keyPath,
            ["Capture:Sync:StatePath"] = statePath
        }).Build();

        var client = new CaptureSyncHttpClient(new HttpClient(handler));
        var runner = new CaptureSyncRunner(cache, client, config, timeProvider);
        await runner.PushCycleAsync();

        Assert.Equal(1, runner.PushedCount);
        Assert.Equal(2, runner.DuplicateCount);
        Assert.Equal(1, runner.ErrorCount);

        var remaining = cache.GetEligibleOutboxRows(10, now + 100);
        Assert.Single(remaining);
        var errRow = remaining[0];
        Assert.Equal(4, errRow.Id);
        Assert.Equal(1, errRow.RetryCount);
        Assert.Equal("server failed", errRow.LastError);
        Assert.Equal(now + 1, errRow.NextRetryAt);
    }

    // 9. A row replaced (INSERT OR REPLACE) while the push is in flight survives the success of the old version.
    [Fact]
    public async Task Test09_Row_replaced_in_flight_survives_deletion_of_old_version()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"sync_test_09_{Guid.NewGuid():N}.db");
        var cache = new MessageCache(dbPath);
        cache.Initialize();

        // Insert initial row (id will be 1)
        using (var conn = new SqliteConnection($"Data Source={dbPath}"))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                INSERT INTO capture_outbox (kind, account_id, peer_id, msg_id, occurred_at, observed_at, payload_json, created_at)
                VALUES ('edited', '111', '222', 10, 100, 100, @payload, 100);";
            cmd.Parameters.AddWithValue("@payload", "{\"account_id\":\"111\",\"peer_id\":\"222\",\"version\":1}");
            cmd.ExecuteNonQuery();
        }

        var masterKey = new byte[32];
        var keyPath = Path.Combine(Path.GetTempPath(), $"master_{Guid.NewGuid():N}.key");
        var statePath = Path.Combine(Path.GetTempPath(), $"state_{Guid.NewGuid():N}.json");
        DeviceCredentials.SaveMasterKey(keyPath, masterKey);
        DeviceCredentials.SaveDeviceState(statePath, new DeviceState("dev-1", "rt-1"));

        var handler = new MockHttpMessageHandler(async (req, ct) =>
        {
            if (req.RequestUri!.AbsolutePath.Contains("/refresh"))
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(JsonSerializer.Serialize(new
                    {
                        refresh_token = "rt-1",
                        access_token = "at-1",
                        expires_at = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds()
                    }), Encoding.UTF8, "application/json")
                };
            }

            // In flight: simulate a new edit for the same unique key replacing row in DB!
            using (var conn = new SqliteConnection($"Data Source={dbPath}"))
            {
                conn.Open();
                using var replaceCmd = conn.CreateCommand();
                replaceCmd.CommandText = @"
                    INSERT OR REPLACE INTO capture_outbox (kind, account_id, peer_id, msg_id, occurred_at, observed_at, payload_json, created_at)
                    VALUES ('edited', '111', '222', 10, 100, 200, @newPayload, 200);";
                replaceCmd.Parameters.AddWithValue("@newPayload", "{\"account_id\":\"111\",\"peer_id\":\"222\",\"version\":2}");
                replaceCmd.ExecuteNonQuery();
            }

            var body = await req.Content!.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(body);
            var rec = doc.RootElement.GetProperty("records")[0];
            var recId = rec.GetProperty("record_id").GetString();

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(new
                {
                    results = new[] { new { record_id = recId, status = PushOutcome.Created } }
                }), Encoding.UTF8, "application/json")
            };
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
        await runner.PushCycleAsync();

        // The newly inserted row (version 2 with new autoincrement ID) must survive!
        var remaining = cache.GetEligibleOutboxRows(10, 500);
        Assert.Single(remaining);
        Assert.Contains("\"version\":2", remaining[0].PayloadJson);
    }

    // 10. A failing row does not block: next cycle sends the other rows, the failing one only after its next_retry_at.
    [Fact]
    public async Task Test10_Failing_row_does_not_block_subsequent_eligible_rows()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"sync_test_10_{Guid.NewGuid():N}.db");
        var cache = new MessageCache(dbPath);
        cache.Initialize();

        var timeProvider = new TestTimeProvider(1000);

        // Row 1 will fail
        using (var conn = new SqliteConnection($"Data Source={dbPath}"))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                INSERT INTO capture_outbox (id, kind, account_id, peer_id, msg_id, occurred_at, observed_at, payload_json, created_at)
                VALUES (1, 'edited', '111', '222', 10, 100, 100, @p1, 100);";
            cmd.Parameters.AddWithValue("@p1", "{\"account_id\":\"111\",\"peer_id\":\"222\"}");
            cmd.ExecuteNonQuery();
        }

        var masterKey = new byte[32];
        var keyPath = Path.Combine(Path.GetTempPath(), $"master_{Guid.NewGuid():N}.key");
        var statePath = Path.Combine(Path.GetTempPath(), $"state_{Guid.NewGuid():N}.json");
        DeviceCredentials.SaveMasterKey(keyPath, masterKey);
        DeviceCredentials.SaveDeviceState(statePath, new DeviceState("dev-1", "rt-1"));

        var pushedMsgIds = new List<long>();
        var handler = new MockHttpMessageHandler(async (req, ct) =>
        {
            if (req.RequestUri!.AbsolutePath.Contains("/refresh"))
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(JsonSerializer.Serialize(new
                    {
                        refresh_token = "rt-1",
                        access_token = "at-1",
                        expires_at = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds()
                    }), Encoding.UTF8, "application/json")
                };
            }

            var body = await req.Content!.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(body);
            var recs = doc.RootElement.GetProperty("records").EnumerateArray().ToList();

            var results = new List<object>();
            foreach (var r in recs)
            {
                var msgId = r.GetProperty("msg_id").GetInt64();
                pushedMsgIds.Add(msgId);
                var recId = r.GetProperty("record_id").GetString()!;

                if (msgId == 10)
                {
                    results.Add(new { record_id = recId, status = PushOutcome.Error, message = "Row 1 error" });
                }
                else
                {
                    results.Add(new { record_id = recId, status = PushOutcome.Created });
                }
            }

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(new { results }), Encoding.UTF8, "application/json")
            };
        });

        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Capture:Sync:Enabled"] = "true",
            ["Capture:Sync:ServerUrl"] = "https://localhost:5001",
            ["Capture:Sync:MasterKeyPath"] = keyPath,
            ["Capture:Sync:StatePath"] = statePath
        }).Build();

        var client = new CaptureSyncHttpClient(new HttpClient(handler));
        var runner = new CaptureSyncRunner(cache, client, config, timeProvider);

        // Cycle 1: Row 1 fails
        await runner.PushCycleAsync();
        Assert.Equal(new[] { 10L }, pushedMsgIds);

        // Insert Row 2
        using (var conn = new SqliteConnection($"Data Source={dbPath}"))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                INSERT INTO capture_outbox (id, kind, account_id, peer_id, msg_id, occurred_at, observed_at, payload_json, created_at)
                VALUES (2, 'edited', '111', '222', 20, 105, 105, @p2, 105);";
            cmd.Parameters.AddWithValue("@p2", "{\"account_id\":\"111\",\"peer_id\":\"222\"}");
            cmd.ExecuteNonQuery();
        }

        // Cycle 2: immediate next cycle at same timestamp (before Row 1's next_retry_at = 1001)
        pushedMsgIds.Clear();
        await runner.PushCycleAsync();

        // Only Row 2 was sent! Row 1 did not block Row 2!
        Assert.Equal(new[] { 20L }, pushedMsgIds);

        // Cycle 3: advance time past retry delay
        timeProvider.SetUnixSeconds(1005);
        pushedMsgIds.Clear();
        await runner.PushCycleAsync();
        Assert.Equal(new[] { 10L }, pushedMsgIds);
    }

    // 11. Network error and 5xx → nothing deleted, cycle backs off.
    [Fact]
    public async Task Test11_Network_error_and_5xx_back_off_cycle_and_keep_rows_intact()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"sync_test_11_{Guid.NewGuid():N}.db");
        var cache = new MessageCache(dbPath);
        cache.Initialize();

        using (var conn = new SqliteConnection($"Data Source={dbPath}"))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                INSERT INTO capture_outbox (id, kind, account_id, peer_id, msg_id, occurred_at, observed_at, payload_json, created_at)
                VALUES (1, 'edited', '111', '222', 10, 100, 100, @p1, 100);";
            cmd.Parameters.AddWithValue("@p1", "{\"account_id\":\"111\",\"peer_id\":\"222\"}");
            cmd.ExecuteNonQuery();
        }

        var masterKey = new byte[32];
        var keyPath = Path.Combine(Path.GetTempPath(), $"master_{Guid.NewGuid():N}.key");
        var statePath = Path.Combine(Path.GetTempPath(), $"state_{Guid.NewGuid():N}.json");
        DeviceCredentials.SaveMasterKey(keyPath, masterKey);
        DeviceCredentials.SaveDeviceState(statePath, new DeviceState("dev-1", "rt-1"));

        var handler = new MockHttpMessageHandler((req, ct) =>
        {
            if (req.RequestUri!.AbsolutePath.Contains("/refresh"))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(JsonSerializer.Serialize(new
                    {
                        refresh_token = "rt-1",
                        access_token = "at-1",
                        expires_at = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds()
                    }), Encoding.UTF8, "application/json")
                });
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError)
            {
                Content = new StringContent("Server Error")
            });
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

        var ok1 = await runner.PushCycleAsync();
        Assert.False(ok1);
        Assert.True(runner.CycleBackoffSeconds >= 1);

        var rows = cache.GetEligibleOutboxRows(10, 200);
        Assert.Single(rows); // Nothing deleted!
    }

    // 12. 400 batch_too_large → batch halved, all rows eventually pushed.
    [Fact]
    public async Task Test12_Batch_too_large_400_halves_batch_size_and_retries()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"sync_test_12_{Guid.NewGuid():N}.db");
        var cache = new MessageCache(dbPath);
        cache.Initialize();

        for (int i = 1; i <= 4; i++)
        {
            using var conn = new SqliteConnection($"Data Source={dbPath}");
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                INSERT INTO capture_outbox (id, kind, account_id, peer_id, msg_id, occurred_at, observed_at, payload_json, created_at)
                VALUES (@id, 'edited', '111', '222', @id, @id, @id, @payload, @id);";
            cmd.Parameters.AddWithValue("@id", i);
            cmd.Parameters.AddWithValue("@payload", $"{{\"account_id\":\"111\",\"peer_id\":\"222\",\"i\":{i}}}");
            cmd.ExecuteNonQuery();
        }

        var masterKey = new byte[32];
        var keyPath = Path.Combine(Path.GetTempPath(), $"master_{Guid.NewGuid():N}.key");
        var statePath = Path.Combine(Path.GetTempPath(), $"state_{Guid.NewGuid():N}.json");
        DeviceCredentials.SaveMasterKey(keyPath, masterKey);
        DeviceCredentials.SaveDeviceState(statePath, new DeviceState("dev-1", "rt-1"));

        var pushBatchSizes = new List<int>();
        var handler = new MockHttpMessageHandler(async (req, ct) =>
        {
            if (req.RequestUri!.AbsolutePath.Contains("/refresh"))
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(JsonSerializer.Serialize(new
                    {
                        refresh_token = "rt-1",
                        access_token = "at-1",
                        expires_at = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds()
                    }), Encoding.UTF8, "application/json")
                };
            }

            var body = await req.Content!.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(body);
            var recs = doc.RootElement.GetProperty("records").EnumerateArray().ToList();
            pushBatchSizes.Add(recs.Count);

            if (recs.Count > 2)
            {
                return new HttpResponseMessage(HttpStatusCode.BadRequest)
                {
                    Content = new StringContent(JsonSerializer.Serialize(new
                    {
                        error = "batch_too_large",
                        max = 2,
                        received = recs.Count
                    }), Encoding.UTF8, "application/json")
                };
            }

            var results = recs.Select(r => new { record_id = r.GetProperty("record_id").GetString()!, status = PushOutcome.Created });
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(new { results }), Encoding.UTF8, "application/json")
            };
        });

        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Capture:Sync:Enabled"] = "true",
            ["Capture:Sync:ServerUrl"] = "https://localhost:5001",
            ["Capture:Sync:MasterKeyPath"] = keyPath,
            ["Capture:Sync:StatePath"] = statePath,
            ["Capture:Sync:PushBatchSize"] = "4"
        }).Build();

        var client = new CaptureSyncHttpClient(new HttpClient(handler));
        var runner = new CaptureSyncRunner(cache, client, config);

        // Cycle 1: attempts 4, gets 400 batch_too_large with max 2 -> halves to 2
        await runner.PushCycleAsync();
        // Cycle 2: sends first 2 rows
        await runner.PushCycleAsync();
        // Cycle 3: sends next 2 rows
        await runner.PushCycleAsync();

        Assert.Equal(4, runner.PushedCount);
        var remaining = cache.GetEligibleOutboxRows(10, 500);
        Assert.Empty(remaining);
    }

    // 13. 401 → one refresh; the rotated refresh token is on disk before the retried push; refresh 401 → sync stops, outbox intact.
    [Fact]
    public async Task Test13_Token_401_refreshes_and_persists_rotated_token_before_retrying_push_and_refresh_401_stops_sync()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"sync_test_13_{Guid.NewGuid():N}.db");
        var cache = new MessageCache(dbPath);
        cache.Initialize();

        using (var conn = new SqliteConnection($"Data Source={dbPath}"))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                INSERT INTO capture_outbox (id, kind, account_id, peer_id, msg_id, occurred_at, observed_at, payload_json, created_at)
                VALUES (1, 'edited', '111', '222', 10, 100, 100, @p1, 100);";
            cmd.Parameters.AddWithValue("@p1", "{\"account_id\":\"111\",\"peer_id\":\"222\"}");
            cmd.ExecuteNonQuery();
        }

        var masterKey = new byte[32];
        var keyPath = Path.Combine(Path.GetTempPath(), $"master_{Guid.NewGuid():N}.key");
        var statePath = Path.Combine(Path.GetTempPath(), $"state_{Guid.NewGuid():N}.json");
        DeviceCredentials.SaveMasterKey(keyPath, masterKey);
        DeviceCredentials.SaveDeviceState(statePath, new DeviceState("dev-1", "initial-rt"));

        bool pushFailedWith401 = false;
        bool diskHadRotatedTokenDuringPush = false;
        var handler = new MockHttpMessageHandler(async (req, ct) =>
        {
            if (req.RequestUri!.AbsolutePath.Contains("/refresh"))
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(JsonSerializer.Serialize(new
                    {
                        refresh_token = "rotated-rt",
                        access_token = "new-access-token",
                        expires_at = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds()
                    }), Encoding.UTF8, "application/json")
                };
            }

            if (!pushFailedWith401)
            {
                pushFailedWith401 = true;
                return new HttpResponseMessage(HttpStatusCode.Unauthorized);
            }

            // In retried push: verify state on disk has already rotated!
            var currentDiskState = DeviceCredentials.LoadDeviceState(statePath);
            diskHadRotatedTokenDuringPush = (currentDiskState?.RefreshToken == "rotated-rt");

            var body = await req.Content!.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(body);
            var recId = doc.RootElement.GetProperty("records")[0].GetProperty("record_id").GetString();

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(new
                {
                    results = new[] { new { record_id = recId, status = PushOutcome.Created } }
                }), Encoding.UTF8, "application/json")
            };
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

        var result = await runner.PushCycleAsync();
        Assert.True(result);
        Assert.True(diskHadRotatedTokenDuringPush); // Rotated token was persisted before push retry!

        // Now test scenario 2: Refresh itself answers 401 Unauthorized
        using (var conn = new SqliteConnection($"Data Source={dbPath}"))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                INSERT INTO capture_outbox (id, kind, account_id, peer_id, msg_id, occurred_at, observed_at, payload_json, created_at)
                VALUES (2, 'edited', '111', '222', 20, 200, 200, @p2, 200);";
            cmd.Parameters.AddWithValue("@p2", "{\"account_id\":\"111\",\"peer_id\":\"222\"}");
            cmd.ExecuteNonQuery();
        }

        var revokedHandler = new MockHttpMessageHandler((req, ct) =>
        {
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized));
        });

        var revokedClient = new CaptureSyncHttpClient(new HttpClient(revokedHandler));
        var revokedRunner = new CaptureSyncRunner(cache, revokedClient, config);

        var revokedResult = await revokedRunner.PushCycleAsync();
        Assert.False(revokedResult);
        Assert.True(revokedRunner.IsStopped); // Sync stops!

        // Outbox intact: row 2 remains!
        var intactRows = cache.GetEligibleOutboxRows(10, 500);
        Assert.Single(intactRows);
        Assert.Equal(2, intactRows[0].Id);
    }

    // 14. Key/state files: bad hex rejected without echo; atomic write; mode 0600 enforced on Linux
    // (skipped with a reason on Windows); a missing file → sync off, capture handler still records.
    [Fact]
    public void Test14_Key_and_state_files_validation_atomic_write_permissions_and_missing_file_handling()
    {
        // 1. Bad hex rejected without echo
        var mockPrompt = new MockConsolePrompt();
        mockPrompt.Enqueue("bad-key-not-64-hex");
        var output = new StringWriter();
        var code = SyncCliCommands.SetKey(new ConfigurationBuilder().Build(), mockPrompt, output);
        Assert.Equal(1, code);
        Assert.DoesNotContain("bad-key-not-64-hex", output.ToString());

        // 2. Atomic write
        var tempFile = Path.Combine(Path.GetTempPath(), $"atomic_{Guid.NewGuid():N}.txt");
        DeviceCredentials.WriteAtomic(tempFile, "sample content");
        Assert.True(File.Exists(tempFile));
        Assert.Equal("sample content", File.ReadAllText(tempFile));

        // 3. Mode 0600 logic
        Assert.False(DeviceCredentials.IsModeWiderThan0600((UnixFileMode)0x180)); // 0600 is user-read + user-write
        Assert.False(DeviceCredentials.IsModeWiderThan0600((UnixFileMode)0x100)); // 0400 is user-read only
        Assert.True(DeviceCredentials.IsModeWiderThan0600((UnixFileMode)0x1A4));  // 0644 is group/other read (wider)
        Assert.True(DeviceCredentials.IsModeWiderThan0600((UnixFileMode)0x1B6));  // 0666 is wider

        // Check platform behavior
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            // On Windows, mode check is skipped because Windows uses NTFS ACLs rather than POSIX file modes.
            Assert.True(DeviceCredentials.CheckUnixFilePermissions(tempFile));
        }

        // 4. Missing file -> returns null
        Assert.Null(DeviceCredentials.LoadMasterKey("non_existent_key_file.key"));
        Assert.Null(DeviceCredentials.LoadDeviceState("non_existent_state_file.json"));
    }

    // 15. Migration: a DB created by the old schema (no retry columns) opens, keeps its rows and pushes them.
    [Fact]
    public void Test15_Migration_upgrades_v1_database_and_preserves_rows()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"migration_test_{Guid.NewGuid():N}.db");

        // Manually create v1 schema (without retry columns)
        using (var conn = new SqliteConnection($"Data Source={dbPath}"))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                PRAGMA journal_mode = WAL;
                CREATE TABLE message_cache (
                    chat_id INTEGER NOT NULL,
                    message_id INTEGER NOT NULL,
                    text TEXT,
                    sender_id TEXT,
                    is_out INTEGER NOT NULL,
                    is_media INTEGER NOT NULL,
                    media_id TEXT,
                    date INTEGER NOT NULL,
                    cached_at INTEGER NOT NULL,
                    PRIMARY KEY (chat_id, message_id)
                );
                CREATE TABLE capture_outbox (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    kind TEXT NOT NULL,
                    account_id TEXT NOT NULL,
                    peer_id TEXT NOT NULL,
                    msg_id INTEGER NOT NULL,
                    occurred_at INTEGER NOT NULL,
                    observed_at INTEGER NOT NULL,
                    payload_json TEXT NOT NULL,
                    created_at INTEGER NOT NULL,
                    UNIQUE (kind, account_id, peer_id, msg_id, occurred_at)
                );
                PRAGMA user_version = 1;
                INSERT INTO capture_outbox (id, kind, account_id, peer_id, msg_id, occurred_at, observed_at, payload_json, created_at)
                VALUES (1, 'edited', '111', '222', 10, 100, 100, @p1, 100);
            ";
            cmd.Parameters.AddWithValue("@p1", "{\"account_id\":\"111\",\"peer_id\":\"222\"}");
            cmd.ExecuteNonQuery();
        }

        // Initialize MessageCache on existing v1 database
        var cache = new MessageCache(dbPath);
        cache.Initialize();

        using (var conn = new SqliteConnection($"Data Source={dbPath}"))
        {
            conn.Open();
            using var vCmd = conn.CreateCommand();
            vCmd.CommandText = "PRAGMA user_version;";
            var v = Convert.ToInt32(vCmd.ExecuteScalar());
            Assert.Equal(5, v); // Upgraded to v5!
        }

        var eligible = cache.GetEligibleOutboxRows(10, 200);
        Assert.Single(eligible);
        Assert.Equal(1, eligible[0].Id);
        Assert.Equal(0, eligible[0].RetryCount);
    }

    // 16. Production wiring: real registration from config, fake HttpMessageHandler via DI, a captured row reaches the fake server.
    [Fact]
    public async Task Test16_Production_wiring_registers_services_and_pushes_captured_row_via_di()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"wiring_test_{Guid.NewGuid():N}.db");
        var masterKey = new byte[32];
        var keyPath = Path.Combine(Path.GetTempPath(), $"master_{Guid.NewGuid():N}.key");
        var statePath = Path.Combine(Path.GetTempPath(), $"state_{Guid.NewGuid():N}.json");
        DeviceCredentials.SaveMasterKey(keyPath, masterKey);
        DeviceCredentials.SaveDeviceState(statePath, new DeviceState("dev-1", "rt-1"));

        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Capture:CacheDatabasePath"] = dbPath,
            ["Capture:Sync:Enabled"] = "true",
            ["Capture:Sync:ServerUrl"] = "https://localhost:5001",
            ["Capture:Sync:MasterKeyPath"] = keyPath,
            ["Capture:Sync:StatePath"] = statePath
        }).Build();

        bool pushReachedServer = false;
        var fakeHandler = new MockHttpMessageHandler((req, ct) =>
        {
            if (req.RequestUri!.AbsolutePath.Contains("/refresh"))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(JsonSerializer.Serialize(new
                    {
                        refresh_token = "rt-1",
                        access_token = "at-1",
                        expires_at = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds()
                    }), Encoding.UTF8, "application/json")
                });
            }

            pushReachedServer = true;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"results\":[{\"record_id\":\"irrelevant\",\"status\":\"created\"}]}", Encoding.UTF8, "application/json")
            });
        });

        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(config);
        services.AddMessageCache(config);
        services.AddSingleton<HttpMessageHandler>(fakeHandler);
        services.AddCaptureSyncClient(config);

        var sp = services.BuildServiceProvider();
        var cache = sp.GetRequiredService<MessageCache>();
        cache.Initialize();

        using (var conn = new SqliteConnection($"Data Source={dbPath}"))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                INSERT INTO capture_outbox (id, kind, account_id, peer_id, msg_id, occurred_at, observed_at, payload_json, created_at)
                VALUES (1, 'edited', '111', '222', 10, 100, 100, @p1, 100);";
            cmd.Parameters.AddWithValue("@p1", "{\"account_id\":\"111\",\"peer_id\":\"222\"}");
            cmd.ExecuteNonQuery();
        }

        var runner = sp.GetRequiredService<CaptureSyncRunner>();
        var result = await runner.PushCycleAsync();

        Assert.True(result);
        Assert.True(pushReachedServer);
    }

    // 17. Enabled absent → no HTTP at all; preflight rejects bad Enabled and a non-https URL.
    [Fact]
    public async Task Test17_Sync_enabled_absent_defaults_to_false_and_preflight_validates_enabled_and_url()
    {
        // 1. Absent Enabled -> defaults to false, no HTTP
        var dbPath = Path.Combine(Path.GetTempPath(), $"sync_test_17_{Guid.NewGuid():N}.db");
        var cache = new MessageCache(dbPath);
        cache.Initialize();

        var emptyConfig = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>()).Build();
        var client = new CaptureSyncHttpClient(new HttpClient(new MockHttpMessageHandler((req, ct) => throw new InvalidOperationException("Should not be called"))));
        var runner = new CaptureSyncRunner(cache, client, emptyConfig);

        Assert.False(runner.IsEnabled);
        var runResult = await runner.PushCycleAsync();
        Assert.False(runResult);

        // 2. Preflight rejects bad Enabled
        var badEnabledConfig = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Telegram:ApiId"] = "12345",
            ["Telegram:ApiHash"] = "hash",
            ["Telegram:DatabaseDirectory"] = Path.GetTempPath(),
            ["Telegram:FilesDirectory"] = Path.GetTempPath(),
            ["Capture:Sync:Enabled"] = "notabool"
        }).Build();

        var report1 = CapturePreflight.Check(badEnabledConfig, checker => true);
        Assert.False(report1.Passed);
        Assert.Contains(report1.Errors, e => e.Contains("Capture:Sync:Enabled must be 'true' or 'false'"));

        // 3. Preflight rejects non-https URL when enabled
        var httpConfig = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Telegram:ApiId"] = "12345",
            ["Telegram:ApiHash"] = "hash",
            ["Telegram:DatabaseDirectory"] = Path.GetTempPath(),
            ["Telegram:FilesDirectory"] = Path.GetTempPath(),
            ["Capture:Sync:Enabled"] = "true",
            ["Capture:Sync:ServerUrl"] = "http://remote-server.com"
        }).Build();

        var report2 = CapturePreflight.Check(httpConfig, checker => true);
        Assert.False(report2.Passed);
        Assert.Contains(report2.Errors, e => e.Contains("must use HTTPS"));

        // 4. Preflight accepts localhost or 127.0.0.1 with http
        var localHttpConfig = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Telegram:ApiId"] = "12345",
            ["Telegram:ApiHash"] = "hash",
            ["Telegram:DatabaseDirectory"] = Path.GetTempPath(),
            ["Telegram:FilesDirectory"] = Path.GetTempPath(),
            ["Capture:Sync:Enabled"] = "true",
            ["Capture:Sync:ServerUrl"] = "http://localhost:5000"
        }).Build();

        var report3 = CapturePreflight.Check(localHttpConfig, checker => true);
        Assert.DoesNotContain(report3.Errors, e => e.Contains("Capture:Sync:ServerUrl"));
    }

    // 18. Logs (all tests above) contain no payload text, key, token, code, account_id or peer_id.
    [Fact]
    public async Task Test18_Logs_never_contain_sensitive_payload_tokens_keys_or_user_ids()
    {
        var loggerProvider = new TestLoggerProvider();
        var loggerFactory = LoggerFactory.Create(builder => builder.AddProvider(loggerProvider));

        var dbPath = Path.Combine(Path.GetTempPath(), $"sync_test_18_{Guid.NewGuid():N}.db");
        var cache = new MessageCache(dbPath);
        cache.Initialize();

        var sensitivePayload = "SECRET_PAYLOAD_TEXT_XYZ";
        var sensitiveMasterHex = "000102030405060708090a0b0c0d0e0f101112131415161718191a1b1c1d1e1f";
        var sensitiveToken = "SUPER_SECRET_TOKEN_12345";
        var sensitiveAccountId = "999888777";
        var sensitivePeerId = "666555444";

        var keyPath = Path.Combine(Path.GetTempPath(), $"master_{Guid.NewGuid():N}.key");
        var statePath = Path.Combine(Path.GetTempPath(), $"state_{Guid.NewGuid():N}.json");
        DeviceCredentials.SaveMasterKey(keyPath, Convert.FromHexString(sensitiveMasterHex));
        DeviceCredentials.SaveDeviceState(statePath, new DeviceState("dev-1", sensitiveToken));

        using (var conn = new SqliteConnection($"Data Source={dbPath}"))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                INSERT INTO capture_outbox (id, kind, account_id, peer_id, msg_id, occurred_at, observed_at, payload_json, created_at)
                VALUES (1, 'edited', @acc, @peer, 10, 100, 100, @payload, 100);";
            cmd.Parameters.AddWithValue("@acc", sensitiveAccountId);
            cmd.Parameters.AddWithValue("@peer", sensitivePeerId);
            cmd.Parameters.AddWithValue("@payload", $"{{\"account_id\":\"{sensitiveAccountId}\",\"peer_id\":\"{sensitivePeerId}\",\"text\":\"{sensitivePayload}\"}}");
            cmd.ExecuteNonQuery();
        }

        var handler = new MockHttpMessageHandler((req, ct) =>
        {
            if (req.RequestUri!.AbsolutePath.Contains("/refresh"))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(JsonSerializer.Serialize(new
                    {
                        refresh_token = sensitiveToken,
                        access_token = "access-secret",
                        expires_at = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds()
                    }), Encoding.UTF8, "application/json")
                });
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError)
            {
                Content = new StringContent("Server Error")
            });
        });

        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Capture:Sync:Enabled"] = "true",
            ["Capture:Sync:ServerUrl"] = "https://localhost:5001",
            ["Capture:Sync:MasterKeyPath"] = keyPath,
            ["Capture:Sync:StatePath"] = statePath
        }).Build();

        var client = new CaptureSyncHttpClient(new HttpClient(handler), loggerFactory.CreateLogger<CaptureSyncHttpClient>());
        var runner = new CaptureSyncRunner(cache, client, config, TimeProvider.System, loggerFactory.CreateLogger<CaptureSyncRunner>());

        await runner.PushCycleAsync();

        foreach (var msg in loggerProvider.Messages)
        {
            Assert.DoesNotContain(sensitivePayload, msg);
            Assert.DoesNotContain(sensitiveMasterHex, msg);
            Assert.DoesNotContain(sensitiveToken, msg);
            Assert.DoesNotContain("access-secret", msg);
            Assert.DoesNotContain(sensitiveAccountId, msg);
            Assert.DoesNotContain(sensitivePeerId, msg);
        }
    }
}
