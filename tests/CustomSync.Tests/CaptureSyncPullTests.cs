using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CustomSync.Capture.Capture;
using CustomSync.Capture.Sync;
using CustomSync.Capture.Tdlib;
using CustomSync.Core;
using CustomSync.Core.Contracts;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace CustomSync.Tests;

public class CaptureSyncPullTests : IDisposable
{
    private readonly List<string> _tempFiles = new();

    private string CreateTempFile(string ext = "tmp")
    {
        var path = Path.Combine(Path.GetTempPath(), $"sync_pull_test_{Guid.NewGuid():N}.{ext}");
        _tempFiles.Add(path);
        return path;
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        foreach (var f in _tempFiles)
        {
            try
            {
                if (File.Exists(f)) File.Delete(f);
                var wal = f + "-wal";
                if (File.Exists(wal)) File.Delete(wal);
                var shm = f + "-shm";
                if (File.Exists(shm)) File.Delete(shm);
            }
            catch { }
        }
    }

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

    private static HttpResponseMessage JsonOk<T>(T value)
    {
        var json = JsonSerializer.Serialize(value, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
        });
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
    }

    private static StoredRecord CreateSettingRecord(
        byte[] masterKey,
        string key,
        string value,
        string accountId,
        long occurredAt,
        long seq = 1,
        string deviceId = "device-1",
        string? overrideRecordId = null,
        byte[]? overrideNonce = null,
        byte[]? overridePayload = null,
        string? overrideAccountHash = null,
        string? overridePeerHash = null,
        long? overrideMsgId = null,
        string kind = "setting")
    {
        var contentKey = SyncCrypto.DeriveContentKey(masterKey);
        var peerKey = SyncCrypto.DerivePeerKey(masterKey);
        var accountKey = SyncCrypto.DeriveAccountKey(masterKey);

        var accountHash = overrideAccountHash ?? CryptoPrimitives.ComputeAccountHash(accountKey, accountId);
        var peerHash = overridePeerHash ?? CryptoPrimitives.ComputePeerHash(peerKey, "0");
        var msgId = overrideMsgId ?? ActivityMapper.DiscriminatorFor(key);
        var recordId = overrideRecordId ?? RecordId.Compute(kind, accountHash, peerHash, msgId, occurredAt);

        byte[] payloadBytes;
        byte[] nonce;

        if (overridePayload != null)
        {
            payloadBytes = overridePayload;
            nonce = overrideNonce ?? new byte[12];
        }
        else
        {
            var dict = new Dictionary<string, string>
            {
                ["key"] = key,
                ["value"] = value,
                ["account_id"] = accountId,
                ["peer_id"] = "0"
            };
            var plaintext = JsonSerializer.SerializeToUtf8Bytes(dict);
            var (wirePayload, genNonce) = overrideNonce != null
                ? SyncCrypto.EncryptPayload(contentKey, plaintext, overrideNonce)
                : SyncCrypto.EncryptPayload(contentKey, plaintext);
            payloadBytes = wirePayload;
            nonce = genNonce;
        }

        return new StoredRecord
        {
            Seq = seq,
            RecordId = recordId,
            Kind = kind,
            AccountHash = accountHash,
            PeerHash = peerHash,
            MsgId = msgId,
            OccurredAt = occurredAt,
            ObservedAt = occurredAt,
            DeviceId = deviceId,
            Nonce = nonce,
            Payload = payloadBytes
        };
    }

    // 2. A setting record built from the vectors (scope.whitelist, account 111222333, occurred_at 1787000007)
    // has the vectors' record_id, verifies and is stored.
    [Fact]
    public async Task Test02_Setting_record_from_vectors_has_vector_record_id_verifies_and_is_stored()
    {
        var hkdfSection = TestVectors.Get("hkdf");
        var masterKey = Convert.FromHexString(hkdfSection.GetProperty("master_key_hex").GetString()!);
        var accountId = "111222333";
        long occurredAt = 1787000007;
        var key = "scope.whitelist";
        var value = "[\"7053823996\", \"562952781246744\"]";

        var record = CreateSettingRecord(masterKey, key, value, accountId, occurredAt);

        // Vector validation from test-vectors.json line 145-151
        const string expectedRecordId = "3de27f66fd99705bfc58df4d06987d54b4764733f4b7751c78225baebb9a267b";
        Assert.Equal(expectedRecordId, record.RecordId);

        var dbPath = CreateTempFile("db");
        var cache = new MessageCache(dbPath);
        cache.Initialize();

        var keyPath = CreateTempFile("key");
        var statePath = CreateTempFile("json");
        DeviceCredentials.SaveMasterKey(keyPath, masterKey);
        DeviceCredentials.SaveDeviceState(statePath, new DeviceState("dev-1", "rt-1"));

        var handler = new MockHttpMessageHandler((req, ct) =>
        {
            if (req.RequestUri!.AbsolutePath.Contains("/refresh"))
            {
                return Task.FromResult(JsonOk(new
                {
                    refresh_token = "rt-1",
                    access_token = "at-1",
                    expires_at = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds()
                }));
            }

            Assert.Contains("/pull", req.RequestUri.AbsolutePath);
            Assert.Contains("kind=setting", req.RequestUri.Query);

            var pullResp = new PullResponse
            {
                Records = new[] { record },
                NextSince = 10,
                HasMore = false
            };
            return Task.FromResult(JsonOk(pullResp));
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

        var success = await runner.PullCycleAsync();
        Assert.True(success);

        var stored = cache.GetSyncedSetting("scope.whitelist");
        Assert.NotNull(stored);
        Assert.Equal("scope.whitelist", stored.Key);
        Assert.Equal(value, stored.Value);
        Assert.Equal(occurredAt, stored.OccurredAt);
        Assert.Equal(expectedRecordId, stored.RecordId);
        Assert.Equal(10, cache.GetPullCursor("pull_cursor"));
    }

    // 3. Each verification step (1–5 of §2) rejects its tampered record, the scope stays as it was, the cursor still advances.
    [Fact]
    public async Task Test03_Each_verification_step_rejects_tampered_record_scope_stays_cursor_advances()
    {
        var masterKey = new byte[32];
        RandomNumberGenerator.Fill(masterKey);
        var contentKey = SyncCrypto.DeriveContentKey(masterKey);
        var peerKey = SyncCrypto.DerivePeerKey(masterKey);
        var accountKey = SyncCrypto.DeriveAccountKey(masterKey);

        var accountId = "111222333";
        var key = "scope.whitelist";
        var value = "[\"7053823996\"]";

        // Step 1: kind != "setting"
        var r1 = CreateSettingRecord(masterKey, key, value, accountId, 100, seq: 1, kind: "deleted");

        // Step 2: record_id mismatch
        var r2 = CreateSettingRecord(masterKey, key, value, accountId, 101, seq: 2, overrideRecordId: new string('f', 64));

        // Step 3: payload decryption failed (tampered ciphertext)
        var validR3 = CreateSettingRecord(masterKey, key, value, accountId, 102, seq: 3);
        var corruptedPayload = (byte[])validR3.Payload.Clone();
        corruptedPayload[^1] ^= 0x01; // flip tag bit
        var r3 = new StoredRecord
        {
            Seq = 3,
            RecordId = validR3.RecordId,
            Kind = validR3.Kind,
            AccountHash = validR3.AccountHash,
            PeerHash = validR3.PeerHash,
            MsgId = validR3.MsgId,
            OccurredAt = validR3.OccurredAt,
            ObservedAt = validR3.ObservedAt,
            DeviceId = validR3.DeviceId,
            Nonce = validR3.Nonce,
            Payload = corruptedPayload
        };

        // Step 4a: payload is not a JSON object
        var nonJsonObjectPlaintext = Encoding.UTF8.GetBytes("not a json object");
        var (nonJsonWire, nonJsonNonce) = SyncCrypto.EncryptPayload(contentKey, nonJsonObjectPlaintext);
        var r4a = CreateSettingRecord(masterKey, key, value, accountId, 103, seq: 4,
            overrideNonce: nonJsonNonce, overridePayload: nonJsonWire);

        // Step 4b: payload fields missing / not string
        var missingFieldPlaintext = Encoding.UTF8.GetBytes("{\"key\":\"scope.whitelist\"}");
        var (missingWire, missingNonce) = SyncCrypto.EncryptPayload(contentKey, missingFieldPlaintext);
        var r4b = CreateSettingRecord(masterKey, key, value, accountId, 104, seq: 5,
            overrideNonce: missingNonce, overridePayload: missingWire);

        // Step 4c: peer_id != "0"
        // JsonSerializer bilan: qo'lda yig'ilgan satrda value ichidagi qo'shtirnoq
        // JSON'ni buzardi va yozuv 4b bosqichida (noto'g'ri sabab bilan) rad etilardi.
        var badPeerIdPlaintext = JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, string>
        {
            ["key"] = key,
            ["value"] = value,
            ["account_id"] = accountId,
            ["peer_id"] = "12345"
        });
        var (badPeerWire, badPeerNonce) = SyncCrypto.EncryptPayload(contentKey, badPeerIdPlaintext);
        var r4c = CreateSettingRecord(masterKey, key, value, accountId, 105, seq: 6,
            overrideNonce: badPeerNonce, overridePayload: badPeerWire);

        // Step 4d: peer_hash mismatch
        var badPeerHash = CryptoPrimitives.ComputePeerHash(peerKey, "9999");
        var r4d = CreateSettingRecord(masterKey, key, value, accountId, 106, seq: 7, overridePeerHash: badPeerHash);

        // Step 4e: account_hash mismatch (§0.14)
        var badAccountHash = CryptoPrimitives.ComputeAccountHash(accountKey, "9999");
        var r4e = CreateSettingRecord(masterKey, key, value, accountId, 107, seq: 8, overrideAccountHash: badAccountHash);

        // Step 5: msg_id != DiscriminatorFor(key)
        var r5 = CreateSettingRecord(masterKey, key, value, accountId, 108, seq: 9, overrideMsgId: 1234567L);

        var tamperedRecords = new[] { r1, r2, r3, r4a, r4b, r4c, r4d, r4e, r5 };

        var dbPath = CreateTempFile("db");
        var cache = new MessageCache(dbPath);
        cache.Initialize();

        var keyPath = CreateTempFile("key");
        var statePath = CreateTempFile("json");
        DeviceCredentials.SaveMasterKey(keyPath, masterKey);
        DeviceCredentials.SaveDeviceState(statePath, new DeviceState("dev-1", "rt-1"));

        var handler = new MockHttpMessageHandler((req, ct) =>
        {
            if (req.RequestUri!.AbsolutePath.Contains("/refresh"))
            {
                return Task.FromResult(JsonOk(new
                {
                    refresh_token = "rt-1",
                    access_token = "at-1",
                    expires_at = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds()
                }));
            }

            var pullResp = new PullResponse
            {
                Records = tamperedRecords,
                NextSince = 50,
                HasMore = false
            };
            return Task.FromResult(JsonOk(pullResp));
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

        var success = await runner.PullCycleAsync();
        Assert.True(success);
        Assert.Equal(tamperedRecords.Length, runner.SkippedCount);

        // Scope remains unchanged (nothing stored)
        Assert.Null(cache.GetSyncedSetting(key));

        // Cursor still advanced!
        Assert.Equal(50, cache.GetPullCursor("pull_cursor"));
    }

    // 4. Unknown setting key → ignored, not counted as an error.
    [Fact]
    public async Task Test04_Unknown_setting_key_ignored_silently_not_counted_as_error()
    {
        var masterKey = new byte[32];
        RandomNumberGenerator.Fill(masterKey);

        var unknownRecord = CreateSettingRecord(masterKey, "ui.theme", "dark", "111222333", 100);

        var dbPath = CreateTempFile("db");
        var cache = new MessageCache(dbPath);
        cache.Initialize();

        var keyPath = CreateTempFile("key");
        var statePath = CreateTempFile("json");
        DeviceCredentials.SaveMasterKey(keyPath, masterKey);
        DeviceCredentials.SaveDeviceState(statePath, new DeviceState("dev-1", "rt-1"));

        var handler = new MockHttpMessageHandler((req, ct) =>
        {
            if (req.RequestUri!.AbsolutePath.Contains("/refresh"))
            {
                return Task.FromResult(JsonOk(new
                {
                    refresh_token = "rt-1",
                    access_token = "at-1",
                    expires_at = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds()
                }));
            }

            return Task.FromResult(JsonOk(new PullResponse
            {
                Records = new[] { unknownRecord },
                NextSince = 15,
                HasMore = false
            }));
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

        var success = await runner.PullCycleAsync();
        Assert.True(success);
        Assert.Equal(0, runner.SkippedCount); // Not counted as error!
        Assert.Null(cache.GetSyncedSetting("ui.theme")); // Ignored silently
        Assert.Equal(15, cache.GetPullCursor("pull_cursor")); // Cursor advanced!
    }

    // 5. Settings from two different account_hash values → both accepted;
    // the newest occurred_at wins; equal occurred_at → greater record_id wins;
    // an older record pulled later does not overwrite.
    [Fact]
    public async Task Test05_Settings_from_different_account_hashes_accepted_and_lww_applied()
    {
        var masterKey = new byte[32];
        RandomNumberGenerator.Fill(masterKey);

        var acc1 = "111222333";
        var acc2 = "999888777";
        var key = "scope.antidelete_global";

        var rec1 = CreateSettingRecord(masterKey, key, "false", acc1, occurredAt: 100, seq: 1);
        var rec2 = CreateSettingRecord(masterKey, key, "true", acc2, occurredAt: 200, seq: 2);
        var recOlder = CreateSettingRecord(masterKey, key, "false", acc1, occurredAt: 50, seq: 3);

        var dbPath = CreateTempFile("db");
        var cache = new MessageCache(dbPath);
        cache.Initialize();

        var keyPath = CreateTempFile("key");
        var statePath = CreateTempFile("json");
        DeviceCredentials.SaveMasterKey(keyPath, masterKey);
        DeviceCredentials.SaveDeviceState(statePath, new DeviceState("dev-1", "rt-1"));

        var queue = new Queue<PullResponse>();
        queue.Enqueue(new PullResponse { Records = new[] { rec1 }, NextSince = 1, HasMore = false });
        queue.Enqueue(new PullResponse { Records = new[] { rec2 }, NextSince = 2, HasMore = false });
        queue.Enqueue(new PullResponse { Records = new[] { recOlder }, NextSince = 3, HasMore = false });

        var handler = new MockHttpMessageHandler((req, ct) =>
        {
            if (req.RequestUri!.AbsolutePath.Contains("/refresh"))
            {
                return Task.FromResult(JsonOk(new
                {
                    refresh_token = "rt-1",
                    access_token = "at-1",
                    expires_at = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds()
                }));
            }

            return Task.FromResult(JsonOk(queue.Dequeue()));
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

        // Cycle 1: record 1 from acc1 (occurredAt = 100)
        await runner.PullCycleAsync();
        Assert.Equal("false", cache.GetSyncedSetting(key)?.Value);

        // Cycle 2: record 2 from acc2 (occurredAt = 200) -> both accounts accepted, newer wins
        await runner.PullCycleAsync();
        Assert.Equal("true", cache.GetSyncedSetting(key)?.Value);
        Assert.Equal(200, cache.GetSyncedSetting(key)?.OccurredAt);

        // Cycle 3: older record pulled later (occurredAt = 50) -> does not overwrite!
        await runner.PullCycleAsync();
        Assert.Equal("true", cache.GetSyncedSetting(key)?.Value);
        Assert.Equal(200, cache.GetSyncedSetting(key)?.OccurredAt);

        // Equal occurred_at tie-breaker test: greater record_id ordinally wins
        var tieRecA = CreateSettingRecord(masterKey, key, "valA", acc1, occurredAt: 300, seq: 4);
        var tieRecB = CreateSettingRecord(masterKey, key, "valB", acc2, occurredAt: 300, seq: 5);

        var expectedWinningValue = string.CompareOrdinal(tieRecA.RecordId, tieRecB.RecordId) > 0 ? "valA" : "valB";

        // Push smaller first then larger, and vice versa
        var smaller = string.CompareOrdinal(tieRecA.RecordId, tieRecB.RecordId) < 0 ? tieRecA : tieRecB;
        var larger = string.CompareOrdinal(tieRecA.RecordId, tieRecB.RecordId) > 0 ? tieRecA : tieRecB;

        cache.MergeSyncedSettingsAndCommitCursor(new[] { new SyncedSettingRow(key, smaller == tieRecA ? "valA" : "valB", smaller.OccurredAt, smaller.RecordId) }, 10);
        cache.MergeSyncedSettingsAndCommitCursor(new[] { new SyncedSettingRow(key, larger == tieRecA ? "valA" : "valB", larger.OccurredAt, larger.RecordId) }, 11);
        Assert.Equal(expectedWinningValue, cache.GetSyncedSetting(key)?.Value);

        // Try applying smaller again -> should NOT overwrite larger
        cache.MergeSyncedSettingsAndCommitCursor(new[] { new SyncedSettingRow(key, smaller == tieRecA ? "valA" : "valB", smaller.OccurredAt, smaller.RecordId) }, 12);
        Assert.Equal(expectedWinningValue, cache.GetSyncedSetting(key)?.Value);
    }

    // 6. Pulling the same page twice → no change (K4).
    [Fact]
    public async Task Test06_Pulling_same_page_twice_no_change_k4()
    {
        var masterKey = new byte[32];
        RandomNumberGenerator.Fill(masterKey);

        var records = new List<StoredRecord>();
        int seq = 1;
        foreach (var k in SyncedScopeSettingsSource.AllScopeKeys)
        {
            string val = k.Contains("categories") ? "{\"user\":true,\"group\":false,\"channel\":false}"
                : k.Contains("per_peer") ? "{}"
                : k.StartsWith("scope.activity_include") || k.StartsWith("scope.activity_exclude") || k.EndsWith("whitelist") || k.EndsWith("blacklist") ? "[]"
                : "true";
            records.Add(CreateSettingRecord(masterKey, k, val, "111222333", 100, seq++));
        }

        var dbPath = CreateTempFile("db");
        var cache = new MessageCache(dbPath);
        cache.Initialize();

        var keyPath = CreateTempFile("key");
        var statePath = CreateTempFile("json");
        DeviceCredentials.SaveMasterKey(keyPath, masterKey);
        DeviceCredentials.SaveDeviceState(statePath, new DeviceState("dev-1", "rt-1"));

        var handler = new MockHttpMessageHandler((req, ct) =>
        {
            if (req.RequestUri!.AbsolutePath.Contains("/refresh"))
            {
                return Task.FromResult(JsonOk(new
                {
                    refresh_token = "rt-1",
                    access_token = "at-1",
                    expires_at = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds()
                }));
            }

            return Task.FromResult(JsonOk(new PullResponse
            {
                Records = records,
                NextSince = 50,
                HasMore = false
            }));
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

        var first = await runner.PullCycleAsync();
        Assert.True(first);
        var settingsAfterFirst = cache.GetAllSyncedSettings();
        Assert.Equal(11, settingsAfterFirst.Count);

        // Reset cursor to 0 and pull same page again
        using (var conn = new SqliteConnection($"Data Source={dbPath}"))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "UPDATE sync_state SET value = '0' WHERE name = 'pull_cursor';";
            cmd.ExecuteNonQuery();
        }

        var second = await runner.PullCycleAsync();
        Assert.True(second);
        var settingsAfterSecond = cache.GetAllSyncedSettings();
        Assert.Equal(11, settingsAfterSecond.Count);

        for (int i = 0; i < 11; i++)
        {
            Assert.Equal(settingsAfterFirst[i].Key, settingsAfterSecond[i].Key);
            Assert.Equal(settingsAfterFirst[i].Value, settingsAfterSecond[i].Value);
            Assert.Equal(settingsAfterFirst[i].OccurredAt, settingsAfterSecond[i].OccurredAt);
            Assert.Equal(settingsAfterFirst[i].RecordId, settingsAfterSecond[i].RecordId);
        }
    }

    // 7. All 8 message keys → snapshot equals the values; remove or corrupt one → null; one non-canonical blacklist entry → null.
    [Fact]
    public void Test07_All_8_message_keys_yield_snapshot_remove_or_corrupt_or_non_canonical_yields_null()
    {
        var dict = new Dictionary<string, string>
        {
            ["scope.whitelist"] = "[\"7053823996\", \"562952781246744\"]",
            ["scope.blacklist"] = "[\"12345678\"]",
            ["scope.wl_categories"] = "{\"user\": true, \"group\": false, \"channel\": false}",
            ["scope.bl_categories"] = "{\"user\": false, \"group\": false, \"channel\": true}",
            ["scope.antidelete_global"] = "true",
            ["scope.antiedit_global"] = "false",
            ["scope.antidelete_per_peer"] = "{\"7053823996\": true, \"12345678\": false}",
            ["scope.antiedit_per_peer"] = "{\"7053823996\": true}"
        };

        var snapshot = SyncedScopeSettingsSource.BuildMessageSnapshot(dict);
        Assert.NotNull(snapshot);
        Assert.True(snapshot.Whitelist.Contains("7053823996"));
        Assert.True(snapshot.Whitelist.Contains("562952781246744"));
        Assert.True(snapshot.Blocklist.Contains("12345678"));
        Assert.True(snapshot.WhitelistCategories.User);
        Assert.False(snapshot.WhitelistCategories.Group);
        Assert.False(snapshot.WhitelistCategories.Channel);
        Assert.False(snapshot.BlocklistCategories.User);
        Assert.False(snapshot.BlocklistCategories.Group);
        Assert.True(snapshot.BlocklistCategories.Channel);
        Assert.True(snapshot.GlobalAntiDelete);
        Assert.False(snapshot.GlobalAntiEdit);
        Assert.True(snapshot.AntiDeletePerPeer["7053823996"]);
        Assert.False(snapshot.AntiDeletePerPeer["12345678"]);
        Assert.True(snapshot.AntiEditPerPeer["7053823996"]);

        // Remove one key -> null
        var missingOne = new Dictionary<string, string>(dict);
        missingOne.Remove("scope.antiedit_global");
        Assert.Null(SyncedScopeSettingsSource.BuildMessageSnapshot(missingOne));

        // Corrupt one key -> null
        var corruptCat = new Dictionary<string, string>(dict)
        {
            ["scope.wl_categories"] = "{\"user\": \"not_a_bool\"}"
        };
        Assert.Null(SyncedScopeSettingsSource.BuildMessageSnapshot(corruptCat));

        // One non-canonical blacklist entry -> null
        var nonCanonicalBl = new Dictionary<string, string>(dict)
        {
            ["scope.blacklist"] = "[\"12345678\", \"invalid_peer_id!\"]"
        };
        Assert.Null(SyncedScopeSettingsSource.BuildMessageSnapshot(nonCanonicalBl));

        // Non-canonical peer id in per-peer map -> null
        var nonCanonicalMap = new Dictionary<string, string>(dict)
        {
            ["scope.antidelete_per_peer"] = "{\"invalid_id\": true}"
        };
        Assert.Null(SyncedScopeSettingsSource.BuildMessageSnapshot(nonCanonicalMap));
    }

    // 8. All 3 activity keys → activity snapshot; missing one → null.
    [Fact]
    public void Test08_All_3_activity_keys_yield_activity_snapshot_missing_or_corrupt_yields_null()
    {
        var dict = new Dictionary<string, string>
        {
            ["scope.activity_track_all_contacts"] = "true",
            ["scope.activity_include"] = "[\"7053823996\"]",
            ["scope.activity_exclude"] = "[\"12345678\"]"
        };

        var snapshot = SyncedScopeSettingsSource.BuildActivitySnapshot(dict);
        Assert.NotNull(snapshot);
        Assert.True(snapshot.TrackAllContacts);
        Assert.True(snapshot.Include.Contains("7053823996"));
        Assert.True(snapshot.Exclude.Contains("12345678"));

        // Missing one key -> null
        var missingExclude = new Dictionary<string, string>(dict);
        missingExclude.Remove("scope.activity_exclude");
        Assert.Null(SyncedScopeSettingsSource.BuildActivitySnapshot(missingExclude));

        // Corrupted track_all_contacts -> null
        var badBool = new Dictionary<string, string>(dict)
        {
            ["scope.activity_track_all_contacts"] = "maybe"
        };
        Assert.Null(SyncedScopeSettingsSource.BuildActivitySnapshot(badBool));

        // Non-canonical entry in include list -> null
        var nonCanonicalInc = new Dictionary<string, string>(dict)
        {
            ["scope.activity_include"] = "[\"not-canonical-id\"]"
        };
        Assert.Null(SyncedScopeSettingsSource.BuildActivitySnapshot(nonCanonicalInc));
    }

    // 9. End-to-end: real registrations, fake server serves the 11 keys →
    // CaptureScopeEvaluator / ActivityScopeEvaluator decisions follow the owner's settings;
    // before the pull they fall back to the server default.
    [Fact]
    public async Task Test09_End_to_end_real_registrations_evaluators_follow_owner_settings_after_pull()
    {
        var masterKey = new byte[32];
        RandomNumberGenerator.Fill(masterKey);

        var dbPath = CreateTempFile("db");
        var keyPath = CreateTempFile("key");
        var statePath = CreateTempFile("json");
        DeviceCredentials.SaveMasterKey(keyPath, masterKey);
        DeviceCredentials.SaveDeviceState(statePath, new DeviceState("dev-1", "rt-1"));

        var allRecords = new List<StoredRecord>
        {
            CreateSettingRecord(masterKey, "scope.whitelist", "[\"7053823996\"]", "111", 100, 1),
            CreateSettingRecord(masterKey, "scope.blacklist", "[]", "111", 100, 2),
            CreateSettingRecord(masterKey, "scope.wl_categories", "{\"user\":false,\"group\":false,\"channel\":false}", "111", 100, 3),
            CreateSettingRecord(masterKey, "scope.bl_categories", "{\"user\":false,\"group\":false,\"channel\":false}", "111", 100, 4),
            CreateSettingRecord(masterKey, "scope.antidelete_global", "false", "111", 100, 5),
            CreateSettingRecord(masterKey, "scope.antiedit_global", "false", "111", 100, 6),
            CreateSettingRecord(masterKey, "scope.antidelete_per_peer", "{}", "111", 100, 7),
            CreateSettingRecord(masterKey, "scope.antiedit_per_peer", "{}", "111", 100, 8),
            CreateSettingRecord(masterKey, "scope.activity_track_all_contacts", "false", "111", 100, 9),
            CreateSettingRecord(masterKey, "scope.activity_include", "[\"7053823996\"]", "111", 100, 10),
            CreateSettingRecord(masterKey, "scope.activity_exclude", "[]", "111", 100, 11)
        };

        var handler = new MockHttpMessageHandler((req, ct) =>
        {
            if (req.RequestUri!.AbsolutePath.Contains("/refresh"))
            {
                return Task.FromResult(JsonOk(new
                {
                    refresh_token = "rt-1",
                    access_token = "at-1",
                    expires_at = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds()
                }));
            }

            return Task.FromResult(JsonOk(new PullResponse
            {
                Records = allRecords,
                NextSince = 11,
                HasMore = false
            }));
        });

        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Capture:CacheDatabasePath"] = dbPath,
            ["Capture:Sync:Enabled"] = "true",
            ["Capture:Sync:ServerUrl"] = "https://localhost:5001",
            ["Capture:Sync:MasterKeyPath"] = keyPath,
            ["Capture:Sync:StatePath"] = statePath
        }).Build();

        var cache = new MessageCache(dbPath);
        cache.Initialize();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(config);
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton(cache);
        services.AddSingleton<ITdClient, MockTdClient>();
        services.AddCaptureHandlers();
        services.AddSingleton<HttpMessageHandler>(handler);
        services.AddCaptureSyncClient(config);

        using var sp = services.BuildServiceProvider();

        var captureScope = sp.GetRequiredService<ICaptureScope>();
        var activityScope = sp.GetRequiredService<IActivityScope>();

        // Before pull: decisions fall back to server default (false)
        var canDeleteBefore = captureScope.ShouldAntiDelete("7053823996");
        Assert.False(canDeleteBefore);

        var trackBefore = activityScope.ShouldTrackActivity("7053823996", isContact: false);
        Assert.False(trackBefore);

        // Execute sync cycle
        var runner = sp.GetRequiredService<CaptureSyncRunner>();
        var pulled = await runner.PullCycleAsync();
        Assert.True(pulled);

        // After pull: decisions follow the owner's settings!
        var canDeleteAfter = captureScope.ShouldAntiDelete("7053823996");
        Assert.True(canDeleteAfter);

        var trackAfter = activityScope.ShouldTrackActivity("7053823996", isContact: false);
        Assert.True(trackAfter);
    }

    private class MockTdClient : ITdClient
    {
        public int ClientId => 1;
        public int PendingRequestCount => 0;
        public event Action<string>? UpdateReceived { add { } remove { } }
        public Task<string> SendAsync(string requestJson, TimeSpan? timeout = null, CancellationToken ct = default) => Task.FromResult("{}");
        public string? Execute(string requestJson) => null;
        public void Dispose() { }
    }

    // 10. Restart: new service provider over the same SQLite file, server unreachable → the snapshot is already there.
    [Fact]
    public async Task Test10_Restart_new_service_provider_over_same_db_loads_snapshot_before_pull()
    {
        var masterKey = new byte[32];
        RandomNumberGenerator.Fill(masterKey);

        var dbPath = CreateTempFile("db");
        var keyPath = CreateTempFile("key");
        var statePath = CreateTempFile("json");
        DeviceCredentials.SaveMasterKey(keyPath, masterKey);
        DeviceCredentials.SaveDeviceState(statePath, new DeviceState("dev-1", "rt-1"));

        var allRecords = new List<StoredRecord>
        {
            CreateSettingRecord(masterKey, "scope.whitelist", "[\"7053823996\"]", "111", 100, 1),
            CreateSettingRecord(masterKey, "scope.blacklist", "[]", "111", 100, 2),
            CreateSettingRecord(masterKey, "scope.wl_categories", "{\"user\":true,\"group\":false,\"channel\":false}", "111", 100, 3),
            CreateSettingRecord(masterKey, "scope.bl_categories", "{\"user\":false,\"group\":false,\"channel\":false}", "111", 100, 4),
            CreateSettingRecord(masterKey, "scope.antidelete_global", "true", "111", 100, 5),
            CreateSettingRecord(masterKey, "scope.antiedit_global", "false", "111", 100, 6),
            CreateSettingRecord(masterKey, "scope.antidelete_per_peer", "{}", "111", 100, 7),
            CreateSettingRecord(masterKey, "scope.antiedit_per_peer", "{}", "111", 100, 8),
            CreateSettingRecord(masterKey, "scope.activity_track_all_contacts", "true", "111", 100, 9),
            CreateSettingRecord(masterKey, "scope.activity_include", "[]", "111", 100, 10),
            CreateSettingRecord(masterKey, "scope.activity_exclude", "[]", "111", 100, 11)
        };

        // First container: pull settings
        var handler = new MockHttpMessageHandler((req, ct) =>
        {
            if (req.RequestUri!.AbsolutePath.Contains("/refresh"))
            {
                return Task.FromResult(JsonOk(new
                {
                    refresh_token = "rt-1",
                    access_token = "at-1",
                    expires_at = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds()
                }));
            }
            return Task.FromResult(JsonOk(new PullResponse { Records = allRecords, NextSince = 11, HasMore = false }));
        });

        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Capture:CacheDatabasePath"] = dbPath,
            ["Capture:Sync:Enabled"] = "true",
            ["Capture:Sync:ServerUrl"] = "https://localhost:5001",
            ["Capture:Sync:MasterKeyPath"] = keyPath,
            ["Capture:Sync:StatePath"] = statePath
        }).Build();

        var cache = new MessageCache(dbPath);
        cache.Initialize();

        {
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddSingleton<IConfiguration>(config);
            services.AddSingleton(TimeProvider.System);
            services.AddSingleton(cache);
            services.AddSingleton<ITdClient, MockTdClient>();
            services.AddCaptureHandlers();
            services.AddSingleton<HttpMessageHandler>(handler);
            services.AddCaptureSyncClient(config);
            using var sp = services.BuildServiceProvider();
            var runner = sp.GetRequiredService<CaptureSyncRunner>();
            Assert.True(await runner.PullCycleAsync());
        }

        // Second container: server is completely unreachable (HTTP 500 / throw)
        var failingHandler = new MockHttpMessageHandler((req, ct) =>
        {
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError));
        });

        {
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddSingleton<IConfiguration>(config);
            services.AddSingleton(TimeProvider.System);
            services.AddSingleton(cache);
            services.AddSingleton<ITdClient, MockTdClient>();
            services.AddCaptureHandlers();
            services.AddSingleton<HttpMessageHandler>(failingHandler);
            services.AddCaptureSyncClient(config);
            using var sp = services.BuildServiceProvider();

            // The snapshot is already there upon construction before any pull!
            var messageSource = sp.GetRequiredService<ISyncedScopeSettingsSource>();
            var activitySource = sp.GetRequiredService<ISyncedActivityScopeSettingsSource>();

            Assert.NotNull(messageSource.CurrentSnapshot);
            Assert.True(messageSource.CurrentSnapshot.Whitelist.Contains("7053823996"));
            Assert.True(messageSource.CurrentSnapshot.GlobalAntiDelete);

            Assert.NotNull(activitySource.CurrentSnapshot);
            Assert.True(activitySource.CurrentSnapshot.TrackAllContacts);
        }
    }

    private class CrashingMessageCache : MessageCache
    {
        public bool ShouldCrash { get; set; } = true;
        public CrashingMessageCache(string dbPath) : base(dbPath) { }

        public override bool MergeSyncedSettingsAndCommitCursor(IReadOnlyList<SyncedSettingRow> candidates, long newCursor, Action? onBeforeCommit = null)
        {
            if (ShouldCrash)
            {
                return base.MergeSyncedSettingsAndCommitCursor(candidates, newCursor, () =>
                {
                    throw new InvalidOperationException("Simulated crash right before commit");
                });
            }
            return base.MergeSyncedSettingsAndCommitCursor(candidates, newCursor, onBeforeCommit);
        }
    }

    // 11. Cursor: a crash (exception) after reading a page but before commit leaves the cursor unchanged and nothing half-stored; the next cycle re-pulls it.
    [Fact]
    public async Task Test11_Cursor_crash_before_commit_leaves_cursor_unchanged_and_next_cycle_repulls()
    {
        var masterKey = new byte[32];
        RandomNumberGenerator.Fill(masterKey);

        var dbPath = CreateTempFile("db");
        var cache = new CrashingMessageCache(dbPath);
        cache.Initialize();

        var keyPath = CreateTempFile("key");
        var statePath = CreateTempFile("json");
        DeviceCredentials.SaveMasterKey(keyPath, masterKey);
        DeviceCredentials.SaveDeviceState(statePath, new DeviceState("dev-1", "rt-1"));

        var record = CreateSettingRecord(masterKey, "scope.antidelete_global", "true", "111", 100, 1);

        var handler = new MockHttpMessageHandler((req, ct) =>
        {
            if (req.RequestUri!.AbsolutePath.Contains("/refresh"))
            {
                return Task.FromResult(JsonOk(new
                {
                    refresh_token = "rt-1",
                    access_token = "at-1",
                    expires_at = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds()
                }));
            }

            return Task.FromResult(JsonOk(new PullResponse
            {
                Records = new[] { record },
                NextSince = 10,
                HasMore = false
            }));
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

        // First attempt crashes right before commit
        cache.ShouldCrash = true;
        await Assert.ThrowsAnyAsync<Exception>(() => runner.PullCycleAsync());

        // Verify cursor unchanged (0) and nothing stored
        Assert.Equal(0, cache.GetPullCursor("pull_cursor"));
        Assert.Null(cache.GetSyncedSetting("scope.antidelete_global"));

        // Next cycle without crash succeeds and re-pulls it
        cache.ShouldCrash = false;
        var success = await runner.PullCycleAsync();
        Assert.True(success);

        Assert.Equal(10, cache.GetPullCursor("pull_cursor"));
        Assert.Equal("true", cache.GetSyncedSetting("scope.antidelete_global")?.Value);
    }

    // 12. has_more paging stops at MaxPullPagesPerCycle.
    [Fact]
    public async Task Test12_Has_more_paging_stops_at_max_pull_pages_per_cycle()
    {
        var masterKey = new byte[32];
        RandomNumberGenerator.Fill(masterKey);

        var dbPath = CreateTempFile("db");
        var cache = new MessageCache(dbPath);
        cache.Initialize();

        var keyPath = CreateTempFile("key");
        var statePath = CreateTempFile("json");
        DeviceCredentials.SaveMasterKey(keyPath, masterKey);
        DeviceCredentials.SaveDeviceState(statePath, new DeviceState("dev-1", "rt-1"));

        int pullCallCount = 0;
        var handler = new MockHttpMessageHandler((req, ct) =>
        {
            if (req.RequestUri!.AbsolutePath.Contains("/refresh"))
            {
                return Task.FromResult(JsonOk(new
                {
                    refresh_token = "rt-1",
                    access_token = "at-1",
                    expires_at = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds()
                }));
            }

            pullCallCount++;
            var rec = CreateSettingRecord(masterKey, "scope.antidelete_global", "true", "111", 100 + pullCallCount, pullCallCount);
            return Task.FromResult(JsonOk(new PullResponse
            {
                Records = new[] { rec },
                NextSince = pullCallCount * 10,
                HasMore = true // Always has more!
            }));
        });

        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Capture:Sync:Enabled"] = "true",
            ["Capture:Sync:ServerUrl"] = "https://localhost:5001",
            ["Capture:Sync:MasterKeyPath"] = keyPath,
            ["Capture:Sync:StatePath"] = statePath,
            ["Capture:Sync:MaxPullPagesPerCycle"] = "3" // Stop at 3 pages
        }).Build();

        var client = new CaptureSyncHttpClient(new HttpClient(handler));
        var runner = new CaptureSyncRunner(cache, client, config);

        var success = await runner.PullCycleAsync();
        Assert.True(success);
        Assert.Equal(3, pullCallCount); // Stopped at exactly 3 pages!
        Assert.Equal(30, cache.GetPullCursor("pull_cursor"));
    }

    // 13. HTTP 5xx / bad JSON on pull → cursor unchanged, backoff.
    [Fact]
    public async Task Test13_Http_5xx_and_bad_json_on_pull_cursor_unchanged_backoff()
    {
        var masterKey = new byte[32];
        RandomNumberGenerator.Fill(masterKey);

        var dbPath = CreateTempFile("db");
        var cache = new MessageCache(dbPath);
        cache.Initialize();

        var keyPath = CreateTempFile("key");
        var statePath = CreateTempFile("json");
        DeviceCredentials.SaveMasterKey(keyPath, masterKey);
        DeviceCredentials.SaveDeviceState(statePath, new DeviceState("dev-1", "rt-1"));

        var responses = new Queue<HttpResponseMessage>();
        // First: 500 error
        responses.Enqueue(new HttpResponseMessage(HttpStatusCode.InternalServerError)
        {
            Content = new StringContent("Server Error")
        });
        // Second: Bad JSON
        responses.Enqueue(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{ not valid json at all")
        });

        var handler = new MockHttpMessageHandler((req, ct) =>
        {
            if (req.RequestUri!.AbsolutePath.Contains("/refresh"))
            {
                return Task.FromResult(JsonOk(new
                {
                    refresh_token = "rt-1",
                    access_token = "at-1",
                    expires_at = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds()
                }));
            }

            return Task.FromResult(responses.Dequeue());
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

        // Cycle 1: HTTP 500
        var res1 = await runner.PullCycleAsync();
        Assert.False(res1);
        Assert.Equal(0, cache.GetPullCursor("pull_cursor"));
        Assert.Equal(1, runner.CycleBackoffSeconds);

        // Cycle 2: Bad JSON
        var res2 = await runner.PullCycleAsync();
        Assert.False(res2);
        Assert.Equal(0, cache.GetPullCursor("pull_cursor"));
        Assert.Equal(2, runner.CycleBackoffSeconds); // Backoff stepped up
    }

    // 14. Migration: a v2 file (as created by the current code) upgrades to v3 with every existing table and row intact.
    [Fact]
    public void Test14_Migration_upgrades_v2_database_and_preserves_existing_tables_and_rows()
    {
        var dbPath = CreateTempFile("db");

        // Create v2 database schema
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
                    retry_count INTEGER NOT NULL DEFAULT 0,
                    next_retry_at INTEGER,
                    last_error TEXT,
                    UNIQUE (kind, account_id, peer_id, msg_id, occurred_at)
                );
                CREATE TABLE activity_latest (
                    peer_id TEXT NOT NULL,
                    field TEXT NOT NULL,
                    value TEXT NOT NULL,
                    observed_at INTEGER NOT NULL,
                    PRIMARY KEY (peer_id, field)
                );
                CREATE TABLE pending_edits (
                    chat_id INTEGER NOT NULL,
                    message_id INTEGER NOT NULL,
                    peer_id TEXT,
                    account_id TEXT,
                    old_text TEXT,
                    new_text TEXT,
                    is_out INTEGER,
                    msg_date INTEGER,
                    edit_date INTEGER,
                    observed_at INTEGER NOT NULL,
                    PRIMARY KEY (chat_id, message_id)
                );
                PRAGMA user_version = 2;

                INSERT INTO message_cache (chat_id, message_id, text, sender_id, is_out, is_media, date, cached_at)
                VALUES (123, 456, 'sample cached message', 'user_1', 0, 0, 1000, 1000);

                INSERT INTO capture_outbox (id, kind, account_id, peer_id, msg_id, occurred_at, observed_at, payload_json, created_at, retry_count)
                VALUES (1, 'edited', '111', '222', 10, 100, 100, @p1, 100, 2);

                INSERT INTO activity_latest (peer_id, field, value, observed_at)
                VALUES ('222', 'status', 'online:100', 100);
            ";
            cmd.Parameters.AddWithValue("@p1", "{\"text\":\"sample\"}");
            cmd.ExecuteNonQuery();
        }

        // Initialize MessageCache on v2 database
        var cache = new MessageCache(dbPath);
        cache.Initialize();

        // Check user_version upgraded to 3
        using (var conn = new SqliteConnection($"Data Source={dbPath}"))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "PRAGMA user_version;";
            var version = Convert.ToInt32(cmd.ExecuteScalar());
            Assert.Equal(3, version);
        }

        // Check all existing rows are intact
        var cached = cache.Get(123, 456);
        Assert.NotNull(cached);
        Assert.Equal("sample cached message", cached.Text);

        var outbox = cache.GetOutboxRows();
        Assert.Single(outbox);
        Assert.Equal(1, outbox[0].Id);
        Assert.Equal(2, outbox[0].RetryCount);

        // Check new tables exist and function
        cache.MergeSyncedSettingsAndCommitCursor(new[]
        {
            new SyncedSettingRow("scope.antidelete_global", "true", 100, "rec-1")
        }, 5);

        Assert.Equal(5, cache.GetPullCursor("pull_cursor"));
        Assert.Equal("true", cache.GetSyncedSetting("scope.antidelete_global")?.Value);
    }

    // 15. Logs contain no setting values, payloads, keys, tokens or ids.
    [Fact]
    public async Task Test15_Logs_contain_no_setting_values_payloads_keys_tokens_or_ids()
    {
        var masterKey = new byte[32];
        RandomNumberGenerator.Fill(masterKey);
        var masterKeyHex = Convert.ToHexString(masterKey).ToLowerInvariant();

        var dbPath = CreateTempFile("db");
        var cache = new MessageCache(dbPath);
        cache.Initialize();

        var keyPath = CreateTempFile("key");
        var statePath = CreateTempFile("json");
        DeviceCredentials.SaveMasterKey(keyPath, masterKey);
        DeviceCredentials.SaveDeviceState(statePath, new DeviceState("dev-1", "secret-refresh-token-xyz"));

        const string sensitiveAccountId = "998877665544";
        const string sensitivePeerId = "776655443322";
        const string sensitiveValue = "[\"776655443322\"]";

        var rec = CreateSettingRecord(masterKey, "scope.whitelist", sensitiveValue, sensitiveAccountId, 100, 1);
        var badRec = CreateSettingRecord(masterKey, "scope.whitelist", sensitiveValue, sensitiveAccountId, 101, 2, overrideRecordId: new string('0', 64));

        var handler = new MockHttpMessageHandler((req, ct) =>
        {
            if (req.RequestUri!.AbsolutePath.Contains("/refresh"))
            {
                return Task.FromResult(JsonOk(new
                {
                    refresh_token = "secret-refresh-token-xyz",
                    access_token = "secret-access-token-abc",
                    expires_at = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds()
                }));
            }

            return Task.FromResult(JsonOk(new PullResponse
            {
                Records = new[] { rec, badRec },
                NextSince = 10,
                HasMore = false
            }));
        });

        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Capture:Sync:Enabled"] = "true",
            ["Capture:Sync:ServerUrl"] = "https://localhost:5001",
            ["Capture:Sync:MasterKeyPath"] = keyPath,
            ["Capture:Sync:StatePath"] = statePath
        }).Build();

        var loggerProvider = new TestLoggerProvider();
        var loggerFactory = LoggerFactory.Create(b => b.AddProvider(loggerProvider));
        var client = new CaptureSyncHttpClient(new HttpClient(handler), loggerFactory.CreateLogger<CaptureSyncHttpClient>());
        var runner = new CaptureSyncRunner(cache, client, config, TimeProvider.System, loggerFactory.CreateLogger<CaptureSyncRunner>());

        await runner.PullCycleAsync();

        Assert.NotEmpty(loggerProvider.Messages);
        foreach (var msg in loggerProvider.Messages)
        {
            Assert.DoesNotContain(masterKeyHex, msg, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("secret-refresh-token-xyz", msg, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("secret-access-token-abc", msg, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(sensitiveAccountId, msg, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(sensitivePeerId, msg, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(sensitiveValue, msg, StringComparison.OrdinalIgnoreCase);
        }
    }

    // TeamLead tekshiruvi (6b): server javobidagi null yozuv yoki null maydon
    // PullCycleAsync'ni NullReferenceException bilan yiqitmasligi kerak —
    // u boshqa buzilgan yozuvlar kabi o'tkazib yuboriladi, qolganlari
    // saqlanadi va kursor siljiydi (aks holda bitta yozuv pull'ni abadiy to'xtatadi).
    [Fact]
    public async Task P01_Null_record_or_null_fields_are_skipped_not_thrown()
    {
        var masterKey = new byte[32];
        RandomNumberGenerator.Fill(masterKey);

        var good = CreateSettingRecord(masterKey, "scope.whitelist", "[\"42\"]", "111222333", 100);
        var opts = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };
        var goodJson = JsonSerializer.Serialize(good, opts);
        var nullKindJson = JsonSerializer.Serialize(good with { Seq = 2 }, opts)
            .Replace("\"kind\":\"setting\"", "\"kind\":null");
        var nullHashesJson = JsonSerializer.Serialize(good with { Seq = 3 }, opts)
            .Replace($"\"account_hash\":\"{good.AccountHash}\"", "\"account_hash\":null")
            .Replace($"\"record_id\":\"{good.RecordId}\"", "\"record_id\":null");
        var nullBytesJson = JsonSerializer.Serialize(good with { Seq = 4 }, opts);
        nullBytesJson = System.Text.RegularExpressions.Regex.Replace(nullBytesJson, "\"payload\":\"[^\"]*\"", "\"payload\":null");
        Assert.Contains("\"kind\":null", nullKindJson);
        Assert.Contains("\"account_hash\":null", nullHashesJson);
        Assert.Contains("\"payload\":null", nullBytesJson);
        var page = $"{{\"records\":[{nullKindJson},null,{nullHashesJson},{nullBytesJson},{goodJson}],\"next_since\":40,\"has_more\":false}}";

        var dbPath = CreateTempFile("db");
        var cache = new MessageCache(dbPath);
        cache.Initialize();
        var keyPath = CreateTempFile("key");
        var statePath = CreateTempFile("json");
        DeviceCredentials.SaveMasterKey(keyPath, masterKey);
        DeviceCredentials.SaveDeviceState(statePath, new DeviceState("dev-1", "rt-1"));

        var handler = new MockHttpMessageHandler((req, ct) =>
        {
            if (req.RequestUri!.AbsolutePath.Contains("/refresh"))
            {
                return Task.FromResult(JsonOk(new
                {
                    refresh_token = "rt-1",
                    access_token = "at-1",
                    expires_at = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds()
                }));
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(page, Encoding.UTF8, "application/json")
            });
        });

        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Capture:Sync:Enabled"] = "true",
            ["Capture:Sync:ServerUrl"] = "https://localhost:5001",
            ["Capture:Sync:MasterKeyPath"] = keyPath,
            ["Capture:Sync:StatePath"] = statePath
        }).Build();

        var runner = new CaptureSyncRunner(cache, new CaptureSyncHttpClient(new HttpClient(handler)), config);

        var success = await runner.PullCycleAsync();

        Assert.True(success);
        Assert.Equal(4, runner.SkippedCount);
        Assert.Equal("[\"42\"]", cache.GetSyncedSetting("scope.whitelist")!.Value);
        Assert.Equal(40, cache.GetPullCursor("pull_cursor"));
    }

    private sealed class RecordingRunner(MessageCache cache, IConfiguration config, CancellationTokenSource stop, bool pushResult = true)
        : CaptureSyncRunner(cache, new CaptureSyncHttpClient(new HttpClient()), config)
    {
        public List<string> Calls { get; } = new();

        public override Task<bool> PushCycleAsync(CancellationToken ct = default)
        {
            Calls.Add("push");
            return Task.FromResult(pushResult);
        }

        public override Task<bool> PullCycleAsync(CancellationToken ct = default)
        {
            Calls.Add("pull");
            stop.Cancel();
            return Task.FromResult(true);
        }
    }

    // TeamLead tekshiruvi (6b): ishlab chiqarish sikli (CaptureSyncLoop) har
    // aylanishda push'dan keyin pull ham qilishi kerak — aks holda
    // runner'dagi butun pull kodi hech qachon chaqirilmaydi.
    [Fact]
    public async Task P02_Loop_runs_push_then_pull_each_cycle()
    {
        var dbPath = CreateTempFile("db");
        var cache = new MessageCache(dbPath);
        cache.Initialize();
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Capture:Sync:IntervalSeconds"] = "1"
        }).Build();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var runner = new RecordingRunner(cache, config, cts);
        var loop = new CaptureSyncLoop(runner, TimeProvider.System, config);

        await loop.RunLoopAsync(cts.Token);

        Assert.Equal(new[] { "push", "pull" }, runner.Calls);
    }

    // TeamLead tekshiruvi (6b): pull'da 401 kelsa token majburan yangilanadi
    // va shu sahifa bir marta qayta so'raladi (prompt §2).
    [Fact]
    public async Task P03_Pull_401_refreshes_token_and_retries_once()
    {
        var masterKey = new byte[32];
        RandomNumberGenerator.Fill(masterKey);
        var good = CreateSettingRecord(masterKey, "scope.whitelist", "[\"42\"]", "111222333", 100);

        var dbPath = CreateTempFile("db");
        var cache = new MessageCache(dbPath);
        cache.Initialize();
        var keyPath = CreateTempFile("key");
        var statePath = CreateTempFile("json");
        DeviceCredentials.SaveMasterKey(keyPath, masterKey);
        DeviceCredentials.SaveDeviceState(statePath, new DeviceState("dev-1", "rt-1"));

        int refreshes = 0;
        var pullTokens = new List<string?>();
        var handler = new MockHttpMessageHandler((req, ct) =>
        {
            if (req.RequestUri!.AbsolutePath.Contains("/refresh"))
            {
                refreshes++;
                return Task.FromResult(JsonOk(new
                {
                    refresh_token = $"rt-{refreshes + 1}",
                    access_token = $"at-{refreshes}",
                    expires_at = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds()
                }));
            }
            pullTokens.Add(req.Headers.Authorization?.Parameter);
            if (pullTokens.Count == 1)
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized));
            }
            return Task.FromResult(JsonOk(new PullResponse { Records = new[] { good }, NextSince = 7, HasMore = false }));
        });

        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Capture:Sync:Enabled"] = "true",
            ["Capture:Sync:ServerUrl"] = "https://localhost:5001",
            ["Capture:Sync:MasterKeyPath"] = keyPath,
            ["Capture:Sync:StatePath"] = statePath
        }).Build();

        var runner = new CaptureSyncRunner(cache, new CaptureSyncHttpClient(new HttpClient(handler)), config);

        Assert.True(await runner.PullCycleAsync());

        Assert.Equal(2, refreshes);
        Assert.Equal(new[] { "at-1", "at-2" }, pullTokens);
        Assert.Equal("[\"42\"]", cache.GetSyncedSetting("scope.whitelist")!.Value);
        Assert.Equal(7, cache.GetPullCursor("pull_cursor"));
    }

    // TeamLead tekshiruvi (6b): push backoff'siz muvaffaqiyatsiz bo'lsa ham
    // (masalan, butun partiya 400 bilan rad etilgan) pull bajariladi —
    // aks holda tiqilib qolgan push egasining sozlamalarini abadiy to'sadi.
    [Fact]
    public async Task P04_Push_failure_without_backoff_does_not_block_pull()
    {
        var dbPath = CreateTempFile("db");
        var cache = new MessageCache(dbPath);
        cache.Initialize();
        var config = new ConfigurationBuilder().Build();
        using var cts = new CancellationTokenSource();
        var runner = new RecordingRunner(cache, config, cts, pushResult: false);

        Assert.False(await runner.SyncCycleAsync());

        Assert.Equal(new[] { "push", "pull" }, runner.Calls);
    }
}
