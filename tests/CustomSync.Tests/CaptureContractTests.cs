using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
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
using Microsoft.Extensions.Logging;
using Xunit;

namespace CustomSync.Tests;

[CollectionDefinition(Name, DisableParallelization = true)]
public class CaptureContractCollection : ICollectionFixture<CustomSyncWebApplicationFactory>
{
    public const string Name = "CaptureContractCollection";
}

[Collection(CaptureContractCollection.Name)]
public class CaptureContractTests : CaptureContractTestBase
{
    public CaptureContractTests(CustomSyncWebApplicationFactory factory) : base(factory)
    {
    }

    // =========================================================================
    // SCENARIO 1 — Enroll
    // =========================================================================
    [Fact]
    public async Task Scenario01_Enroll_redeems_code_persists_state_and_refuses_second_enrollment()
    {
        using var scope = _factory.Services.CreateScope();
        var devices = scope.ServiceProvider.GetRequiredService<DeviceService>();
        var jwt = scope.ServiceProvider.GetRequiredService<JwtIssuer>();

        var code = await devices.CreateEnrollmentCodeAsync(DeviceService.RoleDevice);
        var deviceName = $"cap-srv-{Guid.NewGuid():N}";
        var dir = TempDir();
        var statePath = Path.Combine(dir, "device-state.json");

        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Capture:Sync:ServerUrl"] = "http://localhost",
            ["Capture:Sync:StatePath"] = statePath,
        }).Build();

        var prompt = new DynamicConsolePrompt((msg, _) =>
        {
            if (msg.Contains("code")) return code;
            if (msg.Contains("Device name")) return deviceName;
            return null;
        });

        using var client = _factory.CreateClient();
        var exitCode = await SyncCliCommands.EnrollAsync(config, prompt, client);
        Assert.Equal(0, exitCode);

        // State fayl tekshiruvi: server'ning device_id va refresh_token maydonlari saqlangan
        var state = DeviceCredentials.LoadDeviceState(statePath);
        Assert.NotNull(state);
        Assert.NotEmpty(state.DeviceId);
        Assert.NotEmpty(state.RefreshToken);

        // Admin GET /api/v1/devices tekshiruvi: nom, platforma 'service', rol 'device'
        var (adminToken, _) = await jwt.IssueAsync($"admin-{Guid.NewGuid():N}", DeviceService.RoleAdmin);
        using var adminClient = _factory.CreateClient();
        adminClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", adminToken);

        var listResp = await adminClient.GetAsync("/api/v1/devices");
        Assert.Equal(HttpStatusCode.OK, listResp.StatusCode);
        var deviceList = await listResp.Content.ReadFromJsonAsync<List<JsonElement>>();
        Assert.NotNull(deviceList);

        var found = deviceList.FirstOrDefault(d => d.GetProperty("device_id").GetString() == state.DeviceId);
        Assert.True(found.ValueKind != JsonValueKind.Undefined, "Yangi enroll qilingan qurilma admin ro'yxatida topilmadi");
        Assert.Equal(deviceName, found.GetProperty("name").GetString());
        Assert.Equal("service", found.GetProperty("platform").GetString());
        Assert.Equal(DeviceService.RoleDevice, found.GetProperty("role").GetString());

        // Ishlatilgan kod ikkinchi enrollment'da qat'iy rad etiladi
        var secondPrompt = new DynamicConsolePrompt((msg, _) =>
        {
            if (msg.Contains("code")) return code;
            if (msg.Contains("Device name")) return "second-attempt";
            return null;
        });
        var secondExitCode = await SyncCliCommands.EnrollAsync(config, secondPrompt, client);
        Assert.Equal(1, secondExitCode);
    }

    // =========================================================================
    // SCENARIO 2 — Key setup
    // =========================================================================
    [Fact]
    public async Task Scenario02_KeySetup_downloads_wrap_unwraps_master_key_and_rejects_wrong_password()
    {
        var v = LoadVectorCase1();

        // 1. "tdesktop" admin qurilmasi kalit o'ramini POST /api/v1/keys/wraps orqali yuklaydi
        var (tdesktopClient, _, _) = await CreateEnrolledDeviceAsync(role: DeviceService.RoleAdmin);
        var wrapLabel = $"wrap-{Guid.NewGuid():N}";
        var postWrapResp = await tdesktopClient.PostAsJsonAsync("/api/v1/keys/wraps", new
        {
            wrap_type = "passphrase",
            label = wrapLabel,
            salt = v.Salt,
            nonce = v.Nonce,
            wrapped_key = v.WrappedKey,
            iterations = v.Iterations
        });
        Assert.Equal(HttpStatusCode.OK, postWrapResp.StatusCode);
        var postWrapBody = await postWrapResp.Content.ReadFromJsonAsync<JsonElement>();
        var wrapId = postWrapBody.GetProperty("wrap_id").GetString();
        Assert.NotNull(wrapId);

        // 2. Capture qurilmasini yaratish va enroll qilish
        var dir = TempDir();
        var statePath = Path.Combine(dir, "device-state.json");
        var keyPath = Path.Combine(dir, "master.key");

        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Capture:Sync:ServerUrl"] = "http://localhost",
            ["Capture:Sync:StatePath"] = statePath,
            ["Capture:Sync:MasterKeyPath"] = keyPath,
        }).Build();

        using var scope = _factory.Services.CreateScope();
        var devices = scope.ServiceProvider.GetRequiredService<DeviceService>();
        var code = await devices.CreateEnrollmentCodeAsync(DeviceService.RoleDevice);
        using var httpClient = _factory.CreateClient();

        var enrollPrompt = new DynamicConsolePrompt((msg, _) => msg.Contains("code") ? code : "capture-dev");
        var enrollExit = await SyncCliCommands.EnrollAsync(config, enrollPrompt, httpClient);
        Assert.Equal(0, enrollExit);

        // 3. Noto'g'ri parol: hech qanday kalit fayli yozilmaydi
        var wrongPrompt = new DynamicConsolePrompt((msg, _) =>
        {
            if (msg.Contains("wrap")) return wrapLabel;
            if (msg.Contains("Passphrase", StringComparison.OrdinalIgnoreCase)) return v.WrongPassphrase;
            return "y";
        });

        var wrongExit = await SyncCliCommands.SetKeyAsync(config, wrongPrompt, httpClient);
        Assert.Equal(1, wrongExit);
        Assert.False(File.Exists(keyPath));

        // 4. To'g'ri parol: vektor master kaliti bilan bir xil kalit fayli yoziladi (Linux'da 0600)
        var correctPrompt = new DynamicConsolePrompt((msg, _) =>
        {
            if (msg.Contains("wrap")) return wrapLabel;
            if (msg.Contains("Passphrase", StringComparison.OrdinalIgnoreCase)) return v.Passphrase;
            if (msg.Contains("Does this match")) return "y";
            if (msg.Contains("replace")) return "y";
            return "y";
        });

        var correctExit = await SyncCliCommands.SetKeyAsync(config, correctPrompt, httpClient);
        Assert.Equal(0, correctExit);
        Assert.True(File.Exists(keyPath));

        var loadedKey = DeviceCredentials.LoadMasterKey(keyPath);
        Assert.NotNull(loadedKey);
        Assert.Equal(v.MasterHex, Convert.ToHexString(loadedKey).ToLowerInvariant());
        Assert.Equal(v.Fingerprint, SyncCrypto.Fingerprint(loadedKey));

        if (!OperatingSystem.IsWindows())
        {
            var mode = File.GetUnixFileMode(keyPath);
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, mode);
        }
    }

    // =========================================================================
    // SCENARIO 3 — Refresh and rotation
    // =========================================================================
    [Fact]
    public async Task Scenario03_RefreshAndRotation_persists_rotated_token_and_old_token_is_refused()
    {
        var v = LoadVectorCase1();
        await using var harness = await CreateCaptureHarnessAsync(masterKey: v.MasterKey);

        var state1 = DeviceCredentials.LoadDeviceState(harness.StatePath)!;
        var oldRefreshToken = state1.RefreshToken;

        // Runner'ning birinchi sikli (SyncCycleAsync) tokenni yangilaydi va rotatsiya qilingan refresh tokenni saqlaydi
        var cycleOk = await harness.Runner.SyncCycleAsync();
        Assert.True(cycleOk);

        var state2 = DeviceCredentials.LoadDeviceState(harness.StatePath)!;
        var rotatedRefreshToken = state2.RefreshToken;
        Assert.NotEqual(oldRefreshToken, rotatedRefreshToken);

        // Eski refresh token haqiqiy server tomonidan rad etiladi (RefreshTokenAsync null qaytaradi)
        var httpClient = harness.Provider.GetRequiredService<HttpClient>();
        var syncClient = new CaptureSyncHttpClient(httpClient);
        var oldRefreshResult = await syncClient.RefreshTokenAsync(
            harness.Config["Capture:Sync:ServerUrl"]!, state1.DeviceId, oldRefreshToken);
        Assert.Null(oldRefreshResult);

        // State fayldan qayta qurilgan runner (restart) yangi token bilan muvaffaqiyatli ishlaydi
        var restartedRunner = new CaptureSyncRunner(
            harness.Cache, syncClient, harness.Config);
        var restartCycleOk = await restartedRunner.SyncCycleAsync();
        Assert.True(restartCycleOk);
        Assert.False(restartedRunner.IsStopped);
    }

    // =========================================================================
    // SCENARIO 4 — Push
    // =========================================================================
    [Fact]
    public async Task Scenario04_Push_transfers_deleted_edited_and_activity_and_payloads_decrypt_correctly()
    {
        var v = LoadVectorCase1();
        await using var harness = await CreateCaptureHarnessAsync(masterKey: v.MasterKey, accountId: "12345");

        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        long chatId = 111L;
        string peerId = "111";

        // 1. deleted: avval xabarni keshga yozish, so'ng o'chirish
        harness.Transport.PushUpdate($$"""
        {
          "@type": "updateNewMessage",
          "message": {
            "@type": "message",
            "id": {{101L << 20}},
            "chat_id": {{chatId}},
            "date": {{now}},
            "sender_id": { "@type": "messageSenderUser", "user_id": {{chatId}} },
            "is_outgoing": false,
            "content": { "@type": "messageText", "text": { "text": "hello to delete" } }
          }
        }
        """);
        Assert.True(await WaitAsync(() => harness.Cache.Get(chatId, 101) != null));

        harness.Transport.PushUpdate($$"""
        {
          "@type": "updateDeleteMessages",
          "chat_id": {{chatId}},
          "message_ids": [{{101L << 20}}],
          "is_permanent": true,
          "from_cache": false
        }
        """);
        Assert.True(await WaitAsync(() => harness.Cache.GetOutboxRows("deleted").Count == 1));

        // 2. edited: avval keshga yozish, so'ng content va edit_date kelishi
        harness.Transport.PushUpdate($$"""
        {
          "@type": "updateNewMessage",
          "message": {
            "@type": "message",
            "id": {{102L << 20}},
            "chat_id": {{chatId}},
            "date": {{now}},
            "sender_id": { "@type": "messageSenderUser", "user_id": {{chatId}} },
            "is_outgoing": false,
            "content": { "@type": "messageText", "text": { "text": "original text" } }
          }
        }
        """);
        Assert.True(await WaitAsync(() => harness.Cache.Get(chatId, 102) != null));

        harness.Transport.PushUpdate($$"""
        {
          "@type": "updateMessageContent",
          "chat_id": {{chatId}},
          "message_id": {{102L << 20}},
          "new_content": { "@type": "messageText", "text": { "text": "updated new text" } }
        }
        """);
        harness.Transport.PushUpdate($$"""
        {
          "@type": "updateMessageEdited",
          "chat_id": {{chatId}},
          "message_id": {{102L << 20}},
          "edit_date": {{now + 5}}
        }
        """);
        Assert.True(await WaitAsync(() => harness.Cache.GetOutboxRows("edited").Count == 1));

        // 3. activity: Include qilingan peer 222 uchun status online
        harness.Transport.PushUpdate($$"""
        {
          "@type": "updateUserStatus",
          "user_id": 222,
          "status": { "@type": "userStatusOnline", "expires": {{now + 300}} }
        }
        """);
        Assert.True(await WaitAsync(() => harness.Cache.GetOutboxRows("activity").Count == 1));

        Assert.Equal(3, harness.Cache.GetEligibleOutboxRows(500, now + 10).Count);

        // Bitta SyncCycleAsync barchasini push qiladi
        var startSeq = await GetCurrentMaxSeqAsync();
        var syncOk = await harness.Runner.SyncCycleAsync();
        Assert.True(syncOk);
        Assert.Equal(3, harness.Runner.PushedCount);
        Assert.Empty(harness.Cache.GetEligibleOutboxRows(500, now + 10));

        // Serverda tekshirish: pull orqali 3 ta yozuv olinadi
        var (deviceClient, _, _) = await CreateEnrolledDeviceAsync();
        var pullResp = await deviceClient.GetFromJsonAsync<PullResponse>($"/api/v1/sync/pull?since={startSeq}&limit=500", JsonOptions);
        Assert.NotNull(pullResp);

        var serverRecords = pullResp.Records.Where(r => r.DeviceId == harness.DeviceId).ToList();
        Assert.Equal(3, serverRecords.Count);

        var contentKey = SyncCrypto.DeriveContentKey(v.MasterKey);
        var peerKey = SyncCrypto.DerivePeerKey(v.MasterKey);

        // deleted tekshiruvi
        var delRec = Assert.Single(serverRecords.Where(r => r.Kind == "deleted"));
        Assert.Equal(101, delRec.MsgId);
        Assert.Equal(now, delRec.OccurredAt);
        Assert.Equal(CryptoPrimitives.ComputePeerHash(peerKey, peerId), delRec.PeerHash);
        var delPlain = SyncCrypto.DecryptPayload(contentKey, delRec.Nonce, delRec.Payload);
        using var delDoc = JsonDocument.Parse(delPlain);
        Assert.Equal("hello to delete", delDoc.RootElement.GetProperty("text").GetString());
        Assert.Equal("12345", delDoc.RootElement.GetProperty("account_id").GetString());
        Assert.Equal("111", delDoc.RootElement.GetProperty("peer_id").GetString());

        // edited tekshiruvi
        var editRec = Assert.Single(serverRecords.Where(r => r.Kind == "edited"));
        Assert.Equal(102, editRec.MsgId);
        Assert.Equal(now + 5, editRec.OccurredAt);
        Assert.Equal(CryptoPrimitives.ComputePeerHash(peerKey, peerId), editRec.PeerHash);
        var editPlain = SyncCrypto.DecryptPayload(contentKey, editRec.Nonce, editRec.Payload);
        using var editDoc = JsonDocument.Parse(editPlain);
        Assert.Equal("original text", editDoc.RootElement.GetProperty("old_text").GetString());
        Assert.Equal("updated new text", editDoc.RootElement.GetProperty("new_text").GetString());
        Assert.Equal("12345", editDoc.RootElement.GetProperty("account_id").GetString());
        Assert.Equal("111", editDoc.RootElement.GetProperty("peer_id").GetString());

        // activity tekshiruvi (§0.12 account_hash bo'sh, §3.2.2 status online)
        var actRec = Assert.Single(serverRecords.Where(r => r.Kind == "activity"));
        Assert.Equal("", actRec.AccountHash);
        Assert.Equal(CryptoPrimitives.ComputePeerHash(peerKey, "222"), actRec.PeerHash);
        Assert.Equal(ActivityMapper.DiscriminatorFor("status"), actRec.MsgId);
        var actPlain = SyncCrypto.DecryptPayload(contentKey, actRec.Nonce, actRec.Payload);
        using var actDoc = JsonDocument.Parse(actPlain);
        Assert.Equal("status", actDoc.RootElement.GetProperty("field").GetString());
        Assert.Equal($"online:{now + 300}", actDoc.RootElement.GetProperty("new_value").GetString());
        Assert.Equal("12345", actDoc.RootElement.GetProperty("account_id").GetString());
        Assert.Equal("222", actDoc.RootElement.GetProperty("peer_id").GetString());
    }

    // =========================================================================
    // SCENARIO 5 — Duplicate
    // =========================================================================
    [Fact]
    public async Task Scenario05_Duplicate_keeps_one_record_and_smaller_observed_at_replaces_stored_record()
    {
        var v = LoadVectorCase1();

        long occurredAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        long t1 = occurredAt + 200;
        long t2 = occurredAt + 100; // t2 < t1 (kichikroq observed_at)
        long t3 = occurredAt + 300; // t3 > t2 (kattaroq observed_at)
        long chatId = 777123L;
        string peerId = chatId.ToString(CultureInfo.InvariantCulture);
        long msgId = 55;

        var clock1 = new TestTimeProvider(DateTimeOffset.FromUnixTimeSeconds(t1));
        var clock2 = new TestTimeProvider(DateTimeOffset.FromUnixTimeSeconds(t2));
        var clock3 = new TestTimeProvider(DateTimeOffset.FromUnixTimeSeconds(t3));

        await using var harness1 = await CreateCaptureHarnessAsync(timeProvider: clock1, masterKey: v.MasterKey, accountId: "12345");
        await using var harness2 = await CreateCaptureHarnessAsync(timeProvider: clock2, masterKey: v.MasterKey, accountId: "12345");
        await using var harness3 = await CreateCaptureHarnessAsync(timeProvider: clock3, masterKey: v.MasterKey, accountId: "12345");

        void FeedDeletedMessage(CaptureHarness h)
        {
            h.Transport.PushUpdate($$"""
            {
              "@type": "updateNewMessage",
              "message": {
                "@type": "message",
                "id": {{msgId << 20}},
                "chat_id": {{chatId}},
                "date": {{occurredAt}},
                "sender_id": { "@type": "messageSenderUser", "user_id": {{chatId}} },
                "is_outgoing": false,
                "content": { "@type": "messageText", "text": { "text": "dup event" } }
              }
            }
            """);
            h.Transport.PushUpdate($$"""
            {
              "@type": "updateDeleteMessages",
              "chat_id": {{chatId}},
              "message_ids": [{{msgId << 20}}],
              "is_permanent": true,
              "from_cache": false
            }
            """);
        }

        // 1. Device 1 o'sha hodisani kuzatadi va push qiladi (observed_at = t1 = 200)
        var startSeq = await GetCurrentMaxSeqAsync();
        FeedDeletedMessage(harness1);
        Assert.True(await WaitAsync(() => harness1.Cache.GetOutboxRows("deleted").Count == 1));
        var push1 = await harness1.Runner.PushCycleAsync();
        Assert.True(push1);
        Assert.Equal(1, harness1.Runner.PushedCount);

        // 2. Device 2 aynan shu hodisani (kichikroq observed_at = t2 = 100 bilan) push qiladi
        FeedDeletedMessage(harness2);
        Assert.True(await WaitAsync(() => harness2.Cache.GetOutboxRows("deleted").Count == 1));
        var push2 = await harness2.Runner.PushCycleAsync();
        Assert.True(push2);
        // Server supersedes qildi: capture dublikat deb sanaydi va qayta urinmaydi
        Assert.Equal(1, harness2.Runner.DuplicateCount);
        Assert.Empty(harness2.Cache.GetEligibleOutboxRows(500, t1 + 1000));

        // Serverda tekshirish: yozuv bitta, lekin observed_at kichigi (t2) ga ALMASHTIRILGAN (§3.4)
        var peerKey = SyncCrypto.DerivePeerKey(v.MasterKey);
        var peerHash = CryptoPrimitives.ComputePeerHash(peerKey, peerId);

        var (adminClient, _) = await CreateAdminClientAsync();
        var pull1 = await adminClient.GetFromJsonAsync<PullResponse>($"/api/v1/sync/pull?since={startSeq}&limit=500", JsonOptions);
        Assert.NotNull(pull1);
        var storedList = pull1.Records.Where(r => r.PeerHash == peerHash && r.MsgId == msgId).ToList();
        var stored = Assert.Single(storedList);
        Assert.Equal(t2, stored.ObservedAt);
        Assert.Equal(harness2.DeviceId, stored.DeviceId);

        // 3. Device 3 kattaroq observed_at (t3 = 300) bilan push qiladi: rad etiladi (duplicate), saqlangan yozuv tegmaydi
        FeedDeletedMessage(harness3);
        Assert.True(await WaitAsync(() => harness3.Cache.GetOutboxRows("deleted").Count == 1));
        var push3 = await harness3.Runner.PushCycleAsync();
        Assert.True(push3);
        Assert.Equal(1, harness3.Runner.DuplicateCount);
        Assert.Empty(harness3.Cache.GetEligibleOutboxRows(500, t3 + 1000));

        var pull2 = await adminClient.GetFromJsonAsync<PullResponse>($"/api/v1/sync/pull?since={startSeq}&limit=500", JsonOptions);
        Assert.NotNull(pull2);
        var storedList2 = pull2.Records.Where(r => r.PeerHash == peerHash && r.MsgId == msgId).ToList();
        var stored2 = Assert.Single(storedList2);
        Assert.Equal(t2, stored2.ObservedAt);
        Assert.Equal(harness2.DeviceId, stored2.DeviceId);
    }

    // =========================================================================
    // SCENARIO 6 — Batch limit
    // =========================================================================
    [Fact]
    public async Task Scenario06_BatchLimit_shrunk_on_rejection_and_all_rows_arrive()
    {
        using var scope = _factory.Services.CreateScope();
        var settings = scope.ServiceProvider.GetRequiredService<SettingsService>();
        var originalBatchSize = await settings.GetIntAsync("sync.push_batch_size");

        try
        {
            // Server sozlamasi 2 ga tushiriladi
            await settings.SetAsync("sync.push_batch_size", "2");

            var v = LoadVectorCase1();
            // Capture boshlang'ich partiya o'lchami 4 ta
            await using var harness = await CreateCaptureHarnessAsync(masterKey: v.MasterKey, pushBatchSize: 4);

            long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            long chatId = 666123L;

            // 4 ta o'chirilgan xabar qatori yaratiladi
            for (long id = 1; id <= 4; id++)
            {
                harness.Transport.PushUpdate($$"""
                {
                  "@type": "updateNewMessage",
                  "message": {
                    "@type": "message",
                    "id": {{id << 20}},
                    "chat_id": {{chatId}},
                    "date": {{now}},
                    "sender_id": { "@type": "messageSenderUser", "user_id": {{chatId}} },
                    "is_outgoing": false,
                    "content": { "@type": "messageText", "text": { "text": "batch message" } }
                  }
                }
                """);
                harness.Transport.PushUpdate($$"""
                {
                  "@type": "updateDeleteMessages",
                  "chat_id": {{chatId}},
                  "message_ids": [{{id << 20}}],
                  "is_permanent": true,
                  "from_cache": false
                }
                """);
            }

            Assert.True(await WaitAsync(() => harness.Cache.GetOutboxRows("deleted").Count == 4));

            var startSeq = await GetCurrentMaxSeqAsync();

            // 1-sikl: 4 ta yuboriladi -> server 'batch_too_large' bilan rad etadi -> capture partiyani 2 ga qisqartiradi
            var cycle1 = await harness.Runner.PushCycleAsync();
            Assert.True(cycle1);
            Assert.Equal(0, harness.Runner.PushedCount);
            Assert.Equal(4, harness.Cache.GetEligibleOutboxRows(500, now + 10).Count);

            // 2-sikl: 2 ta yuboriladi va server qabul qiladi
            var cycle2 = await harness.Runner.PushCycleAsync();
            Assert.True(cycle2);
            Assert.Equal(2, harness.Runner.PushedCount);
            Assert.Equal(2, harness.Cache.GetEligibleOutboxRows(500, now + 10).Count);

            // 3-sikl: qolgan 2 ta yuboriladi va server qabul qiladi
            var cycle3 = await harness.Runner.PushCycleAsync();
            Assert.True(cycle3);
            Assert.Equal(4, harness.Runner.PushedCount);
            Assert.Empty(harness.Cache.GetEligibleOutboxRows(500, now + 10));

            // Serverda barcha 4 ta yozuv yo'qotilmasdan va takrorlanmasdan yetib kelgani tasdiqlanadi
            var (deviceClient, _, _) = await CreateEnrolledDeviceAsync();
            var pull = await deviceClient.GetFromJsonAsync<PullResponse>($"/api/v1/sync/pull?since={startSeq}&limit=500", JsonOptions);
            Assert.NotNull(pull);
            var devRecords = pull.Records.Where(r => r.DeviceId == harness.DeviceId).ToList();
            Assert.Equal(4, devRecords.Count);
        }
        finally
        {
            await settings.SetAsync("sync.push_batch_size", originalBatchSize.ToString(CultureInfo.InvariantCulture));
        }
    }

    // =========================================================================
    // SCENARIO 7 — Pull of settings
    // =========================================================================
    [Fact]
    public async Task Scenario07_PullOfSettings_applies_eight_scope_settings_updates_snapshot_and_persists_cursor()
    {
        var v = LoadVectorCase1();

        // 1. "tdesktop" qurilmasini ro'yxatdan o'tkazish
        var (tdesktopClient, tdesktopDeviceId, _) = await CreateEnrolledDeviceAsync();

        // 2. Capture qurilmasini yaratish (CurrentSnapshot avval null bo'ladi)
        await using var harness = await CreateCaptureHarnessAsync(masterKey: v.MasterKey);
        Assert.Null(harness.SettingsSource.CurrentSnapshot);

        var startSeq = await GetCurrentMaxSeqAsync();
        harness.Cache.MergeSyncedSettingsAndCommitCursor([], startSeq);

        // 3. tdesktop 8 ta xabar sozlamasini POST /api/v1/sync/push orqali yuboradi
        var contentKey = SyncCrypto.DeriveContentKey(v.MasterKey);
        var peerKey = SyncCrypto.DerivePeerKey(v.MasterKey);
        var accountKey = SyncCrypto.DeriveAccountKey(v.MasterKey);

        var settingValues = new Dictionary<string, string>
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

        var records = new List<object>();
        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        string accountId = "12345";
        string accountHash = CryptoPrimitives.ComputeAccountHash(accountKey, accountId);
        string peerHash = CryptoPrimitives.ComputePeerHash(peerKey, "0");

        foreach (var (k, val) in settingValues)
        {
            long msgId = ActivityMapper.DiscriminatorFor(k);
            string recId = RecordId.Compute(RecordKind.Setting, accountHash, peerHash, msgId, now);
            string payloadJson = $"{{\"key\":\"{k}\",\"value\":\"{PayloadBuilder.EscapeString(val)}\",\"account_id\":\"{accountId}\",\"peer_id\":\"0\"}}";
            var (enc, nonce) = SyncCrypto.EncryptPayload(contentKey, Encoding.UTF8.GetBytes(payloadJson));

            records.Add(new
            {
                record_id = recId,
                device_id = tdesktopDeviceId,
                kind = "setting",
                account_hash = accountHash,
                peer_hash = peerHash,
                msg_id = msgId,
                occurred_at = now,
                observed_at = now,
                nonce = Convert.ToBase64String(nonce),
                payload = Convert.ToBase64String(enc)
            });
        }

        var pushResp = await tdesktopClient.PostAsJsonAsync("/api/v1/sync/push", new { records });
        Assert.Equal(HttpStatusCode.OK, pushResp.StatusCode);

        // 4. Capture PullCycleAsync: 8 ta sozlama olinadi, snapshot yangilanadi, kursor saqlanadi
        var pullOk = await harness.Runner.PullCycleAsync();
        Assert.True(pullOk);
        Assert.Equal(8, harness.Runner.PulledCount);

        var snap = harness.SettingsSource.CurrentSnapshot;
        Assert.NotNull(snap);
        Assert.True(snap.Whitelist.Contains("7053823996"));
        Assert.True(snap.Whitelist.Contains("562952781246744"));
        Assert.True(snap.Blocklist.Contains("12345678"));
        Assert.True(snap.GlobalAntiDelete);
        Assert.False(snap.GlobalAntiEdit);

        var cursor1 = harness.Cache.GetPullCursor("pull_cursor");
        Assert.True(cursor1 > 0);

        // 5. Ikkinchi pull: hech narsa qayta qo'llanilmaydi
        var pullOk2 = await harness.Runner.PullCycleAsync();
        Assert.True(pullOk2);
        Assert.Equal(8, harness.Runner.PulledCount); // yangi hech narsa tortilmadi
        Assert.Equal(cursor1, harness.Cache.GetPullCursor("pull_cursor"));
    }

    // =========================================================================
    // SCENARIO 8 — Health
    // =========================================================================
    [Fact]
    public async Task Scenario08_Health_reports_snapshot_and_admin_endpoint_reads_exact_values()
    {
        var v = LoadVectorCase1();
        await using var harness = await CreateCaptureHarnessAsync(masterKey: v.MasterKey);

        var snapshot = new StorageSnapshot(
            TakenAt: DateTimeOffset.UtcNow,
            ProcessRssBytes: 111_222_333L,
            MemoryLimitBytes: 2_000_000_000L,
            CacheDatabaseBytes: 3_456_789L,
            MediaStoreBytes: 4_567_890L,
            MediaStoreFiles: 77,
            TdlibFilesBytes: 5_678_901L,
            TdlibDatabaseBytes: 6_789_012L,
            FreeDiskBytes: 70_000_000_000L,
            OptimizeFreedBytes: 0,
            OptimizeDeletedFiles: 0,
            MediaRowsPruned: 0,
            MediaFilesDeleted: 0);

        // ReportHealthAsync orqali hisobot yuborish
        var reported = await harness.Runner.ReportHealthAsync(snapshot);
        Assert.True(reported);

        // Admin GET /api/v1/devices/health aynan shu snapshot qiymatlarini qaytaradi
        var (adminClient, _) = await CreateAdminClientAsync();
        var getResp = await adminClient.GetAsync("/api/v1/devices/health");
        Assert.Equal(HttpStatusCode.OK, getResp.StatusCode);

        var list = await getResp.Content.ReadFromJsonAsync<List<JsonElement>>();
        Assert.NotNull(list);

        var item = list.FirstOrDefault(x => x.GetProperty("device_id").GetString() == harness.DeviceId);
        Assert.True(item.ValueKind != JsonValueKind.Undefined, "Capture qurilmasi health ro'yxatida topilmadi");

        Assert.Equal(111_222_333L, item.GetProperty("rss_bytes").GetInt64());
        Assert.Equal(2_000_000_000L, item.GetProperty("memory_limit_bytes").GetInt64());
        Assert.Equal(3_456_789L, item.GetProperty("cache_db_bytes").GetInt64());
        Assert.Equal(4_567_890L, item.GetProperty("media_store_bytes").GetInt64());
        Assert.Equal(77, item.GetProperty("media_store_files").GetInt32());
        Assert.Equal(5_678_901L, item.GetProperty("tdlib_files_bytes").GetInt64());
        Assert.Equal(6_789_012L, item.GetProperty("tdlib_database_bytes").GetInt64());
        Assert.Equal(70_000_000_000L, item.GetProperty("free_disk_bytes").GetInt64());
    }

    // =========================================================================
    // SCENARIO 9 — Revocation
    // =========================================================================
    [Fact]
    public async Task Scenario09_Revocation_refuses_cycle_stops_runner_sends_no_further_requests_and_health_returns_false()
    {
        var v = LoadVectorCase1();
        await using var harness = await CreateCaptureHarnessAsync(masterKey: v.MasterKey);

        // Boshlang'ich sikl muvaffaqiyatli o'tadi
        var initialPush = await harness.Runner.PushCycleAsync();
        Assert.True(initialPush);

        // Outbox'ga yangi qator qo'shamiz
        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        harness.Transport.PushUpdate($$"""
        {
          "@type": "updateNewMessage",
          "message": {
            "@type": "message",
            "id": {{999L << 20}},
            "chat_id": 999,
            "date": {{now}},
            "sender_id": { "@type": "messageSenderUser", "user_id": 999 },
            "is_outgoing": false,
            "content": { "@type": "messageText", "text": { "text": "revoke test" } }
          }
        }
        """);
        harness.Transport.PushUpdate($$"""
        {
          "@type": "updateDeleteMessages",
          "chat_id": 999,
          "message_ids": [{{999L << 20}}],
          "is_permanent": true,
          "from_cache": false
        }
        """);
        Assert.True(await WaitAsync(() => harness.Cache.GetOutboxRows("deleted").Count == 1));

        // Admin qurilmani bekor qiladi (DELETE /api/v1/devices/{id})
        var (adminClient, _) = await CreateAdminClientAsync();
        var delResp = await adminClient.DeleteAsync($"/api/v1/devices/{harness.DeviceId}");
        Assert.Equal(HttpStatusCode.NoContent, delResp.StatusCode);

        // Navbatdagi sikl: push va refresh rad etiladi, runner to'xtaydi (IsStopped)
        var pushOk = await harness.Runner.PushCycleAsync();
        Assert.False(pushOk);
        Assert.True(harness.Runner.IsStopped);

        int countAfterStopped = harness.CountingHandler.RequestCount;

        // Runner to'xtaganidan keyin yangi so'rovlar umuman yuborilmaydi
        var pushAgain = await harness.Runner.PushCycleAsync();
        Assert.False(pushAgain);
        var pullAgain = await harness.Runner.PullCycleAsync();
        Assert.False(pullAgain);
        Assert.Equal(countAfterStopped, harness.CountingHandler.RequestCount);

        // Bekor qilingandan so'ng health hisoboti false qaytaradi va HTTP so'rov yubormaydi
        var snapshot = new StorageSnapshot(
            DateTimeOffset.UtcNow, 10, 20, 30, 40, 1, 50, 60, 70, 0, 0, 0, 0);
        var healthOk = await harness.Runner.ReportHealthAsync(snapshot);
        Assert.False(healthOk);
        Assert.Equal(countAfterStopped, harness.CountingHandler.RequestCount);
    }
}
