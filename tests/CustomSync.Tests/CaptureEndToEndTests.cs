using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CustomSync.Api.Endpoints;
using CustomSync.Capture.Capture;
using CustomSync.Capture.Maintenance;
using CustomSync.Capture.Media;
using CustomSync.Capture.Sync;
using CustomSync.Core;
using CustomSync.Core.Contracts;
using CustomSync.Services;
using CustomSync.Tests.Fixtures;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CustomSync.Tests;

[Collection(CaptureContractCollection.Name)]
public class CaptureEndToEndTests : CaptureContractTestBase
{
    public CaptureEndToEndTests(CustomSyncWebApplicationFactory factory) : base(factory)
    {
    }

    // =========================================================================
    // SCENARIO 1 — Scope chain (tdesktop settings -> pull -> filter -> records)
    // =========================================================================
    [Fact]
    public async Task Scenario01_Scope_chain_tdesktop_settings_evaluated_server_block_wins_and_edit_filtered()
    {
        var v = LoadVectorCase1();
        var contentKey = SyncCrypto.DeriveContentKey(v.MasterKey);
        var peerKey = SyncCrypto.DerivePeerKey(v.MasterKey);
        var accountKey = SyncCrypto.DeriveAccountKey(v.MasterKey);
        const string accountId = "12345";

        var (tdClient, tdDevId, _) = await CreateEnrolledDeviceAsync("device");
        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        // 8 scope settings pushed by tdesktop device (§3.2.1)
        // Enabling AntiDelete for chats A (1001) and B (1002), leaving C (1003) unlisted.
        // Chat D (1004) has AntiDelete on but AntiEdit explicitly off.
        var settingValues = new Dictionary<string, string>
        {
            ["scope.whitelist"] = JsonSerializer.Serialize(new[] { "1001", "1002" }),
            ["scope.blacklist"] = "[]",
            ["scope.wl_categories"] = JsonSerializer.Serialize(new { user = false, group = false, channel = false }),
            ["scope.bl_categories"] = JsonSerializer.Serialize(new { user = false, group = false, channel = false }),
            ["scope.antidelete_global"] = "false",
            ["scope.antiedit_global"] = "false",
            ["scope.antidelete_per_peer"] = JsonSerializer.Serialize(new Dictionary<string, bool> { ["1004"] = true }),
            ["scope.antiedit_per_peer"] = JsonSerializer.Serialize(new Dictionary<string, bool> { ["1004"] = false })
        };

        var records = new List<SyncRecord>();
        int i = 0;
        foreach (var (key, val) in settingValues)
        {
            var accHash = CryptoPrimitives.ComputeAccountHash(accountKey, accountId);
            var pHash = CryptoPrimitives.ComputePeerHash(peerKey, "0");
            var msgId = ActivityMapper.DiscriminatorFor(key);
            var recId = RecordId.Compute("setting", accHash, pHash, msgId, now + i);
            var plaintext = JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, string>
            {
                ["key"] = key,
                ["value"] = val,
                ["account_id"] = accountId,
                ["peer_id"] = "0"
            });
            var (payload, nonce) = SyncCrypto.EncryptPayload(contentKey, plaintext);
            records.Add(new SyncRecord
            {
                RecordId = recId,
                Kind = "setting",
                AccountHash = accHash,
                PeerHash = pHash,
                MsgId = msgId,
                OccurredAt = now + i,
                ObservedAt = now + i,
                DeviceId = tdDevId,
                Nonce = nonce,
                Payload = payload
            });
            i++;
        }

        var startSeq = await GetCurrentMaxSeqAsync();
        var pushResp = await tdClient.PostAsJsonAsync("/api/v1/sync/push", new SyncEndpoints.PushRequest(records), JsonOptions);
        Assert.Equal(HttpStatusCode.OK, pushResp.StatusCode);

        // Capture harness: server blocks chat B ("1002"), DefaultEnabled = false
        await using var harness = await CreateCaptureHarnessAsync(
            masterKey: v.MasterKey,
            accountId: accountId,
            configureSettings: dict =>
            {
                dict["Capture:Scope:Block:0"] = "1002";
                dict["Capture:Scope:DefaultEnabled"] = "false";
            });

        // Pull settings into capture
        var pulled = await harness.Runner.PullCycleAsync();
        Assert.True(pulled);
        Assert.True(await WaitAsync(() => harness.SettingsSource.CurrentSnapshot != null));
        var snap = harness.SettingsSource.CurrentSnapshot;
        Assert.NotNull(snap);
        Assert.True(snap.Whitelist.Contains("1001"));
        Assert.True(snap.Whitelist.Contains("1002"));

        // Receive + delete in A (1001), B (1002), C (1003)
        void FeedMessage(long chatId, long msgId)
        {
            harness.Transport.PushUpdate($$"""
            {
              "@type": "updateNewMessage",
              "message": {
                "@type": "message",
                "id": {{msgId << 20}},
                "chat_id": {{chatId}},
                "date": {{now}},
                "sender_id": { "@type": "messageSenderUser", "user_id": {{chatId}} },
                "is_outgoing": false,
                "content": { "@type": "messageText", "text": { "text": "hello" } }
              }
            }
            """);
        }

        void FeedDelete(long chatId, long msgId)
        {
            harness.Transport.PushUpdate($$"""
            {
              "@type": "updateDeleteMessages",
              "chat_id": {{chatId}},
              "message_ids": [{{msgId << 20}}],
              "is_permanent": true,
              "from_cache": false
            }
            """);
        }

        // Chat A (1001) - in whitelist, AntiDelete on -> should be captured
        FeedMessage(1001, 11);
        FeedDelete(1001, 11);

        // Chat B (1002) - in whitelist, but blocked by server block -> blocked
        FeedMessage(1002, 12);
        FeedDelete(1002, 12);

        // Chat C (1003) - unlisted, DefaultEnabled false -> blocked
        FeedMessage(1003, 13);
        FeedDelete(1003, 13);

        // Chat D (1004) - AntiEdit off -> edit produces no record
        FeedMessage(1004, 14);
        harness.Transport.PushUpdate($$"""
        {
          "@type": "updateMessageContent",
          "chat_id": 1004,
          "message_id": {{14 << 20}},
          "new_content": { "@type": "messageText", "text": { "text": "edited text" } }
        }
        """);
        harness.Transport.PushUpdate($$"""
        {
          "@type": "updateMessageEdited",
          "chat_id": 1004,
          "message_id": {{14 << 20}},
          "edit_date": {{now + 10}}
        }
        """);

        // Update'lar kelish tartibida ketma-ket qayta ishlanadi: oxirgi
        // (sentinel) xabar keshda ko'rinsa, B, C va D ham qayta ishlangan.
        // Aks holda A ning qatori paydo bo'lishi bilan quyidagi "yozilmadi"
        // tekshiruvlari B/C/D hali qayta ishlanmay turib o'tib ketishi mumkin.
        FeedMessage(1001, 15);
        Assert.True(await WaitAsync(() => harness.Cache.Get(1001, 15) != null));

        // Exactly 1 deleted row in outbox (chat A) and 0 edited rows
        Assert.True(await WaitAsync(() => harness.Cache.GetOutboxRows("deleted").Count == 1));
        Assert.Empty(harness.Cache.GetOutboxRows("edited"));

        // Push cycle
        var pushed = await harness.Runner.PushCycleAsync();
        Assert.True(pushed);
        Assert.Equal(1, harness.Runner.PushedCount);

        // Server check: exactly one non-setting record created (Chat A)
        var (adminClient, _) = await CreateAdminClientAsync();
        var pullResp = await adminClient.GetFromJsonAsync<PullResponse>($"/api/v1/sync/pull?since={startSeq}&limit=100", JsonOptions);
        Assert.NotNull(pullResp);
        var nonSettingRecords = pullResp.Records.Where(r => r.Kind != "setting").ToList();
        var singleRecord = Assert.Single(nonSettingRecords);
        Assert.Equal("deleted", singleRecord.Kind);
        Assert.Equal(CryptoPrimitives.ComputePeerHash(peerKey, "1001"), singleRecord.PeerHash);
        Assert.Equal(11, singleRecord.MsgId);
    }

    // =========================================================================
    // SCENARIO 2 — One event, one record (dedup across peer types and edits)
    // =========================================================================
    [Fact]
    public async Task Scenario02_One_event_one_record_dedup_across_peer_types_and_edits()
    {
        var v = LoadVectorCase1();
        var contentKey = SyncCrypto.DeriveContentKey(v.MasterKey);
        var peerKey = SyncCrypto.DerivePeerKey(v.MasterKey);
        var accountKey = SyncCrypto.DeriveAccountKey(v.MasterKey);
        const string accountId = "12345";

        var (tdClient, tdDevId, _) = await CreateEnrolledDeviceAsync("device");
        long occurredAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 300;
        long tEarly = occurredAt + 5;
        long tLate = occurredAt + 50;

        // 3 chat types: user (shift 0), basic group (shift 1), supergroup (shift 2)
        var cases = new (long ChatId, long MsgId, string Label)[]
        {
            (123456L, 201L, "user"),
            (-67890L, 202L, "group"),
            (-1001234567890L, 203L, "channel")
        };

        var tdRecords = new List<SyncRecord>();
        foreach (var (chatId, msgId, _) in cases)
        {
            long canonicalPeerId = ComputeCanonicalPeerId(chatId);
            string peerIdStr = canonicalPeerId.ToString(CultureInfo.InvariantCulture);
            string accHash = CryptoPrimitives.ComputeAccountHash(accountKey, accountId);
            string pHash = CryptoPrimitives.ComputePeerHash(peerKey, peerIdStr);
            string recId = RecordId.Compute("deleted", accHash, pHash, msgId, occurredAt);

            var payloadBytes = JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, object?>
            {
                ["account_id"] = accountId,
                ["peer_id"] = peerIdStr,
                ["text"] = "tdesktop text",
                ["date"] = occurredAt
            });
            var (wirePayload, nonce) = SyncCrypto.EncryptPayload(contentKey, payloadBytes);

            tdRecords.Add(new SyncRecord
            {
                RecordId = recId,
                Kind = "deleted",
                AccountHash = accHash,
                PeerHash = pHash,
                MsgId = msgId,
                OccurredAt = occurredAt,
                ObservedAt = tEarly,
                DeviceId = tdDevId,
                Nonce = nonce,
                Payload = wirePayload
            });
        }

        // tdesktop pushes deleted records first (earlier observed_at)
        var startSeq = await GetCurrentMaxSeqAsync();
        var pushResp = await tdClient.PostAsJsonAsync("/api/v1/sync/push", new SyncEndpoints.PushRequest(tdRecords), JsonOptions);
        Assert.Equal(HttpStatusCode.OK, pushResp.StatusCode);

        // Capture harness observes with later observed_at
        var clock = new TestTimeProvider(DateTimeOffset.FromUnixTimeSeconds(tLate));
        await using var harness = await CreateCaptureHarnessAsync(timeProvider: clock, masterKey: v.MasterKey, accountId: accountId);

        foreach (var (chatId, msgId, _) in cases)
        {
            harness.Transport.PushUpdate($$"""
            {
              "@type": "updateNewMessage",
              "message": {
                "@type": "message",
                "id": {{msgId << 20}},
                "chat_id": {{chatId}},
                "date": {{occurredAt}},
                "sender_id": { "@type": "messageSenderUser", "user_id": 999 },
                "is_outgoing": false,
                "content": { "@type": "messageText", "text": { "text": "capture text" } }
              }
            }
            """);

            harness.Transport.PushUpdate($$"""
            {
              "@type": "updateDeleteMessages",
              "chat_id": {{chatId}},
              "message_ids": [{{msgId << 20}}],
              "is_permanent": true,
              "from_cache": false
            }
            """);
        }

        Assert.True(await WaitAsync(() => harness.Cache.GetOutboxRows("deleted").Count == 3));
        var pushOk = await harness.Runner.PushCycleAsync();
        Assert.True(pushOk);
        Assert.Equal(3, harness.Runner.DuplicateCount);

        // Edited event: supergroup, occurred_at must be edit_date (§3.1)
        long editChatId = -1001234567890L;
        long editMsgId = 204L;
        long editDate = occurredAt + 30;

        long editCanonicalPeer = ComputeCanonicalPeerId(editChatId);
        string editPeerStr = editCanonicalPeer.ToString(CultureInfo.InvariantCulture);
        string editAccHash = CryptoPrimitives.ComputeAccountHash(accountKey, accountId);
        string editPeerHash = CryptoPrimitives.ComputePeerHash(peerKey, editPeerStr);
        string editRecId = RecordId.Compute("edited", editAccHash, editPeerHash, editMsgId, editDate);

        var editPayloadBytes = JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, object?>
        {
            ["account_id"] = accountId,
            ["peer_id"] = editPeerStr,
            ["text"] = "tdesktop edited",
            ["date"] = editDate
        });
        var (editWirePayload, editNonce) = SyncCrypto.EncryptPayload(contentKey, editPayloadBytes);

        var tdEditRecord = new SyncRecord
        {
            RecordId = editRecId,
            Kind = "edited",
            AccountHash = editAccHash,
            PeerHash = editPeerHash,
            MsgId = editMsgId,
            OccurredAt = editDate,
            ObservedAt = tEarly,
            DeviceId = tdDevId,
            Nonce = editNonce,
            Payload = editWirePayload
        };

        var editPushResp = await tdClient.PostAsJsonAsync("/api/v1/sync/push", new SyncEndpoints.PushRequest(new[] { tdEditRecord }), JsonOptions);
        Assert.Equal(HttpStatusCode.OK, editPushResp.StatusCode);

        // Capture observes the edit
        harness.Transport.PushUpdate($$"""
        {
          "@type": "updateNewMessage",
          "message": {
            "@type": "message",
            "id": {{editMsgId << 20}},
            "chat_id": {{editChatId}},
            "date": {{occurredAt}},
            "sender_id": { "@type": "messageSenderUser", "user_id": 999 },
            "is_outgoing": false,
            "content": { "@type": "messageText", "text": { "text": "original text" } }
          }
        }
        """);
        harness.Transport.PushUpdate($$"""
        {
          "@type": "updateMessageContent",
          "chat_id": {{editChatId}},
          "message_id": {{editMsgId << 20}},
          "new_content": { "@type": "messageText", "text": { "text": "capture edited text" } }
        }
        """);
        harness.Transport.PushUpdate($$"""
        {
          "@type": "updateMessageEdited",
          "chat_id": {{editChatId}},
          "message_id": {{editMsgId << 20}},
          "edit_date": {{editDate}}
        }
        """);

        Assert.True(await WaitAsync(() => harness.Cache.GetOutboxRows("edited").Count == 1));
        var editPushOk = await harness.Runner.PushCycleAsync();
        Assert.True(editPushOk);
        Assert.Equal(4, harness.Runner.DuplicateCount);

        // Verify on server: exactly 1 record per event (total 4), and stored payload peer_id and account_id match
        var (adminClient, _) = await CreateAdminClientAsync();
        var pull = await adminClient.GetFromJsonAsync<PullResponse>($"/api/v1/sync/pull?since={startSeq}&limit=100", JsonOptions);
        Assert.NotNull(pull);
        Assert.Equal(4, pull.Records.Count);

        foreach (var rec in pull.Records)
        {
            var decryptedPayload = SyncCrypto.DecryptPayload(contentKey, rec.Nonce, rec.Payload);
            using var doc = JsonDocument.Parse(decryptedPayload);
            var root = doc.RootElement;
            Assert.Equal(accountId, root.GetProperty("account_id").GetString());

            // Check peer_id matches canonical peer id
            var expectedPeerStr = rec.MsgId switch
            {
                201L => ComputeCanonicalPeerId(123456L).ToString(CultureInfo.InvariantCulture),
                202L => ComputeCanonicalPeerId(-67890L).ToString(CultureInfo.InvariantCulture),
                203L => ComputeCanonicalPeerId(-1001234567890L).ToString(CultureInfo.InvariantCulture),
                204L => ComputeCanonicalPeerId(-1001234567890L).ToString(CultureInfo.InvariantCulture),
                _ => throw new InvalidOperationException()
            };
            Assert.Equal(expectedPeerStr, root.GetProperty("peer_id").GetString());
        }
    }

    // =========================================================================
    // SCENARIO 3 — Media of a deleted message (survives, deduped on second delete)
    // =========================================================================
    [Fact]
    public async Task Scenario03_Media_of_deleted_message_survives_and_second_delete_reuses_with_head_200()
    {
        var v = LoadVectorCase1();
        var tempFile = TempPath(".bin");
        var fileBytes = Encoding.UTF8.GetBytes($"sample unique media bytes {Guid.NewGuid():N}");
        await File.WriteAllBytesAsync(tempFile, fileBytes);
        var sha256 = Convert.ToHexString(SHA256.HashData(fileBytes)).ToLowerInvariant();
        const int fileId = 777;

        long chatId = 12345000L + Random.Shared.Next(1, 100000);
        long msgId1 = 30100L + Random.Shared.Next(1, 10000);
        long msgId2 = msgId1 + 1;
        long occurredAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 300;

        await using var harness = await CreateCaptureHarnessAsync(
            masterKey: v.MasterKey,
            configureSettings: dict =>
            {
                dict["Capture:Media:Enabled"] = "true";
                dict["Capture:Media:PeerIds:0"] = chatId.ToString(CultureInfo.InvariantCulture);
            });

        // Set up transport reply for getMessage and downloadFile
        harness.Transport.Reply = req =>
        {
            var type = req["@type"]?.GetValue<string>();
            if (type == "getMe")
            {
                return new JsonObject { ["@type"] = "user", ["id"] = 12345L, ["first_name"] = "Me" };
            }
            if (type == "getMessage")
            {
                var msgId = req["message_id"]?.GetValue<long>() ?? 0;
                var chatId = req["chat_id"]?.GetValue<long>() ?? 0;
                return new JsonObject
                {
                    ["@type"] = "message",
                    ["id"] = msgId,
                    ["chat_id"] = chatId,
                    ["content"] = new JsonObject
                    {
                        ["@type"] = "messageDocument",
                        ["document"] = new JsonObject
                        {
                            ["document"] = new JsonObject
                            {
                                ["id"] = fileId,
                                ["size"] = fileBytes.Length,
                                ["expected_size"] = fileBytes.Length,
                                ["local"] = new JsonObject
                                {
                                    ["path"] = "",
                                    ["is_downloading_completed"] = false
                                }
                            }
                        }
                    }
                };
            }
            if (type == "downloadFile")
            {
                return new JsonObject
                {
                    ["@type"] = "file",
                    ["id"] = fileId,
                    ["size"] = fileBytes.Length,
                    ["expected_size"] = fileBytes.Length,
                    ["local"] = new JsonObject
                    {
                        ["path"] = tempFile,
                        ["is_downloading_completed"] = true
                    }
                };
            }
            return null;
        };

        // 1. First message arrives
        harness.Transport.PushUpdate($$"""
        {
          "@type": "updateNewMessage",
          "message": {
            "@type": "message",
            "id": {{msgId1 << 20}},
            "chat_id": {{chatId}},
            "date": {{occurredAt}},
            "sender_id": { "@type": "messageSenderUser", "user_id": {{chatId}} },
            "is_outgoing": false,
            "content": {
              "@type": "messageDocument",
              "document": {
                "document": {
                  "id": {{fileId}},
                  "size": {{fileBytes.Length}},
                  "expected_size": {{fileBytes.Length}},
                  "local": { "path": "", "is_downloading_completed": false }
                }
              }
            }
          }
        }
        """);

        Assert.True(await WaitAsync(() => harness.Cache.GetNextDuePendingMedia(DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 10) != null));
        var downloader = harness.Provider.GetRequiredService<MediaDownloader>();
        var dlOk1 = await downloader.ProcessPendingOnceAsync();
        Assert.True(dlOk1);

        // Delete first message
        harness.Transport.PushUpdate($$"""
        {
          "@type": "updateDeleteMessages",
          "chat_id": {{chatId}},
          "message_ids": [{{msgId1 << 20}}],
          "is_permanent": true,
          "from_cache": false
        }
        """);

        Assert.True(await WaitAsync(() => harness.Cache.GetOutboxRows("deleted").Count == 1));
        var pushOk1 = await harness.Runner.PushCycleAsync();
        Assert.True(pushOk1);
        Assert.Equal(1, harness.Runner.PushedCount);

        // Second device GET /api/v1/media/{hash}
        var (adminClient, _) = await CreateAdminClientAsync();
        var getResp = await adminClient.GetAsync($"/api/v1/media/{sha256}");
        Assert.Equal(HttpStatusCode.OK, getResp.StatusCode);
        var xNonce = getResp.Headers.GetValues("X-Nonce").FirstOrDefault();
        Assert.NotNull(xNonce);
        var wireBlob = await getResp.Content.ReadAsByteArrayAsync();

        // Decrypt and verify matching plaintext and plaintext hash (spec §0.5)
        var mediaKey = SyncCrypto.DeriveMediaKey(v.MasterKey);
        var decryptedBytes = SyncCrypto.DecryptPayload(mediaKey, Convert.FromBase64String(xNonce), wireBlob);
        Assert.Equal(fileBytes, decryptedBytes);
        Assert.Equal(sha256, Convert.ToHexString(SHA256.HashData(decryptedBytes)).ToLowerInvariant());

        harness.Transport.PushUpdate($$"""
        {
          "@type": "updateNewMessage",
          "message": {
            "@type": "message",
            "id": {{msgId2 << 20}},
            "chat_id": {{chatId}},
            "date": {{occurredAt + 10}},
            "sender_id": { "@type": "messageSenderUser", "user_id": {{chatId}} },
            "is_outgoing": false,
            "content": {
              "@type": "messageDocument",
              "document": {
                "document": {
                  "id": {{fileId}},
                  "size": {{fileBytes.Length}},
                  "expected_size": {{fileBytes.Length}},
                  "local": { "path": "", "is_downloading_completed": false }
                }
              }
            }
          }
        }
        """);

        Assert.True(await WaitAsync(() => harness.Cache.GetNextDuePendingMedia(DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 10) != null));
        var dlOk2 = await downloader.ProcessPendingOnceAsync();
        Assert.True(dlOk2);

        harness.Transport.PushUpdate($$"""
        {
          "@type": "updateDeleteMessages",
          "chat_id": {{chatId}},
          "message_ids": [{{msgId2 << 20}}],
          "is_permanent": true,
          "from_cache": false
        }
        """);

        Assert.True(await WaitAsync(() => harness.Cache.GetOutboxRows("deleted").Count == 1));

        int reqBefore = harness.CountingHandler.RequestCount;
        var pushOk2 = await harness.Runner.PushCycleAsync();
        Assert.True(pushOk2);
        Assert.Equal(2, harness.Runner.PushedCount);

        // Runner does: HEAD (200), PUSH (no PUT)
        int reqCount = harness.CountingHandler.RequestCount - reqBefore;
        Assert.Equal(2, reqCount);

        // Verify media row status is "uploaded"
        var mRow2 = harness.Cache.GetCapturedMedia(chatId.ToString(), msgId2);
        Assert.NotNull(mRow2);
        Assert.Equal("uploaded", mRow2.Status);
    }

    // =========================================================================
    // SCENARIO 4 — Media limits (413 pushes without media, 507 defers outbox)
    // =========================================================================
    [Fact]
    public async Task Scenario04_Media_limits_413_pushes_without_media_and_507_defers_outbox()
    {
        var v = LoadVectorCase1();
        using var scope = _factory.Services.CreateScope();
        var settings = scope.ServiceProvider.GetRequiredService<SettingsService>();

        var oldMaxUpload = await settings.GetIntAsync("media.max_upload_bytes");
        var oldQuotaTotal = await settings.GetIntAsync("storage.quota_total_mb");
        var oldQuotaPerDev = await settings.GetIntAsync("storage.quota_per_device_mb");

        try
        {
            // Sub-case A: 413 Payload Too Large
            // media.max_upload_bytes = 10, file size = 50
            await settings.SetAsync("media.max_upload_bytes", "10");

            var fileA = TempPath(".bin");
            var bytesA = Encoding.UTF8.GetBytes("this content is 50 bytes long for testing limit 413");
            await File.WriteAllBytesAsync(fileA, bytesA);
            const int fileIdA = 801;

            await using var harnessA = await CreateCaptureHarnessAsync(
                masterKey: v.MasterKey,
                configureSettings: dict =>
                {
                    dict["Capture:Media:Enabled"] = "true";
                    dict["Capture:Media:PeerIds:0"] = "1001";
                });

            harnessA.Transport.Reply = req =>
            {
                var type = req["@type"]?.GetValue<string>();
                if (type == "getMe") return new JsonObject { ["@type"] = "user", ["id"] = 12345L, ["first_name"] = "Me" };
                if (type == "getMessage")
                {
                    return new JsonObject
                    {
                        ["@type"] = "message",
                        ["id"] = 401L << 20,
                        ["chat_id"] = 1001L,
                        ["content"] = new JsonObject
                        {
                            ["@type"] = "messageDocument",
                            ["document"] = new JsonObject
                            {
                                ["document"] = new JsonObject
                                {
                                    ["id"] = fileIdA,
                                    ["size"] = bytesA.Length,
                                    ["expected_size"] = bytesA.Length,
                                    ["local"] = new JsonObject { ["path"] = "", ["is_downloading_completed"] = false }
                                }
                            }
                        }
                    };
                }
                if (type == "downloadFile")
                {
                    return new JsonObject
                    {
                        ["@type"] = "file",
                        ["id"] = fileIdA,
                        ["size"] = bytesA.Length,
                        ["expected_size"] = bytesA.Length,
                        ["local"] = new JsonObject { ["path"] = fileA, ["is_downloading_completed"] = true }
                    };
                }
                return null;
            };

            harnessA.Transport.PushUpdate($$"""
            {
              "@type": "updateNewMessage",
              "message": {
                "@type": "message",
                "id": {{401L << 20}},
                "chat_id": 1001,
                "date": 1000,
                "sender_id": { "@type": "messageSenderUser", "user_id": 1001 },
                "is_outgoing": false,
                "content": {
                  "@type": "messageDocument",
                  "document": {
                    "document": {
                      "id": {{fileIdA}},
                      "size": {{bytesA.Length}},
                      "expected_size": {{bytesA.Length}},
                      "local": { "path": "", "is_downloading_completed": false }
                    }
                  }
                }
              }
            }
            """);

            Assert.True(await WaitAsync(() => harnessA.Cache.GetNextDuePendingMedia(DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 10) != null));
            var downloaderA = harnessA.Provider.GetRequiredService<MediaDownloader>();
            var dlOkA = await downloaderA.ProcessPendingOnceAsync();
            Assert.True(dlOkA);

            harnessA.Transport.PushUpdate($$"""
            {
              "@type": "updateDeleteMessages",
              "chat_id": 1001,
              "message_ids": [{{401L << 20}}],
              "is_permanent": true,
              "from_cache": false
            }
            """);

            Assert.True(await WaitAsync(() => harnessA.Cache.GetOutboxRows("deleted").Count == 1));

            var pushA = await harnessA.Runner.PushCycleAsync();
            Assert.True(pushA);

            // Record is pushed without media: outbox row deleted, media row marked skipped
            Assert.Empty(harnessA.Cache.GetOutboxRows());
            var mediaRowA = harnessA.Cache.GetCapturedMedia("1001", 401);
            Assert.NotNull(mediaRowA);
            Assert.Equal("skipped", mediaRowA.Status);

            // Sub-case B: 507 Insufficient Storage
            // Reset max_upload to large, set quota_total_mb to 1, upload 2MB file
            await settings.SetAsync("media.max_upload_bytes", "52428800");
            await settings.SetAsync("storage.quota_total_mb", "1");

            var fileB = TempPath(".bin");
            var bytesB = new byte[2 * 1024 * 1024]; // 2MB
            RandomNumberGenerator.Fill(bytesB);
            await File.WriteAllBytesAsync(fileB, bytesB);
            const int fileIdB = 802;

            await using var harnessB = await CreateCaptureHarnessAsync(
                masterKey: v.MasterKey,
                configureSettings: dict =>
                {
                    dict["Capture:Media:Enabled"] = "true";
                    dict["Capture:Media:PeerIds:0"] = "1001";
                });

            harnessB.Transport.Reply = req =>
            {
                var type = req["@type"]?.GetValue<string>();
                if (type == "getMe") return new JsonObject { ["@type"] = "user", ["id"] = 12345L, ["first_name"] = "Me" };
                if (type == "getMessage")
                {
                    return new JsonObject
                    {
                        ["@type"] = "message",
                        ["id"] = 402L << 20,
                        ["chat_id"] = 1001L,
                        ["content"] = new JsonObject
                        {
                            ["@type"] = "messageDocument",
                            ["document"] = new JsonObject
                            {
                                ["document"] = new JsonObject
                                {
                                    ["id"] = fileIdB,
                                    ["size"] = bytesB.Length,
                                    ["expected_size"] = bytesB.Length,
                                    ["local"] = new JsonObject { ["path"] = "", ["is_downloading_completed"] = false }
                                }
                            }
                        }
                    };
                }
                if (type == "downloadFile")
                {
                    return new JsonObject
                    {
                        ["@type"] = "file",
                        ["id"] = fileIdB,
                        ["size"] = bytesB.Length,
                        ["expected_size"] = bytesB.Length,
                        ["local"] = new JsonObject { ["path"] = fileB, ["is_downloading_completed"] = true }
                    };
                }
                return null;
            };

            harnessB.Transport.PushUpdate($$"""
            {
              "@type": "updateNewMessage",
              "message": {
                "@type": "message",
                "id": {{402L << 20}},
                "chat_id": 1001,
                "date": 1000,
                "sender_id": { "@type": "messageSenderUser", "user_id": 1001 },
                "is_outgoing": false,
                "content": {
                  "@type": "messageDocument",
                  "document": {
                    "document": {
                      "id": {{fileIdB}},
                      "size": {{bytesB.Length}},
                      "expected_size": {{bytesB.Length}},
                      "local": { "path": "", "is_downloading_completed": false }
                    }
                  }
                }
              }
            }
            """);

            Assert.True(await WaitAsync(() => harnessB.Cache.GetNextDuePendingMedia(DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 10) != null));
            var downloaderB = harnessB.Provider.GetRequiredService<MediaDownloader>();
            var dlOkB = await downloaderB.ProcessPendingOnceAsync();
            Assert.True(dlOkB);

            harnessB.Transport.PushUpdate($$"""
            {
              "@type": "updateDeleteMessages",
              "chat_id": 1001,
              "message_ids": [{{402L << 20}}],
              "is_permanent": true,
              "from_cache": false
            }
            """);

            Assert.True(await WaitAsync(() => harnessB.Cache.GetOutboxRows("deleted").Count == 1));

            var pushB = await harnessB.Runner.PushCycleAsync();
            Assert.True(pushB); // cycle completes gracefully

            // Outbox row kept for retry, nothing pushed without media
            var rowsB = harnessB.Cache.GetOutboxRows();
            Assert.Single(rowsB);
            Assert.Equal(402, rowsB[0].MsgId);
        }
        finally
        {
            await settings.SetAsync("media.max_upload_bytes", oldMaxUpload.ToString(CultureInfo.InvariantCulture));
            await settings.SetAsync("storage.quota_total_mb", oldQuotaTotal.ToString(CultureInfo.InvariantCulture));
            await settings.SetAsync("storage.quota_per_device_mb", oldQuotaPerDev.ToString(CultureInfo.InvariantCulture));
        }
    }

    // =========================================================================
    // SCENARIO 5 — Health from registered maintenance reported to admin API
    // =========================================================================
    [Fact]
    public async Task Scenario05_Health_from_registered_maintenance_reported_and_visible_in_admin_api()
    {
        var v = LoadVectorCase1();
        await using var harness = await CreateCaptureHarnessAsync(masterKey: v.MasterKey);

        // Maintenance TDLib'dan `optimizeStorage` (120 s timeout) va
        // `getStorageStatisticsFast` (30 s) so'raydi. Javobsiz test ikkala
        // timeout'ni kutib 2.5 daqiqa yurardi va TDLib hajmlari hisobotga
        // yetishini umuman tekshirmasdi.
        harness.Transport.Reply = req => req["@type"]?.GetValue<string>() switch
        {
            "optimizeStorage" => new JsonObject { ["@type"] = "storageStatistics", ["size"] = 0, ["count"] = 0 },
            "getStorageStatisticsFast" => new JsonObject
            {
                ["@type"] = "storageStatisticsFast",
                ["files_size"] = 7340032,
                ["database_size"] = 1048576
            },
            _ => null
        };

        var maintenance = harness.Provider.GetRequiredService<StorageMaintenance>();
        await maintenance.RunOnceAsync();
        var snapshot = maintenance.LatestSnapshot;
        Assert.NotNull(snapshot);

        var (adminClient, _) = await CreateAdminClientAsync();
        var getResp = await adminClient.GetAsync("/api/v1/devices/health");
        Assert.Equal(HttpStatusCode.OK, getResp.StatusCode);

        var list = await getResp.Content.ReadFromJsonAsync<List<JsonElement>>();
        Assert.NotNull(list);

        var item = list.FirstOrDefault(x => x.GetProperty("device_id").GetString() == harness.DeviceId);
        Assert.True(item.ValueKind != JsonValueKind.Undefined, "Capture device not found in health response");

        Assert.Equal(snapshot.ProcessRssBytes, item.GetProperty("rss_bytes").GetInt64());
        Assert.Equal(snapshot.CacheDatabaseBytes, item.GetProperty("cache_db_bytes").GetInt64());
        Assert.Equal(snapshot.MediaStoreBytes, item.GetProperty("media_store_bytes").GetInt64());
        Assert.Equal(snapshot.MediaStoreFiles, item.GetProperty("media_store_files").GetInt32());
        Assert.Equal(7340032, item.GetProperty("tdlib_files_bytes").GetInt64());
        Assert.Equal(1048576, item.GetProperty("tdlib_database_bytes").GetInt64());
    }
}
