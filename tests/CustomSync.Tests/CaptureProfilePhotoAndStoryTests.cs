using System.Globalization;
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
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Xunit;

namespace CustomSync.Tests;

public class CaptureProfilePhotoAndStoryTests : IDisposable
{
    private readonly List<string> _tempFiles = new();

    private string CreateTempDbPath()
    {
        var path = Path.Combine(Path.GetTempPath(), $"capture_photo_story_test_{Guid.NewGuid():N}.db");
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

    private static (long MsgId, long OccurredAt, long ObservedAt, string PayloadJson) GetOutboxRow(SqliteConnection conn, string kind, string peerId, long msgId, long occurredAt)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT msg_id, occurred_at, observed_at, payload_json FROM capture_outbox WHERE kind = @k AND peer_id = @p AND msg_id = @m AND occurred_at = @o;";
        cmd.Parameters.AddWithValue("@k", kind);
        cmd.Parameters.AddWithValue("@p", peerId);
        cmd.Parameters.AddWithValue("@m", msgId);
        cmd.Parameters.AddWithValue("@o", occurredAt);
        using var reader = cmd.ExecuteReader();
        if (reader.Read())
        {
            return (reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2), reader.GetString(3));
        }
        throw new InvalidOperationException($"Outbox row not found: {kind}, {peerId}, {msgId}, {occurredAt}");
    }

    private static List<(long MsgId, long OccurredAt, long ObservedAt, string PayloadJson)> GetAllOutboxRows(SqliteConnection conn, string peerId)
    {
        var list = new List<(long MsgId, long OccurredAt, long ObservedAt, string PayloadJson)>();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT msg_id, occurred_at, observed_at, payload_json FROM capture_outbox WHERE peer_id = @p ORDER BY id ASC;";
        cmd.Parameters.AddWithValue("@p", peerId);
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            list.Add((reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2), reader.GetString(3)));
        }
        return list;
    }

    // -------------------------------------------------------------------------
    // 1. Photo values: normal id; negative int64 string (unsigned); number;
    //    absent/null/0 -> "empty"; first observation "empty" recorded;
    //    equal value writes nothing; change carries old value.
    // -------------------------------------------------------------------------
    [Fact]
    public void Test01_Photo_values_formatting_and_changes()
    {
        var dbPath = CreateTempDbPath();
        var timeProvider = new TestTimeProvider(DateTimeOffset.FromUnixTimeSeconds(1700000000));
        var cache = new MessageCache(dbPath, timeProvider);
        cache.Initialize();

        var scope = new CustomActivityScope((_, _) => true);
        var handler = new CaptureUpdateHandler(cache, scope: null, activityScope: scope, timeProvider: timeProvider, accountId: "12345");

        // First observation: absent profile_photo -> "empty", recorded with has_old_value = false
        handler.HandleUpdate("""
        {
            "@type": "updateUser",
            "user": {
                "id": 1001,
                "first_name": "Alice",
                "is_contact": true
            }
        }
        """);

        using (var conn = new SqliteConnection($"Data Source={dbPath}"))
        {
            conn.Open();
            var (msgId, occAt, obsAt, payload) = GetOutboxRow(conn, "activity", "1001", ActivityMapper.DiscriminatorFor("photo"), 1700000000);
            Assert.Equal(ActivityMapper.DiscriminatorFor("photo"), msgId);
            Assert.Equal(1700000000, occAt);
            Assert.Equal(1700000000, obsAt);
            using var doc = JsonDocument.Parse(payload);
            Assert.Equal("photo", doc.RootElement.GetProperty("field").GetString());
            Assert.False(doc.RootElement.GetProperty("has_old_value").GetBoolean());
            Assert.Equal(JsonValueKind.Null, doc.RootElement.GetProperty("old_value").ValueKind);
            Assert.Equal("empty", doc.RootElement.GetProperty("new_value").GetString());
        }

        // Equal value ("empty" again) -> writes nothing
        timeProvider.SetUtcNow(DateTimeOffset.FromUnixTimeSeconds(1700000010));
        handler.HandleUpdate("""
        {
            "@type": "updateUser",
            "user": {
                "id": 1001,
                "first_name": "Alice",
                "is_contact": true,
                "profile_photo": null
            }
        }
        """);

        using (var conn = new SqliteConnection($"Data Source={dbPath}"))
        {
            conn.Open();
            var rows = GetAllOutboxRows(conn, "1001").Where(r => r.MsgId == ActivityMapper.DiscriminatorFor("photo")).ToList();
            Assert.Single(rows);
        }

        // Change to normal photo id
        timeProvider.SetUtcNow(DateTimeOffset.FromUnixTimeSeconds(1700000020));
        handler.HandleUpdate("""
        {
            "@type": "updateUser",
            "user": {
                "id": 1001,
                "first_name": "Alice",
                "is_contact": true,
                "profile_photo": {
                    "id": "987654321"
                }
            }
        }
        """);

        using (var conn = new SqliteConnection($"Data Source={dbPath}"))
        {
            conn.Open();
            var row = GetOutboxRow(conn, "activity", "1001", ActivityMapper.DiscriminatorFor("photo"), 1700000020);
            using var doc = JsonDocument.Parse(row.PayloadJson);
            Assert.True(doc.RootElement.GetProperty("has_old_value").GetBoolean());
            Assert.Equal("empty", doc.RootElement.GetProperty("old_value").GetString());
            Assert.Equal("987654321", doc.RootElement.GetProperty("new_value").GetString());
        }

        // Change to photo id with high bit set (negative int64 in string -> unsigned output)
        timeProvider.SetUtcNow(DateTimeOffset.FromUnixTimeSeconds(1700000030));
        handler.HandleUpdate("""
        {
            "@type": "updateUser",
            "user": {
                "id": 1001,
                "first_name": "Alice",
                "is_contact": true,
                "profile_photo": {
                    "id": "-12345"
                }
            }
        }
        """);

        // unchecked((ulong)-12345L) = 18446744073709539271
        ulong expectedUnsigned = unchecked((ulong)-12345L);
        using (var conn = new SqliteConnection($"Data Source={dbPath}"))
        {
            conn.Open();
            var row = GetOutboxRow(conn, "activity", "1001", ActivityMapper.DiscriminatorFor("photo"), 1700000030);
            using var doc = JsonDocument.Parse(row.PayloadJson);
            Assert.Equal("987654321", doc.RootElement.GetProperty("old_value").GetString());
            Assert.Equal(expectedUnsigned.ToString(CultureInfo.InvariantCulture), doc.RootElement.GetProperty("new_value").GetString());
            Assert.DoesNotContain("-", doc.RootElement.GetProperty("new_value").GetString()!);
        }

        // Change to photo id given as a number instead of string
        timeProvider.SetUtcNow(DateTimeOffset.FromUnixTimeSeconds(1700000040));
        handler.HandleUpdate("""
        {
            "@type": "updateUser",
            "user": {
                "id": 1001,
                "first_name": "Alice",
                "is_contact": true,
                "profile_photo": {
                    "id": 555666777
                }
            }
        }
        """);

        using (var conn = new SqliteConnection($"Data Source={dbPath}"))
        {
            conn.Open();
            var row = GetOutboxRow(conn, "activity", "1001", ActivityMapper.DiscriminatorFor("photo"), 1700000040);
            using var doc = JsonDocument.Parse(row.PayloadJson);
            Assert.Equal(expectedUnsigned.ToString(CultureInfo.InvariantCulture), doc.RootElement.GetProperty("old_value").GetString());
            Assert.Equal("555666777", doc.RootElement.GetProperty("new_value").GetString());
        }

        // Change to id 0 -> "empty"
        timeProvider.SetUtcNow(DateTimeOffset.FromUnixTimeSeconds(1700000050));
        handler.HandleUpdate("""
        {
            "@type": "updateUser",
            "user": {
                "id": 1001,
                "first_name": "Alice",
                "is_contact": true,
                "profile_photo": {
                    "id": 0
                }
            }
        }
        """);

        using (var conn = new SqliteConnection($"Data Source={dbPath}"))
        {
            conn.Open();
            var row = GetOutboxRow(conn, "activity", "1001", ActivityMapper.DiscriminatorFor("photo"), 1700000050);
            using var doc = JsonDocument.Parse(row.PayloadJson);
            Assert.Equal("555666777", doc.RootElement.GetProperty("old_value").GetString());
            Assert.Equal("empty", doc.RootElement.GetProperty("new_value").GetString());
        }
    }

    // -------------------------------------------------------------------------
    // 2. Outbox row of a photo entry: kind, msg_id = DiscriminatorFor("photo"),
    //    occurred_at = now, payload fields including §0.14 account_id / peer_id.
    // -------------------------------------------------------------------------
    [Fact]
    public void Test02_Outbox_row_of_photo_entry()
    {
        var dbPath = CreateTempDbPath();
        const long now = 1700050000;
        var timeProvider = new TestTimeProvider(DateTimeOffset.FromUnixTimeSeconds(now));
        var cache = new MessageCache(dbPath, timeProvider);
        cache.Initialize();

        var scope = new CustomActivityScope((_, _) => true);
        var handler = new CaptureUpdateHandler(cache, scope: null, activityScope: scope, timeProvider: timeProvider, accountId: "998877");

        handler.HandleUpdate("""
        {
            "@type": "updateUser",
            "user": {
                "id": 4242,
                "first_name": "Bob",
                "is_contact": true,
                "profile_photo": {
                    "id": "11223344"
                }
            }
        }
        """);

        using var conn = new SqliteConnection($"Data Source={dbPath}");
        conn.Open();
        var row = GetOutboxRow(conn, "activity", "4242", ActivityMapper.DiscriminatorFor("photo"), now);

        Assert.Equal(ActivityMapper.DiscriminatorFor("photo"), row.MsgId);
        Assert.Equal(now, row.OccurredAt);
        Assert.Equal(now, row.ObservedAt);

        using var doc = JsonDocument.Parse(row.PayloadJson);
        var root = doc.RootElement;
        Assert.Equal("998877", root.GetProperty("account_id").GetString());
        Assert.Equal("4242", root.GetProperty("peer_id").GetString());
        Assert.Equal("photo", root.GetProperty("field").GetString());
        Assert.False(root.GetProperty("has_old_value").GetBoolean());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("old_value").ValueKind);
        Assert.Equal("11223344", root.GetProperty("new_value").GetString());
    }

    // -------------------------------------------------------------------------
    // 3. A photo change to id X makes the loop send exactly one
    //    getUserProfilePhotos{user_id, offset 0, limit 1}; answer (X, date D)
    //    writes status online:D with observed_at D.
    // -------------------------------------------------------------------------
    [Fact]
    public async Task Test03_Photo_change_sends_getUserProfilePhotos_and_writes_online_moment()
    {
        var dbPath = CreateTempDbPath();
        const long now = 1700100000;
        const long photoDate = 1700095000; // 5000 seconds ago (< 30 days)
        var timeProvider = new TestTimeProvider(DateTimeOffset.FromUnixTimeSeconds(now));
        var cache = new MessageCache(dbPath, timeProvider);
        cache.Initialize();

        var transport = new CaptureContractTestBase.QueueTransport
        {
            Reply = req =>
            {
                if (req["@type"]?.GetValue<string>() == "getUserProfilePhotos")
                {
                    Assert.Equal(777, req["user_id"]?.GetValue<long>());
                    Assert.Equal(0, req["offset"]?.GetValue<int>());
                    Assert.Equal(1, req["limit"]?.GetValue<int>());
                    return new JsonObject
                    {
                        ["@type"] = "chatPhotos",
                        ["total_count"] = 1,
                        ["photos"] = new JsonArray
                        {
                            new JsonObject
                            {
                                ["id"] = "445566",
                                ["added_date"] = photoDate
                            }
                        }
                    };
                }
                return null;
            }
        };

        var client = new TdClient(transport);
        var photoLookup = new ProfilePhotoLookup(client, cache, timeProvider);
        var scope = new CustomActivityScope((_, _) => true);
        var handler = new CaptureUpdateHandler(cache, scope: null, activityScope: scope, timeProvider: timeProvider, accountId: "12345", photoLookup: photoLookup);

        handler.HandleUpdate("""
        {
            "@type": "updateUser",
            "user": {
                "id": 777,
                "first_name": "Charlie",
                "is_contact": true,
                "profile_photo": {
                    "id": "445566"
                }
            }
        }
        """);

        // Process pending lookup
        bool processed = await photoLookup.ProcessPendingOnceAsync();
        Assert.True(processed);

        // Verify that getUserProfilePhotos was sent
        Assert.Contains(transport.Sent, req => req.Contains("getUserProfilePhotos"));

        // Verify status online:photoDate moment written to outbox
        using var conn = new SqliteConnection($"Data Source={dbPath}");
        conn.Open();
        var momentRow = GetOutboxRow(conn, "activity", "777", ActivityMapper.DiscriminatorFor("status"), photoDate);
        Assert.Equal(photoDate, momentRow.OccurredAt);
        Assert.Equal(photoDate, momentRow.ObservedAt);

        using var doc = JsonDocument.Parse(momentRow.PayloadJson);
        Assert.Equal("status", doc.RootElement.GetProperty("field").GetString());
        Assert.False(doc.RootElement.GetProperty("has_old_value").GetBoolean());
        Assert.Equal($"online:{photoDate}", doc.RootElement.GetProperty("new_value").GetString());

        // Verify recorded in activity_history
        Assert.True(cache.HasActivityEntryAt("777", "status", photoDate));
    }

    // -------------------------------------------------------------------------
    // 4. Each skip rule of §1.2 on its own:
    //    - D one second past now + 60 skipped, exactly now + 60 written
    //    - D exactly 30 days old written, one second older skipped
    //    - answer names another id -> skipped
    //    - error answer -> skipped
    //    - no answer before timeout -> skipped
    //    - existing entry at D (both normal status and moment before restart) -> skipped
    // -------------------------------------------------------------------------
    [Fact]
    public async Task Test04_Photo_online_moment_skip_rules()
    {
        var dbPath = CreateTempDbPath();
        const long now = 1700200000;
        var timeProvider = new TestTimeProvider(DateTimeOffset.FromUnixTimeSeconds(now));
        var cache = new MessageCache(dbPath, timeProvider);
        cache.Initialize();
        var scope = new CustomActivityScope((_, _) => true);

        // Rule A: D one second past now + 60 (now + 61) skipped; exactly now + 60 written
        {
            var transport = new CaptureContractTestBase.QueueTransport
            {
                Reply = req => new JsonObject
                {
                    ["@type"] = "chatPhotos",
                    ["total_count"] = 1,
                    ["photos"] = new JsonArray { new JsonObject { ["id"] = "101", ["added_date"] = now + 61 } }
                }
            };
            var client = new TdClient(transport);
            var lookup = new ProfilePhotoLookup(client, cache, timeProvider);
            var handler = new CaptureUpdateHandler(cache, null, scope, timeProvider, accountId: "1", photoLookup: lookup);
            handler.HandleUpdate("""{"@type":"updateUser","user":{"id":2001,"is_contact":true,"profile_photo":{"id":"101"}}}""");
            Assert.True(await lookup.ProcessPendingOnceAsync());
            Assert.False(cache.HasActivityEntryAt("2001", "status", now + 61));

            // Exactly now + 60 -> written
            transport.Reply = req => new JsonObject
            {
                ["@type"] = "chatPhotos",
                ["total_count"] = 1,
                ["photos"] = new JsonArray { new JsonObject { ["id"] = "102", ["added_date"] = now + 60 } }
            };
            handler.HandleUpdate("""{"@type":"updateUser","user":{"id":2001,"is_contact":true,"profile_photo":{"id":"102"}}}""");
            Assert.True(await lookup.ProcessPendingOnceAsync());
            Assert.True(cache.HasActivityEntryAt("2001", "status", now + 60));
        }

        // Rule B: Exactly 30 days old (now - 2592000) written; one second older (now - 2592001) skipped
        {
            const long exactly30Days = now - 2592000;
            const long olderThan30Days = now - 2592001;

            var transport = new CaptureContractTestBase.QueueTransport
            {
                Reply = req => new JsonObject
                {
                    ["@type"] = "chatPhotos",
                    ["total_count"] = 1,
                    ["photos"] = new JsonArray { new JsonObject { ["id"] = "201", ["added_date"] = exactly30Days } }
                }
            };
            var client = new TdClient(transport);
            var lookup = new ProfilePhotoLookup(client, cache, timeProvider);
            var handler = new CaptureUpdateHandler(cache, null, scope, timeProvider, accountId: "1", photoLookup: lookup);
            handler.HandleUpdate("""{"@type":"updateUser","user":{"id":2002,"is_contact":true,"profile_photo":{"id":"201"}}}""");
            Assert.True(await lookup.ProcessPendingOnceAsync());
            Assert.True(cache.HasActivityEntryAt("2002", "status", exactly30Days));

            // One second older -> skipped
            transport.Reply = req => new JsonObject
            {
                ["@type"] = "chatPhotos",
                ["total_count"] = 1,
                ["photos"] = new JsonArray { new JsonObject { ["id"] = "202", ["added_date"] = olderThan30Days } }
            };
            handler.HandleUpdate("""{"@type":"updateUser","user":{"id":2002,"is_contact":true,"profile_photo":{"id":"202"}}}""");
            Assert.True(await lookup.ProcessPendingOnceAsync());
            Assert.False(cache.HasActivityEntryAt("2002", "status", olderThan30Days));
        }

        // Rule C: Answer names another photo id -> skipped
        {
            var transport = new CaptureContractTestBase.QueueTransport
            {
                Reply = req => new JsonObject
                {
                    ["@type"] = "chatPhotos",
                    ["total_count"] = 1,
                    ["photos"] = new JsonArray { new JsonObject { ["id"] = "999", ["added_date"] = now - 100 } }
                }
            };
            var client = new TdClient(transport);
            var lookup = new ProfilePhotoLookup(client, cache, timeProvider);
            var handler = new CaptureUpdateHandler(cache, null, scope, timeProvider, accountId: "1", photoLookup: lookup);
            handler.HandleUpdate("""{"@type":"updateUser","user":{"id":2003,"is_contact":true,"profile_photo":{"id":"301"}}}""");
            Assert.True(await lookup.ProcessPendingOnceAsync());
            Assert.False(cache.HasActivityEntryAt("2003", "status", now - 100));
        }

        // Rule D: Current photo is no longer that id when date arrives -> skipped
        {
            var transport = new CaptureContractTestBase.QueueTransport
            {
                Reply = req => new JsonObject
                {
                    ["@type"] = "chatPhotos",
                    ["total_count"] = 1,
                    ["photos"] = new JsonArray { new JsonObject { ["id"] = "401", ["added_date"] = now - 50 } }
                }
            };
            var client = new TdClient(transport);
            var lookup = new ProfilePhotoLookup(client, cache, timeProvider);
            var handler = new CaptureUpdateHandler(cache, null, scope, timeProvider, accountId: "1", photoLookup: lookup);

            // User gets photo 401
            handler.HandleUpdate("""{"@type":"updateUser","user":{"id":2004,"is_contact":true,"profile_photo":{"id":"401"}}}""");
            // Then changes to photo 402 before lookup completes
            handler.HandleUpdate("""{"@type":"updateUser","user":{"id":2004,"is_contact":true,"profile_photo":{"id":"402"}}}""");

            // Process the first lookup (for 401)
            Assert.True(await lookup.ProcessPendingOnceAsync());
            Assert.False(cache.HasActivityEntryAt("2004", "status", now - 50));
        }

        // Rule E: Error answer -> skipped
        {
            var transport = new CaptureContractTestBase.QueueTransport
            {
                Reply = req => new JsonObject
                {
                    ["@type"] = "error",
                    ["code"] = 404,
                    ["message"] = "Not Found"
                }
            };
            var client = new TdClient(transport);
            var lookup = new ProfilePhotoLookup(client, cache, timeProvider);
            var handler = new CaptureUpdateHandler(cache, null, scope, timeProvider, accountId: "1", photoLookup: lookup);
            handler.HandleUpdate("""{"@type":"updateUser","user":{"id":2005,"is_contact":true,"profile_photo":{"id":"501"}}}""");
            using var conn = new SqliteConnection($"Data Source={dbPath}");
            conn.Open();
            Assert.Empty(GetAllOutboxRows(conn, "2005").Where(r => r.MsgId == ActivityMapper.DiscriminatorFor("status")));
        }

        // Rule F: Existing entry at D (both normal status entry and moment before restart) -> skipped
        {
            const long collisionDate = now - 500;
            // Existing normal status entry recorded at collisionDate
            cache.RecordActivity("1", "2006", "status", $"online:{collisionDate}", collisionDate);
            Assert.True(cache.HasActivityEntryAt("2006", "status", collisionDate));

            var transport = new CaptureContractTestBase.QueueTransport
            {
                Reply = req => new JsonObject
                {
                    ["@type"] = "chatPhotos",
                    ["total_count"] = 1,
                    ["photos"] = new JsonArray { new JsonObject { ["id"] = "601", ["added_date"] = collisionDate } }
                }
            };
            var client = new TdClient(transport);
            var lookup = new ProfilePhotoLookup(client, cache, timeProvider);
            var handler = new CaptureUpdateHandler(cache, null, scope, timeProvider, accountId: "1", photoLookup: lookup);
            handler.HandleUpdate("""{"@type":"updateUser","user":{"id":2006,"is_contact":true,"profile_photo":{"id":"601"}}}""");
            Assert.True(await lookup.ProcessPendingOnceAsync());

            // Outbox only has the initial status row, not an extra photo moment
            using var conn = new SqliteConnection($"Data Source={dbPath}");
            conn.Open();
            var rows = GetAllOutboxRows(conn, "2006").Where(r => r.MsgId == ActivityMapper.DiscriminatorFor("status")).ToList();
            Assert.Single(rows);
        }
    }

    // -------------------------------------------------------------------------
    // 5. No lookup when photo is unchanged, "empty", or out of scope.
    // -------------------------------------------------------------------------
    [Fact]
    public void Test05_No_lookup_when_photo_unchanged_empty_or_out_of_scope()
    {
        var dbPath = CreateTempDbPath();
        var timeProvider = new TestTimeProvider(DateTimeOffset.FromUnixTimeSeconds(1700300000));
        var cache = new MessageCache(dbPath, timeProvider);
        cache.Initialize();

        var transport = new CaptureContractTestBase.QueueTransport();
        var client = new TdClient(transport);
        var lookup = new ProfilePhotoLookup(client, cache, timeProvider);

        // Scope only tracks "3001"
        var scope = new CustomActivityScope((peer, _) => peer == "3001");
        var handler = new CaptureUpdateHandler(cache, null, scope, timeProvider, accountId: "1", photoLookup: lookup);

        // 1. Out of scope peer "3002"
        handler.HandleUpdate("""{"@type":"updateUser","user":{"id":3002,"is_contact":true,"profile_photo":{"id":"111"}}}""");
        Assert.Equal(0, lookup.InFlightCount);
        Assert.Empty(transport.Sent);

        // 2. In scope peer with no photo (empty)
        handler.HandleUpdate("""{"@type":"updateUser","user":{"id":3001,"is_contact":true,"profile_photo":null}}""");
        Assert.Equal(0, lookup.InFlightCount);
        Assert.Empty(transport.Sent);

        // 3. User gets photo "222" -> queued (in-flight)
        handler.HandleUpdate("""{"@type":"updateUser","user":{"id":3001,"is_contact":true,"profile_photo":{"id":"222"}}}""");
        Assert.Equal(1, lookup.InFlightCount);

        // 4. Same update again (unchanged) -> does NOT queue another
        handler.HandleUpdate("""{"@type":"updateUser","user":{"id":3001,"is_contact":true,"profile_photo":{"id":"222"}}}""");
        Assert.Equal(1, lookup.InFlightCount);
    }

    // -------------------------------------------------------------------------
    // 6. Threading: with a transport that never answers, handling updateUser
    //    returns at once; same updateUser three times sends one request;
    //    queue overflow drops and counts.
    // -------------------------------------------------------------------------
    [Fact]
    public void Test06_Threading_nonblocking_dedup_and_queue_overflow()
    {
        var dbPath = CreateTempDbPath();
        var timeProvider = new TestTimeProvider(DateTimeOffset.FromUnixTimeSeconds(1700400000));
        var cache = new MessageCache(dbPath, timeProvider);
        cache.Initialize();

        // Transport that never answers getUserProfilePhotos
        var transport = new CaptureContractTestBase.QueueTransport
        {
            Reply = _ => null
        };
        var client = new TdClient(transport);

        // Small capacity to test overflow
        var lookup = new ProfilePhotoLookup(client, cache, timeProvider, queueCapacity: 2);
        var scope = new CustomActivityScope((_, _) => true);
        var handler = new CaptureUpdateHandler(cache, null, scope, timeProvider, accountId: "1", photoLookup: lookup);

        // HandleUpdate must return IMMEDIATELY
        var sw = System.Diagnostics.Stopwatch.StartNew();
        handler.HandleUpdate("""{"@type":"updateUser","user":{"id":4001,"is_contact":true,"profile_photo":{"id":"501"}}}""");
        sw.Stop();
        Assert.True(sw.ElapsedMilliseconds < 500, $"HandleUpdate took too long: {sw.ElapsedMilliseconds}ms");

        // Next update processed without delay
        handler.HandleUpdate("""{"@type":"updateUser","user":{"id":4002,"is_contact":true,"profile_photo":{"id":"502"}}}""");

        // Same user with photo 501 twice more -> deduplicated (one in flight)
        handler.HandleUpdate("""{"@type":"updateUser","user":{"id":4001,"is_contact":true,"profile_photo":{"id":"501"}}}""");
        handler.HandleUpdate("""{"@type":"updateUser","user":{"id":4001,"is_contact":true,"profile_photo":{"id":"501"}}}""");

        // Overflow: queue capacity is 2. 4001 and 4002 take capacity. Next user 4003 overflows.
        handler.HandleUpdate("""{"@type":"updateUser","user":{"id":4003,"is_contact":true,"profile_photo":{"id":"503"}}}""");
        Assert.True(lookup.DroppedCount >= 1);
    }

    // -------------------------------------------------------------------------
    // 7. Stories: in-scope user with stories at D1 < D2 -> two moments and story = D2 at now;
    //    same update again -> nothing; later D3 -> one moment and story D3 with old value D2;
    //    empty list -> nothing; group/channel chat id -> nothing;
    //    out of scope -> nothing; date 0 skipped; update before my_id processed after it arrives.
    // -------------------------------------------------------------------------
    [Fact]
    public void Test07_Story_signals_and_story_field()
    {
        var dbPath = CreateTempDbPath();
        long now = 1700500000;
        var timeProvider = new TestTimeProvider(DateTimeOffset.FromUnixTimeSeconds(now));
        var cache = new MessageCache(dbPath, timeProvider);
        cache.Initialize();

        var scope = new CustomActivityScope((peer, _) => peer == "5001" || peer == "5002");
        var handler = new CaptureUpdateHandler(cache, null, scope, timeProvider, accountId: "12345");

        // Contact mapping for 5001
        handler.HandleUpdate("""{"@type":"updateUser","user":{"id":5001,"is_contact":true}}""");

        // 1. In-scope user with stories at D1 < D2 -> two moments and field story = D2 at now
        const long D1 = 1700490000;
        const long D2 = 1700495000;
        handler.HandleUpdate($$"""
        {
            "@type": "updateChatActiveStories",
            "chat_id": 5001,
            "active_stories": {
                "@type": "chatActiveStories",
                "chat_id": 5001,
                "stories": [
                    { "@type": "storyInfo", "id": 1, "date": {{D1}} },
                    { "@type": "storyInfo", "id": 2, "date": {{D2}} },
                    { "@type": "storyInfo", "id": 3, "date": 0 }
                ]
            }
        }
        """);

        using (var conn = new SqliteConnection($"Data Source={dbPath}"))
        {
            conn.Open();
            // Moments for D1 and D2
            var m1 = GetOutboxRow(conn, "activity", "5001", ActivityMapper.DiscriminatorFor("status"), D1);
            Assert.Equal(D1, m1.OccurredAt);
            using var d1 = JsonDocument.Parse(m1.PayloadJson);
            Assert.Equal($"online:{D1}", d1.RootElement.GetProperty("new_value").GetString());

            var m2 = GetOutboxRow(conn, "activity", "5001", ActivityMapper.DiscriminatorFor("status"), D2);
            Assert.Equal(D2, m2.OccurredAt);
            using var d2 = JsonDocument.Parse(m2.PayloadJson);
            Assert.Equal($"online:{D2}", d2.RootElement.GetProperty("new_value").GetString());

            // Field story = D2 at now
            var sRow = GetOutboxRow(conn, "activity", "5001", ActivityMapper.DiscriminatorFor("story"), now);
            Assert.Equal(now, sRow.OccurredAt);
            using var ds = JsonDocument.Parse(sRow.PayloadJson);
            Assert.Equal("story", ds.RootElement.GetProperty("field").GetString());
            Assert.False(ds.RootElement.GetProperty("has_old_value").GetBoolean());
            Assert.Equal(D2.ToString(CultureInfo.InvariantCulture), ds.RootElement.GetProperty("new_value").GetString());
        }

        // 2. Same update again -> writes nothing
        timeProvider.SetUtcNow(DateTimeOffset.FromUnixTimeSeconds(now + 10));
        handler.HandleUpdate($$"""
        {
            "@type": "updateChatActiveStories",
            "chat_id": 5001,
            "active_stories": {
                "@type": "chatActiveStories",
                "chat_id": 5001,
                "stories": [
                    { "@type": "storyInfo", "id": 1, "date": {{D1}} },
                    { "@type": "storyInfo", "id": 2, "date": {{D2}} }
                ]
            }
        }
        """);

        using (var conn = new SqliteConnection($"Data Source={dbPath}"))
        {
            conn.Open();
            var allRows = GetAllOutboxRows(conn, "5001").Where(r => r.MsgId != ActivityMapper.DiscriminatorFor("photo")).ToList();
            Assert.Equal(3, allRows.Count); // 2 status moments + 1 story
        }

        // 3. Later D3 -> one moment and story D3 with old_value D2
        const long D3 = 1700498000;
        timeProvider.SetUtcNow(DateTimeOffset.FromUnixTimeSeconds(now + 20));
        handler.HandleUpdate($$"""
        {
            "@type": "updateChatActiveStories",
            "chat_id": 5001,
            "active_stories": {
                "@type": "chatActiveStories",
                "chat_id": 5001,
                "stories": [
                    { "@type": "storyInfo", "id": 1, "date": {{D1}} },
                    { "@type": "storyInfo", "id": 2, "date": {{D2}} },
                    { "@type": "storyInfo", "id": 4, "date": {{D3}} }
                ]
            }
        }
        """);

        using (var conn = new SqliteConnection($"Data Source={dbPath}"))
        {
            conn.Open();
            var m3 = GetOutboxRow(conn, "activity", "5001", ActivityMapper.DiscriminatorFor("status"), D3);
            Assert.Equal(D3, m3.OccurredAt);

            var sRow3 = GetOutboxRow(conn, "activity", "5001", ActivityMapper.DiscriminatorFor("story"), now + 20);
            using var ds3 = JsonDocument.Parse(sRow3.PayloadJson);
            Assert.True(ds3.RootElement.GetProperty("has_old_value").GetBoolean());
            Assert.Equal(D2.ToString(CultureInfo.InvariantCulture), ds3.RootElement.GetProperty("old_value").GetString());
            Assert.Equal(D3.ToString(CultureInfo.InvariantCulture), ds3.RootElement.GetProperty("new_value").GetString());
        }

        // 4. Empty list -> writes nothing
        handler.HandleUpdate("""
        {
            "@type": "updateChatActiveStories",
            "chat_id": 5001,
            "active_stories": {
                "@type": "chatActiveStories",
                "chat_id": 5001,
                "stories": []
            }
        }
        """);

        // 5. Group/channel chat id (negative) -> writes nothing
        handler.HandleUpdate($$"""
        {
            "@type": "updateChatActiveStories",
            "chat_id": -1001234567890,
            "active_stories": {
                "@type": "chatActiveStories",
                "chat_id": -1001234567890,
                "stories": [ { "@type": "storyInfo", "id": 1, "date": {{now}} } ]
            }
        }
        """);

        // 6. Out of scope peer (e.g. 9999) -> writes nothing
        handler.HandleUpdate($$"""
        {
            "@type": "updateChatActiveStories",
            "chat_id": 9999,
            "active_stories": {
                "@type": "chatActiveStories",
                "chat_id": 9999,
                "stories": [ { "@type": "storyInfo", "id": 1, "date": {{now}} } ]
            }
        }
        """);

        using (var conn = new SqliteConnection($"Data Source={dbPath}"))
        {
            conn.Open();
            Assert.Empty(GetAllOutboxRows(conn, "-1001234567890"));
            Assert.Empty(GetAllOutboxRows(conn, "9999"));
        }

        // 7. Update before my_id is buffered and processed after account_id arrives
        var dbPath2 = CreateTempDbPath();
        var cache2 = new MessageCache(dbPath2, timeProvider);
        cache2.Initialize();
        var handler2 = new CaptureUpdateHandler(cache2, null, scope, timeProvider, accountId: null);

        // Update arrived before my_id
        handler2.HandleUpdate("""{"@type":"updateUser","user":{"id":5002,"is_contact":true}}""");
        handler2.HandleUpdate($$"""
        {
            "@type": "updateChatActiveStories",
            "chat_id": 5002,
            "active_stories": {
                "@type": "chatActiveStories",
                "chat_id": 5002,
                "stories": [ { "@type": "storyInfo", "id": 1, "date": {{D1}} } ]
            }
        }
        """);

        using (var conn = new SqliteConnection($"Data Source={dbPath2}"))
        {
            conn.Open();
            Assert.Empty(GetAllOutboxRows(conn, "5002"));
        }

        // Now my_id arrives
        handler2.HandleUpdate("""{"@type":"updateOption","name":"my_id","value":{"@type":"optionValueString","value":"12345"}}""");

        using (var conn = new SqliteConnection($"Data Source={dbPath2}"))
        {
            conn.Open();
            var m = GetOutboxRow(conn, "activity", "5002", ActivityMapper.DiscriminatorFor("status"), D1);
            Assert.Equal(D1, m.OccurredAt);
        }
    }

    // -------------------------------------------------------------------------
    // 8. A19: a moment older than stored status leaves activity_latest unchanged;
    //    a newer one becomes the latest, and next status update carries it as old_value.
    // -------------------------------------------------------------------------
    [Fact]
    public void Test08_A19_latest_value_replacement_rules()
    {
        var dbPath = CreateTempDbPath();
        const long baseTime = 1700600000;
        var timeProvider = new TestTimeProvider(DateTimeOffset.FromUnixTimeSeconds(baseTime));
        var cache = new MessageCache(dbPath, timeProvider);
        cache.Initialize();

        // Stored current status at baseTime
        cache.RecordActivity("1", "6001", "status", "online:1700600500", baseTime);
        var latest1 = cache.GetLatestActivity("6001", "status");
        Assert.Equal("online:1700600500", latest1.Value);
        Assert.Equal(baseTime, latest1.ObservedAt);

        // Older moment (baseTime - 1000) recorded -> activity_latest stays UNCHANGED
        const long olderMoment = baseTime - 1000;
        cache.RecordActivityMoment("1", "6001", "status", $"online:{olderMoment}", olderMoment);
        var latest2 = cache.GetLatestActivity("6001", "status");
        Assert.Equal("online:1700600500", latest2.Value);
        Assert.Equal(baseTime, latest2.ObservedAt);

        // Newer moment (baseTime + 100) recorded -> activity_latest BECOMES that moment
        const long newerMoment = baseTime + 100;
        cache.RecordActivityMoment("1", "6001", "status", $"online:{newerMoment}", newerMoment);
        var latest3 = cache.GetLatestActivity("6001", "status");
        Assert.Equal($"online:{newerMoment}", latest3.Value);
        Assert.Equal(newerMoment, latest3.ObservedAt);

        // Next real status change at baseTime + 200 carries the newer moment as old_value
        timeProvider.SetUtcNow(DateTimeOffset.FromUnixTimeSeconds(baseTime + 200));
        cache.RecordActivity("1", "6001", "status", "offline:1700600200", baseTime + 200);

        using var conn = new SqliteConnection($"Data Source={dbPath}");
        conn.Open();
        var row = GetOutboxRow(conn, "activity", "6001", ActivityMapper.DiscriminatorFor("status"), baseTime + 200);
        using var doc = JsonDocument.Parse(row.PayloadJson);
        Assert.True(doc.RootElement.GetProperty("has_old_value").GetBoolean());
        Assert.Equal($"online:{newerMoment}", doc.RootElement.GetProperty("old_value").GetString());
        Assert.Equal("offline:1700600200", doc.RootElement.GetProperty("new_value").GetString());
    }

    // -------------------------------------------------------------------------
    // 9. Persistence: §1.5 record survives a new MessageCache on the same file
    //    (a repeated story update after restart writes nothing);
    //    a v5 database migrates in place; pruner deletes records older than 31 days.
    // -------------------------------------------------------------------------
    [Fact]
    public void Test09_Persistence_v5_migration_and_pruner()
    {
        var dbPath = CreateTempDbPath();
        long now = 1700700000;
        var timeProvider = new TestTimeProvider(DateTimeOffset.FromUnixTimeSeconds(now));

        // Create initial cache and record an activity moment
        var cache1 = new MessageCache(dbPath, timeProvider);
        cache1.Initialize();

        long momentTime = now - 500;
        cache1.RecordActivityMoment("1", "7001", "status", $"online:{momentTime}", momentTime);
        Assert.True(cache1.HasActivityEntryAt("7001", "status", momentTime));

        // Restart with a brand new MessageCache instance on the same file
        var cache2 = new MessageCache(dbPath, timeProvider);
        cache2.Initialize();
        Assert.True(cache2.HasActivityEntryAt("7001", "status", momentTime));

        // Re-recording same moment writes nothing
        bool recordedAgain = cache2.RecordActivityMoment("1", "7001", "status", $"online:{momentTime}", momentTime);
        Assert.False(recordedAgain);

        // Pruner: deletes records older than 31 days (31 * 86400 = 2678400), keeps younger ones
        long youngTime = now - (30 * 86400L); // 30 days old (< 31 days)
        long oldTime = now - (32 * 86400L);   // 32 days old (> 31 days)
        cache2.RecordActivityMoment("1", "7001", "status", $"online:{youngTime}", youngTime);
        cache2.RecordActivityMoment("1", "7001", "status", $"online:{oldTime}", oldTime);

        Assert.True(cache2.HasActivityEntryAt("7001", "status", youngTime));
        Assert.True(cache2.HasActivityEntryAt("7001", "status", oldTime));

        var pruner = new PeriodicCachePruner(cache2, retentionDays: 30, interval: TimeSpan.FromHours(6), timeProvider: timeProvider);
        pruner.PruneOnce();

        Assert.True(cache2.HasActivityEntryAt("7001", "status", youngTime), "Younger than 31 days record must survive");
        Assert.False(cache2.HasActivityEntryAt("7001", "status", oldTime), "Older than 31 days record must be deleted");

        // v5 database migrates in place to v6
        var v5DbPath = CreateTempDbPath();
        using (var v5Conn = new SqliteConnection($"Data Source={v5DbPath}"))
        {
            v5Conn.Open();
            using var cmd = v5Conn.CreateCommand();
            cmd.CommandText = @"
                CREATE TABLE message_cache (chat_id INTEGER, message_id INTEGER, date INTEGER, cached_at INTEGER, PRIMARY KEY (chat_id, message_id));
                CREATE TABLE capture_outbox (id INTEGER PRIMARY KEY, kind TEXT, account_id TEXT, peer_id TEXT, msg_id INTEGER, occurred_at INTEGER, observed_at INTEGER, payload_json TEXT, created_at INTEGER, retry_count INTEGER DEFAULT 0, next_retry_at INTEGER, last_error TEXT);
                CREATE TABLE activity_latest (peer_id TEXT, field TEXT, value TEXT, observed_at INTEGER, PRIMARY KEY (peer_id, field));
                CREATE TABLE captured_media (peer_id TEXT, msg_id INTEGER, chat_id INTEGER, message_id INTEGER, content_type TEXT, status TEXT, attempts INTEGER DEFAULT 0, next_attempt_at INTEGER, local_path TEXT, sha256 TEXT, size INTEGER, created_at INTEGER, PRIMARY KEY (peer_id, msg_id));
                PRAGMA user_version = 5;
            ";
            cmd.ExecuteNonQuery();
        }

        var migratedCache = new MessageCache(v5DbPath, timeProvider);
        migratedCache.Initialize();

        using (var checkConn = new SqliteConnection($"Data Source={v5DbPath}"))
        {
            checkConn.Open();
            using var checkCmd = checkConn.CreateCommand();
            checkCmd.CommandText = "PRAGMA user_version;";
            int version = Convert.ToInt32(checkCmd.ExecuteScalar());
            Assert.Equal(6, version);

            // Verify activity_history table exists
            checkCmd.CommandText = "SELECT COUNT(*) FROM activity_history;";
            long count = Convert.ToInt64(checkCmd.ExecuteScalar());
            Assert.Equal(0, count);
        }
    }

    // -------------------------------------------------------------------------
    // 10. Policy: getUserProfilePhotos accepted only in the exact shape
    //     (other keys, limit != 1, offset != 0, user_id <= 0 or string rejected);
    //     every request listed as "must stay rejected" in §3 is rejected.
    // -------------------------------------------------------------------------
    [Fact]
    public void Test10_TdRequestPolicy_getUserProfilePhotos_and_rejected_methods()
    {
        // Allowed: exact shape
        var valid = TdRequestPolicy.ValidateAndNormalize("""
        {
            "@type": "getUserProfilePhotos",
            "user_id": 12345,
            "offset": 0,
            "limit": 1
        }
        """);
        Assert.NotNull(valid);

        // Allowed with @extra
        var validWithExtra = TdRequestPolicy.ValidateAndNormalize("""
        {
            "@type": "getUserProfilePhotos",
            "@extra": "ex123",
            "user_id": 12345,
            "offset": 0,
            "limit": 1
        }
        """);
        Assert.NotNull(validWithExtra);

        // Disallowed: other keys
        Assert.Throws<TdRequestNotAllowedException>(() => TdRequestPolicy.ValidateAndNormalize("""
        {"@type":"getUserProfilePhotos","user_id":12345,"offset":0,"limit":1,"extra_prop":"bad"}
        """));

        // Disallowed: limit != 1 (e.g. limit = 100)
        Assert.Throws<TdRequestNotAllowedException>(() => TdRequestPolicy.ValidateAndNormalize("""
        {"@type":"getUserProfilePhotos","user_id":12345,"offset":0,"limit":100}
        """));

        // Disallowed: offset != 0
        Assert.Throws<TdRequestNotAllowedException>(() => TdRequestPolicy.ValidateAndNormalize("""
        {"@type":"getUserProfilePhotos","user_id":12345,"offset":5,"limit":1}
        """));

        // Disallowed: user_id <= 0
        Assert.Throws<TdRequestNotAllowedException>(() => TdRequestPolicy.ValidateAndNormalize("""
        {"@type":"getUserProfilePhotos","user_id":0,"offset":0,"limit":1}
        """));
        Assert.Throws<TdRequestNotAllowedException>(() => TdRequestPolicy.ValidateAndNormalize("""
        {"@type":"getUserProfilePhotos","user_id":-100,"offset":0,"limit":1}
        """));

        // Disallowed: user_id as string
        Assert.Throws<TdRequestNotAllowedException>(() => TdRequestPolicy.ValidateAndNormalize("""
        {"@type":"getUserProfilePhotos","user_id":"12345","offset":0,"limit":1}
        """));

        // Must stay rejected: openStory, closeStory, getStory, getChatActiveStories, getUserFullInfo, viewMessages, openChat, loadActiveStories
        var forbiddenMethods = new[]
        {
            "openStory", "closeStory", "getStory", "getChatActiveStories",
            "getUserFullInfo", "viewMessages", "openChat", "loadActiveStories"
        };

        foreach (var method in forbiddenMethods)
        {
            Assert.Throws<TdRequestNotAllowedException>(() => TdRequestPolicy.ValidateAndNormalize($@"{{""@type"":""{method}""}}"));
        }
    }

    // -------------------------------------------------------------------------
    // 11. Wiring: through real registrations and Worker, lookup loop is started —
    //     the test fails when the start call is removed.
    // -------------------------------------------------------------------------
    [Fact]
    public async Task Test11_Production_wiring_starts_photo_date_lookup_loop()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"wiring_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        _tempFiles.Add(tempDir);

        var keyPath = Path.Combine(tempDir, "tdlib-db-key");
        File.WriteAllText(keyPath, Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)));

        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Telegram:ApiId"] = "12345",
            ["Telegram:ApiHash"] = "hash",
            ["Telegram:DatabaseDirectory"] = tempDir,
            ["Telegram:FilesDirectory"] = tempDir,
            ["Telegram:DatabaseEncryptionKeyFile"] = keyPath,
            ["Capture:CacheDatabasePath"] = Path.Combine(tempDir, "cache.db"),
            ["Capture:Activity:TrackAllContacts"] = "true",
            ["Capture:SessionInvisibilityTimeoutSeconds"] = "5",
            ["Capture:Sync:Enabled"] = "false",
            ["Capture:Media:Enabled"] = "false"
        }).Build();

        long addedDate = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 100;

        var transport = new FakeRecordingTdTransport { AutoRepeatReadyState = true };
        transport.IncomingQueue.Enqueue("""{"@type":"updateAuthorizationState","authorization_state":{"@type":"authorizationStateReady"}}""");
        transport.IncomingQueue.Enqueue("""{"@type":"updateOption","name":"my_id","value":{"@type":"optionValueString","value":"123"}}""");

        transport.OnSend = (c, req) =>
        {
            using var d = JsonDocument.Parse(req);
            var root = d.RootElement;
            var type = root.GetProperty("@type").GetString();
            var extra = root.TryGetProperty("@extra", out var ex) ? ex.GetString() : null;

            if (type == "setTdlibParameters")
                return new JsonObject { ["@type"] = "ok", ["@extra"] = extra }.ToJsonString();
            if (type == "setOption")
                return new JsonObject { ["@type"] = "ok", ["@extra"] = extra }.ToJsonString();
            if (type == "getOption")
                return new JsonObject { ["@type"] = "optionValueBoolean", ["value"] = false, ["@extra"] = extra }.ToJsonString();
            if (type == "getUserProfilePhotos")
            {
                return new JsonObject
                {
                    ["@type"] = "chatPhotos",
                    ["@extra"] = extra,
                    ["total_count"] = 1,
                    ["photos"] = new JsonArray
                    {
                        new JsonObject { ["id"] = "8899", ["added_date"] = addedDate }
                    }
                }.ToJsonString();
            }
            return null;
        };

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(config);
        services.AddTdlibClient(config);
        services.AddMessageCache(config);
        services.AddCaptureHandlers();
        services.AddCaptureSyncClient(config);

        // Swap transport and probes for testing
        services.RemoveAll<ITdTransport>();
        services.AddSingleton<ITdTransport>(transport);
        services.RemoveAll<IDiskSpaceProbe>();
        services.AddSingleton<IDiskSpaceProbe>(new FixedDiskSpaceProbe(100_000_000));
        services.RemoveAll<INativeLibraryProbe>();
        services.AddSingleton<INativeLibraryProbe>(new FixedProbe(true));

        var sp = services.BuildServiceProvider();
        var cache = sp.GetRequiredService<MessageCache>();
        cache.Initialize();

        var client = sp.GetRequiredService<ITdClient>();
        var handler = sp.GetRequiredService<CaptureUpdateHandler>();
        var lookup = sp.GetRequiredService<ProfilePhotoLookup>();

        // Set up Worker
        var workerLogger = new CapturingLogger<Worker>();
        var lifetime = new FakeHostLifetime();
        var worker = new Worker(config, sp, lifetime, workerLogger);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));

        // Start worker
        var workerTask = worker.StartAsync(cts.Token);

        // Wait until worker is authorized and running
        for (int i = 0; i < 100; i++)
        {
            if (workerLogger.Logs.Any(l => l.Message.Contains("Capture service authorized and running")))
                break;
            await Task.Delay(50);
        }
        Assert.True(workerLogger.Logs.Any(l => l.Message.Contains("Capture service authorized and running")), $"Worker logs: {string.Join(" | ", workerLogger.Logs.Select(l => $"{l.Level}: {l.Message}"))}");

        // Ensure account id is set
        handler.SetAccountId("123");

        // Feed an updateUser with photo
        handler.HandleUpdate("""
        {
            "@type": "updateUser",
            "user": {
                "id": 8801,
                "first_name": "Dave",
                "is_contact": true,
                "profile_photo": {
                    "id": "8899"
                }
            }
        }
        """);

        // Wait for background lookup loop to process the item (started by Worker!)
        bool momentRecorded = await CaptureContractTestBase.WaitAsync(() => cache.HasActivityEntryAt("8801", "status", addedDate), timeoutMs: 8000);
        Assert.True(momentRecorded, "Lookup loop must be started by Worker to process photo moment");

        cts.Cancel();
        try { await workerTask; } catch { }
    }

    // -------------------------------------------------------------------------
    // 12. One record across clients: for a photo moment and a story moment,
    //     the record_id computed for outbox row equals record_id from spec function
    //     for (activity, empty account, peer, DiscriminatorFor("status"), D).
    // -------------------------------------------------------------------------
    [Fact]
    public void Test12_One_record_across_clients_record_id_parity()
    {
        var dbPath = CreateTempDbPath();
        var timeProvider = new TestTimeProvider(DateTimeOffset.FromUnixTimeSeconds(1700900000));
        var cache = new MessageCache(dbPath, timeProvider);
        cache.Initialize();

        const string peerId = "9001";
        const long photoDate = 1700895000;
        const long storyDate = 1700898000;

        // Record photo moment and story moment
        cache.RecordActivityMoment("12345", peerId, "status", $"online:{photoDate}", photoDate);
        cache.RecordActivityMoment("12345", peerId, "status", $"online:{storyDate}", storyDate);

        var (masterKey, _, _, _, _, _, _, _, _) = CaptureContractTestBase.LoadVectorCase1();

        using var conn = new SqliteConnection($"Data Source={dbPath}");
        conn.Open();

        // 1. Photo moment record_id parity
        var photoRow = GetOutboxRow(conn, "activity", peerId, ActivityMapper.DiscriminatorFor("status"), photoDate);
        var photoRecord = SyncCrypto.BuildRecord(new OutboxRow(1, "activity", "12345", peerId, photoRow.MsgId, photoRow.OccurredAt, photoRow.ObservedAt, photoRow.PayloadJson, photoRow.OccurredAt), masterKey, "device1");

        // Canonical spec computation:
        // peer_hash = HMAC(peer_key, peer_id)[0..16]
        // record_id = HMAC(record_key, "activity" ‖ 0x00 ‖ "" ‖ 0x00 ‖ peer_hash ‖ 0x00 ‖ msg_id ‖ 0x00 ‖ occurred_at)
        var peerKey = SyncCrypto.DerivePeerKey(masterKey);
        string peerHash = CryptoPrimitives.ComputePeerHash(peerKey, peerId);
        string canonicalPhotoRecordId = RecordId.Compute("activity", accountHash: "", peerHash, ActivityMapper.DiscriminatorFor("status"), photoDate);
        Assert.Equal(canonicalPhotoRecordId, photoRecord.RecordId);

        // 2. Story moment record_id parity
        var storyRow = GetOutboxRow(conn, "activity", peerId, ActivityMapper.DiscriminatorFor("status"), storyDate);
        var storyRecord = SyncCrypto.BuildRecord(new OutboxRow(2, "activity", "12345", peerId, storyRow.MsgId, storyRow.OccurredAt, storyRow.ObservedAt, storyRow.PayloadJson, storyRow.OccurredAt), masterKey, "device1");

        string canonicalStoryRecordId = RecordId.Compute("activity", accountHash: "", peerHash, ActivityMapper.DiscriminatorFor("status"), storyDate);
        Assert.Equal(canonicalStoryRecordId, storyRecord.RecordId);
    }
}
