using System.Text.Json;
using System.Text.Json.Nodes;
using CustomSync.Capture.Capture;
using CustomSync.Capture.Preflight;
using CustomSync.Capture.Tdlib;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace CustomSync.Tests;

public class CustomActivityScope : IActivityScope
{
    private readonly Func<string, bool, bool> _predicate;

    public CustomActivityScope(Func<string, bool, bool> predicate)
    {
        _predicate = predicate;
    }

    public bool ShouldTrackActivity(string peerId, bool isContact) => _predicate(peerId, isContact);
}

public class CaptureActivityTests : IDisposable
{
    private readonly List<string> _tempFiles = new();

    private string CreateTempDbPath()
    {
        var path = Path.Combine(Path.GetTempPath(), $"capture_activity_test_{Guid.NewGuid():N}.db");
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
                var wal = file + "-wal";
                if (File.Exists(wal)) File.Delete(wal);
                var shm = file + "-shm";
                if (File.Exists(shm)) File.Delete(shm);
            }
            catch
            {
                // Best effort cleanup
            }
        }
    }

    [Fact]
    public void Test01_Mapper_status_name_username_and_discriminator()
    {
        long now = 1787000000;

        // Status kinds
        using var onlineDoc = JsonDocument.Parse(@"{ ""@type"": ""userStatusOnline"", ""expires"": 1787000300 }");
        Assert.Equal("online:1787000300", ActivityMapper.MapStatus(onlineDoc.RootElement, now));

        // Online with expired timestamp -> offline:<expires>
        using var expiredDoc = JsonDocument.Parse(@"{ ""@type"": ""userStatusOnline"", ""expires"": 1786999900 }");
        Assert.Equal("offline:1786999900", ActivityMapper.MapStatus(expiredDoc.RootElement, now));

        using var offlineDoc = JsonDocument.Parse(@"{ ""@type"": ""userStatusOffline"", ""was_online"": 1786995000 }");
        Assert.Equal("offline:1786995000", ActivityMapper.MapStatus(offlineDoc.RootElement, now));

        using var offlineZeroDoc = JsonDocument.Parse(@"{ ""@type"": ""userStatusOffline"", ""was_online"": 0 }");
        Assert.Equal("empty", ActivityMapper.MapStatus(offlineZeroDoc.RootElement, now));

        using var recentlyDoc = JsonDocument.Parse(@"{ ""@type"": ""userStatusRecently"" }");
        Assert.Equal("recently", ActivityMapper.MapStatus(recentlyDoc.RootElement, now));

        using var lastWeekDoc = JsonDocument.Parse(@"{ ""@type"": ""userStatusLastWeek"" }");
        Assert.Equal("within_week", ActivityMapper.MapStatus(lastWeekDoc.RootElement, now));

        using var lastMonthDoc = JsonDocument.Parse(@"{ ""@type"": ""userStatusLastMonth"" }");
        Assert.Equal("within_month", ActivityMapper.MapStatus(lastMonthDoc.RootElement, now));

        using var emptyDoc = JsonDocument.Parse(@"{ ""@type"": ""userStatusEmpty"" }");
        Assert.Equal("empty", ActivityMapper.MapStatus(emptyDoc.RootElement, now));

        using var unknownDoc = JsonDocument.Parse(@"{ ""@type"": ""someUnknownStatus"" }");
        Assert.Equal("empty", ActivityMapper.MapStatus(unknownDoc.RootElement, now));

        // Name mapping (langFullName)
        Assert.Equal("Ali Valiyev", ActivityMapper.MapName("Ali", "Valiyev"));
        Assert.Equal("Ali", ActivityMapper.MapName("Ali", ""));
        Assert.Equal("Ali", ActivityMapper.MapName("Ali", null));
        Assert.Equal("Valiyev", ActivityMapper.MapName("", "Valiyev"));
        Assert.Equal("Valiyev", ActivityMapper.MapName(null, "Valiyev"));
        Assert.Equal("", ActivityMapper.MapName("", ""));
        Assert.Equal("Иван Иванов 🇺🇿", ActivityMapper.MapName("Иван", "Иванов 🇺🇿"));

        // Username mapping
        using var multiUsernamesDoc = JsonDocument.Parse(@"{
            ""active_usernames"": [""primary_user"", ""secondary_user""],
            ""editable_username"": ""primary_user""
        }");
        Assert.Equal("primary_user", ActivityMapper.MapUsername(multiUsernamesDoc.RootElement));

        using var singleEditableDoc = JsonDocument.Parse(@"{
            ""active_usernames"": [],
            ""editable_username"": ""only_editable""
        }");
        Assert.Equal("only_editable", ActivityMapper.MapUsername(singleEditableDoc.RootElement));

        using var emptyUsernamesDoc = JsonDocument.Parse(@"{
            ""active_usernames"": []
        }");
        Assert.Equal("", ActivityMapper.MapUsername(emptyUsernamesDoc.RootElement));

        // Discriminator check (pins value for field names matching tdesktop)
        Assert.Equal(521316072760462774L, ActivityMapper.DiscriminatorFor("status"));
        Assert.Equal(190087418246581886L, ActivityMapper.DiscriminatorFor("name"));
        Assert.Equal(1654943659220005122L, ActivityMapper.DiscriminatorFor("username"));
    }

    [Fact]
    public void Test02_First_observation_records_event_with_has_old_value_false_and_skips_empty()
    {
        var dbPath = CreateTempDbPath();
        var timeProvider = new TestTimeProvider(DateTimeOffset.FromUnixTimeSeconds(1787000000));
        var cache = new MessageCache(dbPath, timeProvider);
        cache.Initialize();

        var scope = new CustomActivityScope((_, _) => true);
        var handler = new CaptureUpdateHandler(cache, scope: null, activityScope: scope, timeProvider: timeProvider, accountId: "7053823996");

        // User 1 has name "Ali" and username "ali_dev"
        var userUpdate1 = @"{
            ""@type"": ""updateUser"",
            ""user"": {
                ""id"": 111,
                ""first_name"": ""Ali"",
                ""last_name"": """",
                ""usernames"": {
                    ""active_usernames"": [""ali_dev""]
                },
                ""is_contact"": true,
                ""status"": { ""@type"": ""userStatusOnline"", ""expires"": 1787000300 }
            }
        }";

        handler.HandleUpdate(userUpdate1);

        var rows = cache.GetOutboxRows("activity");
        // name, username, status -> 3 rows
        Assert.Equal(3, rows.Count);

        var nameRow = rows.First(r => r.MsgId == ActivityMapper.DiscriminatorFor("name"));
        var namePayload = JsonNode.Parse(nameRow.PayloadJson)!.AsObject();
        Assert.Equal("7053823996", nameRow.AccountId);
        Assert.Equal("111", nameRow.PeerId);
        Assert.Equal("name", namePayload["field"]!.GetValue<string>());
        Assert.False(namePayload["has_old_value"]!.GetValue<bool>());
        Assert.Null(namePayload["old_value"]);
        Assert.Equal("Ali", namePayload["new_value"]!.GetValue<string>());

        var usernameRow = rows.First(r => r.MsgId == ActivityMapper.DiscriminatorFor("username"));
        var usernamePayload = JsonNode.Parse(usernameRow.PayloadJson)!.AsObject();
        Assert.Equal("username", usernamePayload["field"]!.GetValue<string>());
        Assert.False(usernamePayload["has_old_value"]!.GetValue<bool>());
        Assert.Null(usernamePayload["old_value"]);
        Assert.Equal("ali_dev", usernamePayload["new_value"]!.GetValue<string>());

        var statusRow = rows.First(r => r.MsgId == ActivityMapper.DiscriminatorFor("status"));
        var statusPayload = JsonNode.Parse(statusRow.PayloadJson)!.AsObject();
        Assert.Equal("status", statusPayload["field"]!.GetValue<string>());
        Assert.False(statusPayload["has_old_value"]!.GetValue<bool>());
        Assert.Null(statusPayload["old_value"]);
        Assert.Equal("online:1787000300", statusPayload["new_value"]!.GetValue<string>());

        // User 2 has name "Vali" but NO username
        var userUpdate2 = @"{
            ""@type"": ""updateUser"",
            ""user"": {
                ""id"": 222,
                ""first_name"": ""Vali"",
                ""last_name"": """",
                ""usernames"": {
                    ""active_usernames"": []
                },
                ""is_contact"": true
            }
        }";

        handler.HandleUpdate(userUpdate2);

        // Only name row must be emitted for User 2; empty username must NOT emit an outbox row
        var user2Rows = cache.GetOutboxRows("activity").Where(r => r.PeerId == "222").ToList();
        Assert.Single(user2Rows);
        Assert.Equal(ActivityMapper.DiscriminatorFor("name"), user2Rows[0].MsgId);
    }

    [Fact]
    public void Test03_Real_change_produces_one_row_with_exact_payload_shape_and_types()
    {
        var dbPath = CreateTempDbPath();
        var timeProvider = new TestTimeProvider(DateTimeOffset.FromUnixTimeSeconds(1787000000));
        var cache = new MessageCache(dbPath, timeProvider);
        cache.Initialize();

        var scope = new CustomActivityScope((_, _) => true);
        var handler = new CaptureUpdateHandler(cache, scope: null, activityScope: scope, timeProvider: timeProvider, accountId: "7053823996");

        // Initial observation: name "Ali"
        handler.HandleUpdate(@"{
            ""@type"": ""updateUser"",
            ""user"": {
                ""id"": 333,
                ""first_name"": ""Ali"",
                ""last_name"": """",
                ""is_contact"": false
            }
        }");

        timeProvider.Advance(TimeSpan.FromSeconds(10)); // now = 1787000010

        // Change name to "Ali Valiyev"
        handler.HandleUpdate(@"{
            ""@type"": ""updateUser"",
            ""user"": {
                ""id"": 333,
                ""first_name"": ""Ali"",
                ""last_name"": ""Valiyev"",
                ""is_contact"": false
            }
        }");

        var rows = cache.GetOutboxRows("activity");
        Assert.Equal(2, rows.Count);

        var secondRow = rows[1];
        Assert.Equal(1787000010, secondRow.OccurredAt);
        Assert.Equal(1787000010, secondRow.ObservedAt);
        Assert.Equal(ActivityMapper.DiscriminatorFor("name"), secondRow.MsgId);

        var payload = JsonNode.Parse(secondRow.PayloadJson)!.AsObject();
        var keys = payload.Select(kv => kv.Key).ToArray();
        Assert.Equal(new[] { "account_id", "peer_id", "field", "old_value", "has_old_value", "new_value" }, keys);

        Assert.Equal("7053823996", payload["account_id"]!.GetValue<string>());
        Assert.Equal("333", payload["peer_id"]!.GetValue<string>());
        Assert.Equal("name", payload["field"]!.GetValue<string>());
        Assert.True(payload["has_old_value"]!.GetValue<bool>());
        Assert.Equal("Ali", payload["old_value"]!.GetValue<string>());
        Assert.Equal("Ali Valiyev", payload["new_value"]!.GetValue<string>());
    }

    [Fact]
    public void Test04_Noise_filter_status_within_window_suppressed_after_window_written()
    {
        var dbPath = CreateTempDbPath();
        var timeProvider = new TestTimeProvider(DateTimeOffset.FromUnixTimeSeconds(1787000010));
        var cache = new MessageCache(dbPath, timeProvider);
        cache.Initialize();

        var scope = new CustomActivityScope((_, _) => true);
        var handler = new CaptureUpdateHandler(cache, scope: null, activityScope: scope, timeProvider: timeProvider, accountId: "7053823996");

        // 1. Initial status: offline:1787000000 at now=1787000010
        handler.HandleUpdate(@"{
            ""@type"": ""updateUserStatus"",
            ""user_id"": 444,
            ""status"": { ""@type"": ""userStatusOffline"", ""was_online"": 1787000000 }
        }");

        var rows = cache.GetOutboxRows("activity");
        Assert.Single(rows);

        // 2. 10s later at now=1787000020, Telegram sends periodic offline bump: was_online=1787000015
        // oldAge = 1787000020 - 1787000000 = 20
        // newAge = 1787000020 - 1787000015 = 5
        // |20 - 5| = 15 < 60 seconds -> MUST BE SUPPRESSED!
        timeProvider.SetUtcNow(DateTimeOffset.FromUnixTimeSeconds(1787000020));
        handler.HandleUpdate(@"{
            ""@type"": ""updateUserStatus"",
            ""user_id"": 444,
            ""status"": { ""@type"": ""userStatusOffline"", ""was_online"": 1787000015 }
        }");

        rows = cache.GetOutboxRows("activity");
        Assert.Single(rows); // Still 1 row, noise suppressed!

        // 3. 80s later at now=1787000100, was_online=1787000090
        // oldAge = 1787000100 - 1787000000 = 100
        // newAge = 1787000100 - 1787000090 = 10
        // |100 - 10| = 90 >= 60 seconds -> MUST BE WRITTEN!
        timeProvider.SetUtcNow(DateTimeOffset.FromUnixTimeSeconds(1787000100));
        handler.HandleUpdate(@"{
            ""@type"": ""updateUserStatus"",
            ""user_id"": 444,
            ""status"": { ""@type"": ""userStatusOffline"", ""was_online"": 1787000090 }
        }");

        rows = cache.GetOutboxRows("activity");
        Assert.Equal(2, rows.Count);

        var secondRow = rows[1];
        var payload = JsonNode.Parse(secondRow.PayloadJson)!.AsObject();
        Assert.Equal("offline:1787000000", payload["old_value"]!.GetValue<string>());
        Assert.Equal("offline:1787000090", payload["new_value"]!.GetValue<string>());
        Assert.True(payload["has_old_value"]!.GetValue<bool>());

        // 4. Status transition from offline to online is NEVER suppressed by noise filter
        timeProvider.SetUtcNow(DateTimeOffset.FromUnixTimeSeconds(1787000105));
        handler.HandleUpdate(@"{
            ""@type"": ""updateUserStatus"",
            ""user_id"": 444,
            ""status"": { ""@type"": ""userStatusOnline"", ""expires"": 1787000400 }
        }");

        rows = cache.GetOutboxRows("activity");
        Assert.Equal(3, rows.Count);
        var thirdPayload = JsonNode.Parse(rows[2].PayloadJson)!.AsObject();
        Assert.Equal("offline:1787000090", thirdPayload["old_value"]!.GetValue<string>());
        Assert.Equal("online:1787000400", thirdPayload["new_value"]!.GetValue<string>());
    }

    [Fact]
    public void Test05_Restart_same_db_preserves_old_value_and_emits_no_duplicate()
    {
        var dbPath = CreateTempDbPath();
        var timeProvider = new TestTimeProvider(DateTimeOffset.FromUnixTimeSeconds(1787000000));

        // Instance 1
        var cache1 = new MessageCache(dbPath, timeProvider);
        cache1.Initialize();
        var scope1 = new CustomActivityScope((_, _) => true);
        var handler1 = new CaptureUpdateHandler(cache1, scope: null, activityScope: scope1, timeProvider: timeProvider, accountId: "7053823996");

        handler1.HandleUpdate(@"{
            ""@type"": ""updateUserStatus"",
            ""user_id"": 555,
            ""status"": { ""@type"": ""userStatusOnline"", ""expires"": 1787000500 }
        }");

        Assert.Single(cache1.GetOutboxRows("activity"));

        // Instance 2 (after restart with same DB file)
        var cache2 = new MessageCache(dbPath, timeProvider);
        cache2.Initialize();
        var scope2 = new CustomActivityScope((_, _) => true);
        var handler2 = new CaptureUpdateHandler(cache2, scope: null, activityScope: scope2, timeProvider: timeProvider, accountId: "7053823996");

            // Replay same online status at startup -> must NOT emit duplicate row!
            handler2.HandleUpdate(@"{
                ""@type"": ""updateUserStatus"",
                ""user_id"": 555,
                ""status"": { ""@type"": ""userStatusOnline"", ""expires"": 1787000500 }
            }");

            Assert.Single(cache2.GetOutboxRows("activity"));

            // Real change -> must use old_value preserved across restart!
            timeProvider.Advance(TimeSpan.FromSeconds(10));
            handler2.HandleUpdate(@"{
                ""@type"": ""updateUserStatus"",
                ""user_id"": 555,
                ""status"": { ""@type"": ""userStatusOffline"", ""was_online"": 1787000010 }
            }");

            var rows = cache2.GetOutboxRows("activity");
            Assert.Equal(2, rows.Count);
            var payload = JsonNode.Parse(rows[1].PayloadJson)!.AsObject();
            Assert.True(payload["has_old_value"]!.GetValue<bool>());
            Assert.Equal("online:1787000500", payload["old_value"]!.GetValue<string>());
            Assert.Equal("offline:1787000010", payload["new_value"]!.GetValue<string>());
    }

    [Fact]
    public void Test06_Replay_of_same_update_creates_no_new_row()
    {
        var dbPath = CreateTempDbPath();
        var timeProvider = new TestTimeProvider(DateTimeOffset.FromUnixTimeSeconds(1787000000));
        var cache = new MessageCache(dbPath, timeProvider);
        cache.Initialize();

        var scope = new CustomActivityScope((_, _) => true);
        var handler = new CaptureUpdateHandler(cache, scope: null, activityScope: scope, timeProvider: timeProvider, accountId: "7053823996");

        var updateJson = @"{
            ""@type"": ""updateUser"",
            ""user"": {
                ""id"": 666,
                ""first_name"": ""Bek"",
                ""last_name"": ""Aliyev"",
                ""is_contact"": true
            }
        }";

        handler.HandleUpdate(updateJson);
        handler.HandleUpdate(updateJson);
        handler.HandleUpdate(updateJson);

        Assert.Single(cache.GetOutboxRows("activity"));
    }

    [Fact]
    public void Test07_Two_different_fields_changing_in_same_second_both_rows_exist()
    {
        var dbPath = CreateTempDbPath();
        var timeProvider = new TestTimeProvider(DateTimeOffset.FromUnixTimeSeconds(1787000000));
        var cache = new MessageCache(dbPath, timeProvider);
        cache.Initialize();

        var scope = new CustomActivityScope((_, _) => true);
        var handler = new CaptureUpdateHandler(cache, scope: null, activityScope: scope, timeProvider: timeProvider, accountId: "7053823996");

        // First observation
        handler.HandleUpdate(@"{
            ""@type"": ""updateUser"",
            ""user"": {
                ""id"": 777,
                ""first_name"": ""OldName"",
                ""last_name"": """",
                ""status"": { ""@type"": ""userStatusOnline"", ""expires"": 1787000100 }
            }
        }");

        // Clear outbox to test exact simultaneous change
        var rowsInitial = cache.GetOutboxRows("activity");
        Assert.Equal(2, rowsInitial.Count); // initial name and status

        // Same second: now = 1787000050
        timeProvider.SetUtcNow(DateTimeOffset.FromUnixTimeSeconds(1787000050));

        handler.HandleUpdate(@"{
            ""@type"": ""updateUser"",
            ""user"": {
                ""id"": 777,
                ""first_name"": ""NewName"",
                ""last_name"": """"
            }
        }");

        handler.HandleUpdate(@"{
            ""@type"": ""updateUserStatus"",
            ""user_id"": 777,
            ""status"": { ""@type"": ""userStatusOffline"", ""was_online"": 1787000040 }
        }");

        var allRows = cache.GetOutboxRows("activity");
        // 2 initial + 2 new = 4 total rows
        Assert.Equal(4, allRows.Count);

        var newNameRow = allRows.First(r => r.MsgId == ActivityMapper.DiscriminatorFor("name") && r.OccurredAt == 1787000050);
        var newStatusRow = allRows.First(r => r.MsgId == ActivityMapper.DiscriminatorFor("status") && r.OccurredAt == 1787000050);

        Assert.NotEqual(newNameRow.MsgId, newStatusRow.MsgId);
        Assert.Contains("NewName", newNameRow.PayloadJson);
        Assert.Contains("offline:1787000040", newStatusRow.PayloadJson);
    }

    [Fact]
    public void Test08_Scope_layers_and_isolation_from_message_scope()
    {
        // 1. Exclude beats Include
        var configBuilder = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Capture:Activity:Exclude:0"] = "111",
            ["Capture:Activity:Include:0"] = "111",
            ["Capture:Activity:Include:1"] = "222",
            ["Capture:Activity:TrackAllContacts"] = "true",
            // Message scope has 111 allowed
            ["Capture:Scope:Allow:0"] = "111"
        });
        var config = configBuilder.Build();
        var evaluator = new ActivityScopeEvaluator(config);

        Assert.False(evaluator.ShouldTrackActivity("111", isContact: true)); // Exclude beats Include and TrackAllContacts
        Assert.True(evaluator.ShouldTrackActivity("222", isContact: false)); // Include beats not-a-contact
        Assert.True(evaluator.ShouldTrackActivity("333", isContact: true));  // TrackAllContacts for contact
        Assert.False(evaluator.ShouldTrackActivity("333", isContact: false)); // TrackAllContacts only for contacts

        // 2. Default config records nothing (fail-closed)
        var emptyConfig = new ConfigurationBuilder().Build();
        var defaultEvaluator = new ActivityScopeEvaluator(emptyConfig);
        Assert.False(defaultEvaluator.ShouldTrackActivity("111", isContact: true));
        Assert.False(defaultEvaluator.ShouldTrackActivity("111", isContact: false));

        // 3. Server layer beats synced snapshot
        var snapshotSource = new TestActivitySnapshotSource(new ActivityScopeSettingsSnapshot(
            Exclude: new HashSet<string> { "222" }, // Snapshot excludes 222
            Include: new HashSet<string> { "111" }, // Snapshot includes 111
            TrackAllContacts: false));

        var layeredEvaluator = new ActivityScopeEvaluator(config, snapshotSource);
        // Server Exclude beats Snapshot Include for 111
        Assert.False(layeredEvaluator.ShouldTrackActivity("111", isContact: true));
        // Server Include beats Snapshot Exclude for 222
        Assert.True(layeredEvaluator.ShouldTrackActivity("222", isContact: false));

        // 4. Activity lists do NOT affect message scope, and message lists do NOT affect activity scope
        var msgEvaluator = new CaptureScopeEvaluator(config);
        Assert.True(msgEvaluator.ShouldCache("111")); // 111 allowed in message scope
        Assert.False(evaluator.ShouldTrackActivity("111", isContact: true)); // but excluded in activity scope
    }

    private class TestActivitySnapshotSource(ActivityScopeSettingsSnapshot? snapshot) : ISyncedActivityScopeSettingsSource
    {
        public ActivityScopeSettingsSnapshot? CurrentSnapshot => snapshot;
    }

    [Fact]
    public void Test09_Preflight_validation_activity_rules()
    {
        Dictionary<string, string?> BaseConfig() => new()
        {
            ["Telegram:ApiId"] = "12345",
            ["Telegram:ApiHash"] = "hash",
            ["Telegram:DatabaseDirectory"] = Path.GetTempPath(),
            ["Telegram:FilesDirectory"] = Path.GetTempPath(),
        };

        // 1. Malformed entry
        var d1 = BaseConfig();
        d1["Capture:Activity:Exclude:0"] = "not_a_number";
        var c1 = new ConfigurationBuilder().AddInMemoryCollection(d1).Build();
        var r1 = CapturePreflight.Check(c1, nativeLibChecker: _ => true);
        Assert.False(r1.Passed);
        Assert.Contains(r1.Errors, e => e.Contains("Capture:Activity:Exclude") && !e.Contains("not_a_number"));

        // 2. Negative entry
        var d2 = BaseConfig();
        d2["Capture:Activity:Include:0"] = "-1002827825432";
        var c2 = new ConfigurationBuilder().AddInMemoryCollection(d2).Build();
        var r2 = CapturePreflight.Check(c2, nativeLibChecker: _ => true);
        Assert.False(r2.Passed);
        Assert.Contains(r2.Errors, e => e.Contains("Capture:Activity:Include") && !e.Contains("-1002827825432"));

        // 3. Overlapping entries
        var d3 = BaseConfig();
        d3["Capture:Activity:Exclude:0"] = "123456789";
        d3["Capture:Activity:Include:0"] = "123456789";
        var c3 = new ConfigurationBuilder().AddInMemoryCollection(d3).Build();
        var r3 = CapturePreflight.Check(c3, nativeLibChecker: _ => true);
        Assert.False(r3.Passed);
        Assert.Contains(r3.Errors, e => e.Contains("overlapping") && !e.Contains("123456789"));

        // 4. Non-boolean TrackAllContacts
        var d4 = BaseConfig();
        d4["Capture:Activity:TrackAllContacts"] = "yes_please";
        var c4 = new ConfigurationBuilder().AddInMemoryCollection(d4).Build();
        var r4 = CapturePreflight.Check(c4, nativeLibChecker: _ => true);
        Assert.False(r4.Passed);
        Assert.Contains(r4.Errors, e => e.Contains("Capture:Activity:TrackAllContacts"));

        // 5. Valid config
        var d5 = BaseConfig();
        d5["Capture:Activity:Exclude:0"] = "111";
        d5["Capture:Activity:Include:0"] = "222";
        d5["Capture:Activity:TrackAllContacts"] = "true";
        var c5 = new ConfigurationBuilder().AddInMemoryCollection(d5).Build();
        var r5 = CapturePreflight.Check(c5, nativeLibChecker: _ => true);
        Assert.True(r5.Passed);
    }

    [Fact]
    public void Test10_Production_wiring_records_activity()
    {
        var dbPath = CreateTempDbPath();
        var fakeTransport = new FakeTdTransport();

        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Capture:CacheDatabasePath"] = dbPath,
            ["Capture:Activity:Include:0"] = "888"
        }).Build();

        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(config);
        services.AddMessageCache(config);
        services.AddSingleton<ITdTransport>(fakeTransport);
        services.AddSingleton<ITdClient, TdClient>();
        services.AddCaptureHandlers();

        using var sp = services.BuildServiceProvider();

        var client = sp.GetRequiredService<ITdClient>();
        var cache = sp.GetRequiredService<MessageCache>();
        cache.Initialize();

        // Enqueue my_id
        fakeTransport.EnqueueIncoming(@"{ ""@type"": ""updateOption"", ""name"": ""my_id"", ""value"": { ""@type"": ""optionValueInteger"", ""value"": ""7053823996"" } }");

        // Enqueue activity for included user 888
        fakeTransport.EnqueueIncoming(@"{
            ""@type"": ""updateUser"",
            ""user"": {
                ""id"": 888,
                ""first_name"": ""ProductionUser"",
                ""last_name"": """",
                ""is_contact"": false
            }
        }");

        Thread.Sleep(200);

        var rows = cache.GetOutboxRows("activity");
        Assert.Single(rows);
        Assert.Equal("888", rows[0].PeerId);
        Assert.Equal("7053823996", rows[0].AccountId);
    }

    [Fact]
    public void Test11_Updates_before_account_id_is_known_are_not_lost()
    {
        var dbPath = CreateTempDbPath();
        var timeProvider = new TestTimeProvider(DateTimeOffset.FromUnixTimeSeconds(1787000000));
        var cache = new MessageCache(dbPath, timeProvider);
        cache.Initialize();

        var scope = new CustomActivityScope((_, _) => true);
        var handler = new CaptureUpdateHandler(cache, scope: null, activityScope: scope, timeProvider: timeProvider, accountId: null);

        // Update arrives before account_id is known
        handler.HandleUpdate(@"{
            ""@type"": ""updateUser"",
            ""user"": {
                ""id"": 999,
                ""first_name"": ""EarlyUser"",
                ""last_name"": """",
                ""is_contact"": false
            }
        }");

        // Outbox is empty because accountId is unknown
        Assert.Empty(cache.GetOutboxRows("activity"));

        // Now my_id is received -> flushes early queue
        handler.HandleUpdate(@"{
            ""@type"": ""updateOption"",
            ""name"": ""my_id"",
            ""value"": { ""@type"": ""optionValueInteger"", ""value"": ""7053823996"" }
        }");

        var rows = cache.GetOutboxRows("activity");
        Assert.Single(rows);
        Assert.Equal("7053823996", rows[0].AccountId);
        Assert.Equal("999", rows[0].PeerId);
        Assert.Contains("EarlyUser", rows[0].PayloadJson);
    }

    [Fact]
    public void Test12_Logs_contain_no_names_usernames_status_or_peer_ids()
    {
        var dbPath = CreateTempDbPath();
        var timeProvider = new TestTimeProvider(DateTimeOffset.FromUnixTimeSeconds(1787000000));
        var cache = new MessageCache(dbPath, timeProvider);
        cache.Initialize();

        var logger = new TestLogger<CaptureUpdateHandler>();
        var scope = new CustomActivityScope((_, _) => true);
        var handler = new CaptureUpdateHandler(cache, scope: null, activityScope: scope, timeProvider: timeProvider, logger: logger, accountId: "7053823996");

        const string secretName = "MaxfiyIsm";
        const string secretUsername = "maxfiy_username";
        const string secretStatus = "online:1787000999";
        const string watchedPeerId = "9876543210";

        handler.HandleUpdate($@"{{
            ""@type"": ""updateUser"",
            ""user"": {{
                ""id"": {watchedPeerId},
                ""first_name"": ""{secretName}"",
                ""last_name"": """",
                ""usernames"": {{ ""active_usernames"": [""{secretUsername}""] }},
                ""is_contact"": true,
                ""status"": {{ ""@type"": ""userStatusOnline"", ""expires"": 1787000999 }}
            }}
        }}");

        handler.HandleUpdate($@"{{
            ""@type"": ""updateUserStatus"",
            ""user_id"": {watchedPeerId},
            ""status"": {{ ""@type"": ""userStatusOnline"", ""expires"": 1787000999 }}
        }}");

        // Verify outbox has rows
        Assert.NotEmpty(cache.GetOutboxRows("activity"));

        // Verify no log entry contains sensitive data
        foreach (var entry in logger.Entries)
        {
            Assert.DoesNotContain(secretName, entry.Message);
            Assert.DoesNotContain(secretUsername, entry.Message);
            Assert.DoesNotContain(secretStatus, entry.Message);
            Assert.DoesNotContain(watchedPeerId, entry.Message);
        }
    }
}
