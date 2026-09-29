using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using CustomSync.Capture.Capture;
using CustomSync.Capture.Tdlib;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace CustomSync.Tests;

public class CaptureEditDateTests : IDisposable
{
    private readonly List<string> _tempFiles = new();

    private string CreateTempDbPath()
    {
        var path = Path.Combine(Path.GetTempPath(), $"capture_edit_date_test_{Guid.NewGuid():N}.db");
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

    private static void RespondTo(FakeTdTransport transport, string requestType, Func<JsonObject, string> response)
    {
        var previous = transport.OnSend;
        transport.OnSend = (clientId, json) =>
        {
            previous?.Invoke(clientId, json);
            var req = JsonNode.Parse(json)!.AsObject();
            if (req["@type"]!.GetValue<string>() != requestType) return;
            var resp = JsonNode.Parse(response(req))!.AsObject();
            resp["@extra"] = req["@extra"]!.GetValue<string>();
            transport.EnqueueIncoming(resp.ToJsonString());
        };
    }

    [Fact]
    public void Test01_Content_then_edited_emits_one_row_occurred_at_edit_date_exact_payload()
    {
        var dbPath = CreateTempDbPath();
        var cache = new MessageCache(dbPath);
        cache.Initialize();

        var scope = new AllowAllCaptureScope();
        var handler = new CaptureUpdateHandler(cache, scope, accountId: "7053823996");

        long chatId = -1002827825432L; // peer "562952781246744"
        long srvMsgId = 100L;
        long sendDate = 1787000000L;
        long editDate = 1787000010L;

        cache.Put(new CachedMessage(chatId, srvMsgId, "Old Text", "7053823996", false, false, null, sendDate, sendDate));

        // 1. Content arrives first
        handler.HandleUpdate($@"{{
            ""@type"": ""updateMessageContent"",
            ""chat_id"": {chatId},
            ""message_id"": {srvMsgId << 20},
            ""new_content"": {{
                ""@type"": ""messageText"",
                ""text"": {{ ""text"": ""New Text"" }}
            }}
        }}");

        // Staged, not emitted yet!
        Assert.Empty(cache.GetOutboxRows());

        // 2. Edited arrives second
        handler.HandleUpdate($@"{{
            ""@type"": ""updateMessageEdited"",
            ""chat_id"": {chatId},
            ""message_id"": {srvMsgId << 20},
            ""edit_date"": {editDate}
        }}");

        var rows = cache.GetOutboxRows();
        Assert.Single(rows);
        var row = rows[0];

        Assert.Equal("edited", row.Kind);
        Assert.Equal("7053823996", row.AccountId);
        Assert.Equal("562952781246744", row.PeerId);
        Assert.Equal(srvMsgId, row.MsgId);
        Assert.Equal(editDate, row.OccurredAt);

        var payload = JsonNode.Parse(row.PayloadJson)!.AsObject();
        Assert.Equal("7053823996", payload["account_id"]!.GetValue<string>());
        Assert.Equal("562952781246744", payload["peer_id"]!.GetValue<string>());
        Assert.Equal("Old Text", payload["old_text"]!.GetValue<string>());
        Assert.Equal("New Text", payload["new_text"]!.GetValue<string>());
        Assert.False(payload["is_out"]!.GetValue<bool>());

        var updated = cache.Get(chatId, srvMsgId);
        Assert.NotNull(updated);
        Assert.Equal("New Text", updated.Text);
    }

    [Fact]
    public void Test02_Edited_then_content_emits_same_row()
    {
        var dbPath = CreateTempDbPath();
        var cache = new MessageCache(dbPath);
        cache.Initialize();

        var scope = new AllowAllCaptureScope();
        var handler = new CaptureUpdateHandler(cache, scope, accountId: "7053823996");

        long chatId = -1002827825432L;
        long srvMsgId = 100L;
        long sendDate = 1787000000L;
        long editDate = 1787000010L;

        cache.Put(new CachedMessage(chatId, srvMsgId, "Old Text", "7053823996", false, false, null, sendDate, sendDate));

        // 1. Edited arrives first
        handler.HandleUpdate($@"{{
            ""@type"": ""updateMessageEdited"",
            ""chat_id"": {chatId},
            ""message_id"": {srvMsgId << 20},
            ""edit_date"": {editDate}
        }}");

        Assert.Empty(cache.GetOutboxRows());

        // 2. Content arrives second
        handler.HandleUpdate($@"{{
            ""@type"": ""updateMessageContent"",
            ""chat_id"": {chatId},
            ""message_id"": {srvMsgId << 20},
            ""new_content"": {{
                ""@type"": ""messageText"",
                ""text"": {{ ""text"": ""New Text"" }}
            }}
        }}");

        var rows = cache.GetOutboxRows();
        Assert.Single(rows);
        var row = rows[0];

        Assert.Equal("edited", row.Kind);
        Assert.Equal("7053823996", row.AccountId);
        Assert.Equal("562952781246744", row.PeerId);
        Assert.Equal(srvMsgId, row.MsgId);
        Assert.Equal(editDate, row.OccurredAt);

        var payload = JsonNode.Parse(row.PayloadJson)!.AsObject();
        Assert.Equal("Old Text", payload["old_text"]!.GetValue<string>());
        Assert.Equal("New Text", payload["new_text"]!.GetValue<string>());
    }

    [Fact]
    public void Test03_Chained_edits_A_to_B_to_C_emits_two_rows_matching_test_vectors()
    {
        var dbPath = CreateTempDbPath();
        var cache = new MessageCache(dbPath);
        cache.Initialize();

        var scope = new AllowAllCaptureScope();
        var handler = new CaptureUpdateHandler(cache, scope, accountId: "111222333");

        long chatId = 7053823996L; // peer "7053823996"
        long srvMsgId = 390234L;
        long sendDate = 1787000000L;
        long editDate1 = 1787000010L;
        long editDate2 = 1787000020L;

        cache.Put(new CachedMessage(chatId, srvMsgId, "A", "111222333", false, false, null, sendDate, sendDate));

        // Edit 1: A -> B with edit_date 1787000010
        handler.HandleUpdate($@"{{
            ""@type"": ""updateMessageContent"",
            ""chat_id"": {chatId},
            ""message_id"": {srvMsgId << 20},
            ""new_content"": {{ ""@type"": ""messageText"", ""text"": {{ ""text"": ""B"" }} }}
        }}");
        handler.HandleUpdate($@"{{
            ""@type"": ""updateMessageEdited"",
            ""chat_id"": {chatId},
            ""message_id"": {srvMsgId << 20},
            ""edit_date"": {editDate1}
        }}");

        // Edit 2: B -> C with edit_date 1787000020
        handler.HandleUpdate($@"{{
            ""@type"": ""updateMessageContent"",
            ""chat_id"": {chatId},
            ""message_id"": {srvMsgId << 20},
            ""new_content"": {{ ""@type"": ""messageText"", ""text"": {{ ""text"": ""C"" }} }}
        }}");
        handler.HandleUpdate($@"{{
            ""@type"": ""updateMessageEdited"",
            ""chat_id"": {chatId},
            ""message_id"": {srvMsgId << 20},
            ""edit_date"": {editDate2}
        }}");

        var rows = cache.GetOutboxRows();
        Assert.Equal(2, rows.Count);

        var r1 = rows[0];
        Assert.Equal("edited", r1.Kind);
        Assert.Equal(srvMsgId, r1.MsgId);
        Assert.Equal(editDate1, r1.OccurredAt);
        var p1 = JsonNode.Parse(r1.PayloadJson)!.AsObject();
        Assert.Equal("A", p1["old_text"]!.GetValue<string>());
        Assert.Equal("B", p1["new_text"]!.GetValue<string>());

        var r2 = rows[1];
        Assert.Equal("edited", r2.Kind);
        Assert.Equal(srvMsgId, r2.MsgId);
        Assert.Equal(editDate2, r2.OccurredAt);
        var p2 = JsonNode.Parse(r2.PayloadJson)!.AsObject();
        Assert.Equal("B", p2["old_text"]!.GetValue<string>());
        Assert.Equal("C", p2["new_text"]!.GetValue<string>());
    }

    [Fact]
    public void Test04_Restart_between_two_halves_emits_with_right_old_text()
    {
        var dbPath = CreateTempDbPath();
        {
            var cache1 = new MessageCache(dbPath);
            cache1.Initialize();
            var handler1 = new CaptureUpdateHandler(cache1, new AllowAllCaptureScope(), accountId: "7053823996");

            long chatId = -1002827825432L;
            long srvMsgId = 100L;
            cache1.Put(new CachedMessage(chatId, srvMsgId, "Old Text", "7053823996", false, false, null, 1787000000L));

            // Content arrives on handler1
            handler1.HandleUpdate($@"{{
                ""@type"": ""updateMessageContent"",
                ""chat_id"": {chatId},
                ""message_id"": {srvMsgId << 20},
                ""new_content"": {{ ""@type"": ""messageText"", ""text"": {{ ""text"": ""New Text"" }} }}
            }}");

            Assert.Empty(cache1.GetOutboxRows());
        }

        // Simulate service restart: create fresh cache and handler pointing to same DB
        {
            var cache2 = new MessageCache(dbPath);
            cache2.Initialize();
            var handler2 = new CaptureUpdateHandler(cache2, new AllowAllCaptureScope(), accountId: "7053823996");

            long chatId = -1002827825432L;
            long srvMsgId = 100L;
            long editDate = 1787000010L;

            // Edited arrives on handler2
            handler2.HandleUpdate($@"{{
                ""@type"": ""updateMessageEdited"",
                ""chat_id"": {chatId},
                ""message_id"": {srvMsgId << 20},
                ""edit_date"": {editDate}
            }}");

            var rows = cache2.GetOutboxRows();
            Assert.Single(rows);
            Assert.Equal(editDate, rows[0].OccurredAt);
            var payload = JsonNode.Parse(rows[0].PayloadJson)!.AsObject();
            Assert.Equal("Old Text", payload["old_text"]!.GetValue<string>());
            Assert.Equal("New Text", payload["new_text"]!.GetValue<string>());
        }
    }

    [Fact]
    public void Test05_Content_with_no_edited_emits_fallback_row_after_timeout()
    {
        var dbPath = CreateTempDbPath();
        var timeProvider = new TestTimeProvider(DateTimeOffset.FromUnixTimeSeconds(1787000000));
        var cache = new MessageCache(dbPath, timeProvider);
        cache.Initialize();

        var handler = new CaptureUpdateHandler(cache, new AllowAllCaptureScope(), timeProvider, accountId: "7053823996", editPairingTimeoutSeconds: 60);

        long chatId = -1002827825432L;
        long srvMsgId = 100L;
        long sendDate = 1787000005L;

        cache.Put(new CachedMessage(chatId, srvMsgId, "Old Text", "7053823996", false, false, null, sendDate));

        handler.HandleUpdate($@"{{
            ""@type"": ""updateMessageContent"",
            ""chat_id"": {chatId},
            ""message_id"": {srvMsgId << 20},
            ""new_content"": {{ ""@type"": ""messageText"", ""text"": {{ ""text"": ""New Text"" }} }}
        }}");

        Assert.Empty(cache.GetOutboxRows());

        // Advance 30 seconds (before timeout of 60s)
        timeProvider.Advance(TimeSpan.FromSeconds(30));
        handler.HandleUpdate($@"{{ ""@type"": ""updateUserStatus"", ""user_id"": 1, ""status"": {{ ""@type"": ""userStatusEmpty"" }} }}");
        Assert.Empty(cache.GetOutboxRows());
        Assert.Equal(0, handler.UnpairedEditCount);

        // Advance past timeout (total +65s)
        timeProvider.Advance(TimeSpan.FromSeconds(35));
        handler.HandleUpdate($@"{{ ""@type"": ""updateUserStatus"", ""user_id"": 1, ""status"": {{ ""@type"": ""userStatusEmpty"" }} }}");

        var rows = cache.GetOutboxRows();
        Assert.Single(rows);
        Assert.Equal(sendDate, rows[0].OccurredAt); // fallback occurred_at = msg date!
        Assert.Equal(1, handler.UnpairedEditCount);
    }

    [Fact]
    public void Test06_Edited_with_no_text_change_discards_pending_state_after_timeout()
    {
        var dbPath = CreateTempDbPath();
        var timeProvider = new TestTimeProvider(DateTimeOffset.FromUnixTimeSeconds(1787000000));
        var cache = new MessageCache(dbPath, timeProvider);
        cache.Initialize();

        var handler = new CaptureUpdateHandler(cache, new AllowAllCaptureScope(), timeProvider, accountId: "7053823996", editPairingTimeoutSeconds: 60);

        long chatId = -1002827825432L;
        long srvMsgId = 100L;

        cache.Put(new CachedMessage(chatId, srvMsgId, "Same Text", "7053823996", false, false, null, 1787000000L));

        // updateMessageEdited arrives without text change
        handler.HandleUpdate($@"{{
            ""@type"": ""updateMessageEdited"",
            ""chat_id"": {chatId},
            ""message_id"": {srvMsgId << 20},
            ""edit_date"": 1787000010
        }}");

        Assert.Empty(cache.GetOutboxRows());

        // Advance past timeout
        timeProvider.Advance(TimeSpan.FromSeconds(65));
        handler.HandleUpdate($@"{{ ""@type"": ""updateUserStatus"", ""user_id"": 1, ""status"": {{ ""@type"": ""userStatusEmpty"" }} }}");

        // Still no row emitted, and pending state discarded
        Assert.Empty(cache.GetOutboxRows());
        Assert.Equal(0, handler.UnpairedEditCount);
    }

    [Fact]
    public void Test07_Edit_date_zero_uses_fallback_msg_date()
    {
        var dbPath = CreateTempDbPath();
        var cache = new MessageCache(dbPath);
        cache.Initialize();

        var handler = new CaptureUpdateHandler(cache, new AllowAllCaptureScope(), accountId: "7053823996");

        long chatId = -1002827825432L;
        long srvMsgId = 100L;
        long sendDate = 1787000005L;

        cache.Put(new CachedMessage(chatId, srvMsgId, "Old Text", "7053823996", false, false, null, sendDate));

        handler.HandleUpdate($@"{{
            ""@type"": ""updateMessageContent"",
            ""chat_id"": {chatId},
            ""message_id"": {srvMsgId << 20},
            ""new_content"": {{ ""@type"": ""messageText"", ""text"": {{ ""text"": ""New Text"" }} }}
        }}");

        // edit_date = 0
        handler.HandleUpdate($@"{{
            ""@type"": ""updateMessageEdited"",
            ""chat_id"": {chatId},
            ""message_id"": {srvMsgId << 20},
            ""edit_date"": 0
        }}");

        var rows = cache.GetOutboxRows();
        Assert.Single(rows);
        Assert.Equal(sendDate, rows[0].OccurredAt); // fallback to msg_date
    }

    [Fact]
    public void Test08_Replaying_either_update_produces_no_second_row()
    {
        var dbPath = CreateTempDbPath();
        var cache = new MessageCache(dbPath);
        cache.Initialize();

        var handler = new CaptureUpdateHandler(cache, new AllowAllCaptureScope(), accountId: "7053823996");

        long chatId = -1002827825432L;
        long srvMsgId = 100L;
        long editDate = 1787000010L;

        cache.Put(new CachedMessage(chatId, srvMsgId, "Old Text", "7053823996", false, false, null, 1787000000L));

        var contentUpdate = $@"{{
            ""@type"": ""updateMessageContent"",
            ""chat_id"": {chatId},
            ""message_id"": {srvMsgId << 20},
            ""new_content"": {{ ""@type"": ""messageText"", ""text"": {{ ""text"": ""New Text"" }} }}
        }}";
        var editedUpdate = $@"{{
            ""@type"": ""updateMessageEdited"",
            ""chat_id"": {chatId},
            ""message_id"": {srvMsgId << 20},
            ""edit_date"": {editDate}
        }}";

        handler.HandleUpdate(contentUpdate);
        handler.HandleUpdate(editedUpdate);

        Assert.Single(cache.GetOutboxRows());

        // Replay content update
        handler.HandleUpdate(contentUpdate);
        Assert.Single(cache.GetOutboxRows());

        // Replay edited update
        handler.HandleUpdate(editedUpdate);
        Assert.Single(cache.GetOutboxRows());
    }

    [Fact]
    public void Test09_Same_message_id_in_two_chats_edited_at_once_not_cross_paired()
    {
        var dbPath = CreateTempDbPath();
        var cache = new MessageCache(dbPath);
        cache.Initialize();

        var handler = new CaptureUpdateHandler(cache, new AllowAllCaptureScope(), accountId: "7053823996");

        long chat1 = 1001L; // peer "1001"
        long chat2 = 2002L; // peer "2002"
        long srvMsgId = 50L;

        cache.Put(new CachedMessage(chat1, srvMsgId, "C1 Old", "7053823996", false, false, null, 1787000000L));
        cache.Put(new CachedMessage(chat2, srvMsgId, "C2 Old", "7053823996", false, false, null, 1787000000L));

        // Content for both chats arrives
        handler.HandleUpdate($@"{{
            ""@type"": ""updateMessageContent"",
            ""chat_id"": {chat1},
            ""message_id"": {srvMsgId << 20},
            ""new_content"": {{ ""@type"": ""messageText"", ""text"": {{ ""text"": ""C1 New"" }} }}
        }}");
        handler.HandleUpdate($@"{{
            ""@type"": ""updateMessageContent"",
            ""chat_id"": {chat2},
            ""message_id"": {srvMsgId << 20},
            ""new_content"": {{ ""@type"": ""messageText"", ""text"": {{ ""text"": ""C2 New"" }} }}
        }}");

        Assert.Empty(cache.GetOutboxRows());

        // Edit date for chat 1 arrives
        handler.HandleUpdate($@"{{
            ""@type"": ""updateMessageEdited"",
            ""chat_id"": {chat1},
            ""message_id"": {srvMsgId << 20},
            ""edit_date"": 1787000010
        }}");

        var rows = cache.GetOutboxRows();
        Assert.Single(rows);
        Assert.Equal("1001", rows[0].PeerId);
        Assert.Equal(1787000010L, rows[0].OccurredAt);
        var p1 = JsonNode.Parse(rows[0].PayloadJson)!.AsObject();
        Assert.Equal("C1 New", p1["new_text"]!.GetValue<string>());

        // Edit date for chat 2 arrives
        handler.HandleUpdate($@"{{
            ""@type"": ""updateMessageEdited"",
            ""chat_id"": {chat2},
            ""message_id"": {srvMsgId << 20},
            ""edit_date"": 1787000020
        }}");

        rows = cache.GetOutboxRows();
        Assert.Equal(2, rows.Count);
        Assert.Equal("2002", rows[1].PeerId);
        Assert.Equal(1787000020L, rows[1].OccurredAt);
        var p2 = JsonNode.Parse(rows[1].PayloadJson)!.AsObject();
        Assert.Equal("C2 New", p2["new_text"]!.GetValue<string>());
    }

    [Fact]
    public void Test10_Second_content_change_before_first_is_paired_emits_both_rows()
    {
        var dbPath = CreateTempDbPath();
        var cache = new MessageCache(dbPath);
        cache.Initialize();

        var handler = new CaptureUpdateHandler(cache, new AllowAllCaptureScope(), accountId: "7053823996");

        long chatId = -1002827825432L;
        long srvMsgId = 100L;
        long sendDate = 1787000000L;

        cache.Put(new CachedMessage(chatId, srvMsgId, "V1", "7053823996", false, false, null, sendDate));

        // First content change V1 -> V2
        handler.HandleUpdate($@"{{
            ""@type"": ""updateMessageContent"",
            ""chat_id"": {chatId},
            ""message_id"": {srvMsgId << 20},
            ""new_content"": {{ ""@type"": ""messageText"", ""text"": {{ ""text"": ""V2"" }} }}
        }}");

        Assert.Empty(cache.GetOutboxRows());

        // Second content change V2 -> V3 before first is paired!
        handler.HandleUpdate($@"{{
            ""@type"": ""updateMessageContent"",
            ""chat_id"": {chatId},
            ""message_id"": {srvMsgId << 20},
            ""new_content"": {{ ""@type"": ""messageText"", ""text"": {{ ""text"": ""V3"" }} }}
        }}");

        // The first edit MUST have been emitted with fallback occurred_at!
        var rows = cache.GetOutboxRows();
        Assert.Single(rows);
        Assert.Equal(sendDate, rows[0].OccurredAt);
        var p1 = JsonNode.Parse(rows[0].PayloadJson)!.AsObject();
        Assert.Equal("V1", p1["old_text"]!.GetValue<string>());
        Assert.Equal("V2", p1["new_text"]!.GetValue<string>());

        // Now edit_date arrives for second edit
        handler.HandleUpdate($@"{{
            ""@type"": ""updateMessageEdited"",
            ""chat_id"": {chatId},
            ""message_id"": {srvMsgId << 20},
            ""edit_date"": 1787000020
        }}");

        rows = cache.GetOutboxRows();
        Assert.Equal(2, rows.Count);
        Assert.Equal(1787000020L, rows[1].OccurredAt);
        var p2 = JsonNode.Parse(rows[1].PayloadJson)!.AsObject();
        Assert.Equal("V2", p2["old_text"]!.GetValue<string>());
        Assert.Equal("V3", p2["new_text"]!.GetValue<string>());
    }

    [Fact]
    public void Test11_ShouldAntiEdit_false_updates_cache_no_pending_row_no_record()
    {
        var dbPath = CreateTempDbPath();
        var cache = new MessageCache(dbPath);
        cache.Initialize();

        // Scope allows caching and anti-delete, but anti-edit is FALSE
        var scope = new CustomAntiEditScope(shouldCache: true, shouldAntiEdit: false);
        var handler = new CaptureUpdateHandler(cache, scope, accountId: "7053823996");

        long chatId = -1002827825432L;
        long srvMsgId = 100L;

        cache.Put(new CachedMessage(chatId, srvMsgId, "Initial", "7053823996", false, false, null, 1787000000L));

        handler.HandleUpdate($@"{{
            ""@type"": ""updateMessageContent"",
            ""chat_id"": {chatId},
            ""message_id"": {srvMsgId << 20},
            ""new_content"": {{ ""@type"": ""messageText"", ""text"": {{ ""text"": ""Updated"" }} }}
        }}");

        handler.HandleUpdate($@"{{
            ""@type"": ""updateMessageEdited"",
            ""chat_id"": {chatId},
            ""message_id"": {srvMsgId << 20},
            ""edit_date"": 1787000010
        }}");

        // Cache updated to new text
        var updated = cache.Get(chatId, srvMsgId);
        Assert.NotNull(updated);
        Assert.Equal("Updated", updated.Text);

        // No outbox records emitted
        Assert.Empty(cache.GetOutboxRows());

        // No pending rows
        using var conn = new SqliteConnection($"Data Source={dbPath};");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM pending_edits;";
        long pendingCount = Convert.ToInt64(cmd.ExecuteScalar());
        Assert.Equal(0, pendingCount);
    }

    private class CustomAntiEditScope(bool shouldCache, bool shouldAntiEdit) : ICaptureScope
    {
        public bool ShouldCache(string peerId) => shouldCache;
        public bool ShouldAntiDelete(string peerId) => true;
        public bool ShouldAntiEdit(string peerId) => shouldAntiEdit;
    }

    [Fact]
    public async Task Test12_Cache_miss_fetches_getMessage_baseline_no_row_and_next_edit_pairs()
    {
        var dbPath = CreateTempDbPath();
        var cache = new MessageCache(dbPath);
        cache.Initialize();

        var handler = new CaptureUpdateHandler(cache, new AllowAllCaptureScope(), accountId: "7053823996");
        var transport = new FakeTdTransport();
        using var client = new TdClient(transport);
        handler.Attach(client);

        long chatId = -1002827825432L;
        long srvMsgId = 500L;

        RespondTo(transport, "getMessage", req => $@"{{
            ""@type"": ""message"",
            ""id"": {req["message_id"]!.GetValue<long>()},
            ""chat_id"": {req["chat_id"]!.GetValue<long>()},
            ""sender_id"": {{ ""@type"": ""messageSenderUser"", ""user_id"": 7053823996 }},
            ""is_outgoing"": true,
            ""date"": 1786000000,
            ""content"": {{ ""@type"": ""messageText"", ""text"": {{ ""text"": ""Baseline Text"" }} }}
        }}");

        // First edit arrives on uncached message
        transport.EnqueueIncoming($@"{{
            ""@type"": ""updateMessageContent"",
            ""chat_id"": {chatId},
            ""message_id"": {srvMsgId << 20},
            ""new_content"": {{ ""@type"": ""messageText"", ""text"": {{ ""text"": ""Baseline Text"" }} }}
        }}");

        // Wait for baseline to be fetched
        var cached = await WaitFor(() => cache.Get(chatId, srvMsgId), c => c is not null);
        Assert.NotNull(cached);
        Assert.Empty(cache.GetOutboxRows());
        Assert.Equal(1, handler.UncachedEditCount);

        // Next edit arrives: content then edited
        transport.EnqueueIncoming($@"{{
            ""@type"": ""updateMessageContent"",
            ""chat_id"": {chatId},
            ""message_id"": {srvMsgId << 20},
            ""new_content"": {{ ""@type"": ""messageText"", ""text"": {{ ""text"": ""Second Edit"" }} }}
        }}");
        transport.EnqueueIncoming($@"{{
            ""@type"": ""updateMessageEdited"",
            ""chat_id"": {chatId},
            ""message_id"": {srvMsgId << 20},
            ""edit_date"": 1787000010
        }}");

        var rows = await WaitFor(() => cache.GetOutboxRows(), r => r.Count > 0);
        Assert.Single(rows);
        Assert.Equal(1787000010L, rows[0].OccurredAt);
        var payload = JsonNode.Parse(rows[0].PayloadJson)!.AsObject();
        Assert.Equal("Baseline Text", payload["old_text"]!.GetValue<string>());
        Assert.Equal("Second Edit", payload["new_text"]!.GetValue<string>());
    }

    private static async Task<T> WaitFor<T>(Func<T> probe, Func<T, bool> done)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        var value = probe();
        while (!done(value) && DateTime.UtcNow < deadline)
        {
            await Task.Delay(20);
            value = probe();
        }
        return value;
    }

    [Fact]
    public void Test13_Same_second_collision_documented_keeps_latest_payload()
    {
        var dbPath = CreateTempDbPath();
        var cache = new MessageCache(dbPath);
        cache.Initialize();

        var handler = new CaptureUpdateHandler(cache, new AllowAllCaptureScope(), accountId: "7053823996");

        long chatId = -1002827825432L;
        long srvMsgId = 100L;
        long sameEditDate = 1787000010L;

        cache.Put(new CachedMessage(chatId, srvMsgId, "Version 0", "7053823996", false, false, null, 1787000000L));

        // First edit: Version 0 -> Version 1 at 1787000010
        handler.HandleUpdate($@"{{
            ""@type"": ""updateMessageContent"",
            ""chat_id"": {chatId},
            ""message_id"": {srvMsgId << 20},
            ""new_content"": {{ ""@type"": ""messageText"", ""text"": {{ ""text"": ""Version 1"" }} }}
        }}");
        handler.HandleUpdate($@"{{
            ""@type"": ""updateMessageEdited"",
            ""chat_id"": {chatId},
            ""message_id"": {srvMsgId << 20},
            ""edit_date"": {sameEditDate}
        }}");

        var rows = cache.GetOutboxRows();
        Assert.Single(rows);
        var p1 = JsonNode.Parse(rows[0].PayloadJson)!.AsObject();
        Assert.Equal("Version 1", p1["new_text"]!.GetValue<string>());

        // Second edit in the SAME second: Version 1 -> Version 2 with same edit_date 1787000010
        handler.HandleUpdate($@"{{
            ""@type"": ""updateMessageContent"",
            ""chat_id"": {chatId},
            ""message_id"": {srvMsgId << 20},
            ""new_content"": {{ ""@type"": ""messageText"", ""text"": {{ ""text"": ""Version 2"" }} }}
        }}");
        handler.HandleUpdate($@"{{
            ""@type"": ""updateMessageEdited"",
            ""chat_id"": {chatId},
            ""message_id"": {srvMsgId << 20},
            ""edit_date"": {sameEditDate}
        }}");

        // By protocol design, same occurred_at collides in capture_outbox UNIQUE constraint and keeps the latest
        rows = cache.GetOutboxRows();
        Assert.Single(rows);
        Assert.Equal(sameEditDate, rows[0].OccurredAt);
        var p2 = JsonNode.Parse(rows[0].PayloadJson)!.AsObject();
        Assert.Equal("Version 2", p2["new_text"]!.GetValue<string>());
    }

    [Fact]
    public async Task Test14_Production_wiring_real_registration_resolve_only_ITdClient_emits_row_with_edit_date()
    {
        var dbPath = CreateTempDbPath();
        var services = new ServiceCollection();
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Capture:CacheDatabasePath"] = dbPath,
                ["Capture:Scope:DefaultEnabled"] = "true",
                ["Capture:EditPairingTimeoutSeconds"] = "60"
            })
            .Build();

        services.AddSingleton<IConfiguration>(config);
        services.AddMessageCache(config);
        services.AddSingleton<ITdTransport, FakeTdTransport>();
        services.AddSingleton<ITdClient, TdClient>();
        services.AddCaptureHandlers();

        var sp = services.BuildServiceProvider();

        // Start cache
        using var cts = new CancellationTokenSource();
        _ = CaptureCacheStartup.Start(sp, null, cts.Token);

        // Resolve ONLY ITdClient
        var client = sp.GetRequiredService<ITdClient>();
        var transport = (FakeTdTransport)sp.GetRequiredService<ITdTransport>();

        // 1. my_id
        transport.EnqueueIncoming(@"{
            ""@type"": ""updateOption"",
            ""name"": ""my_id"",
            ""value"": { ""@type"": ""optionValueInteger"", ""value"": 7053823996 }
        }");

        long chatId = -1002827825432L;
        long srvMsgId = 100L;
        long editDate = 1787000010L;

        // 2. updateNewMessage to populate cache
        transport.EnqueueIncoming($@"{{
            ""@type"": ""updateNewMessage"",
            ""message"": {{
                ""id"": {srvMsgId << 20},
                ""chat_id"": {chatId},
                ""sender_id"": {{ ""@type"": ""messageSenderUser"", ""user_id"": 7053823996 }},
                ""is_outgoing"": false,
                ""date"": 1787000000,
                ""content"": {{ ""@type"": ""messageText"", ""text"": {{ ""text"": ""Initial"" }} }}
            }}
        }}");

        // 3. updateMessageContent
        transport.EnqueueIncoming($@"{{
            ""@type"": ""updateMessageContent"",
            ""chat_id"": {chatId},
            ""message_id"": {srvMsgId << 20},
            ""new_content"": {{ ""@type"": ""messageText"", ""text"": {{ ""text"": ""Edited Text"" }} }}
        }}");

        // 4. updateMessageEdited
        transport.EnqueueIncoming($@"{{
            ""@type"": ""updateMessageEdited"",
            ""chat_id"": {chatId},
            ""message_id"": {srvMsgId << 20},
            ""edit_date"": {editDate}
        }}");

        var cache = sp.GetRequiredService<MessageCache>();
        var rows = await WaitFor(() => cache.GetOutboxRows(), r => r.Count > 0);

        Assert.Single(rows);
        Assert.Equal("edited", rows[0].Kind);
        Assert.Equal(editDate, rows[0].OccurredAt);
        var payload = JsonNode.Parse(rows[0].PayloadJson)!.AsObject();
        Assert.Equal("Initial", payload["old_text"]!.GetValue<string>());
        Assert.Equal("Edited Text", payload["new_text"]!.GetValue<string>());

        cts.Cancel();
    }

    [Fact]
    public void Test15_Atomicity_outbox_insert_forced_to_fail_leaves_pending_row_in_place()
    {
        var dbPath = CreateTempDbPath();
        var cache = new MessageCache(dbPath);
        cache.Initialize();

        var handler = new CaptureUpdateHandler(cache, new AllowAllCaptureScope(), accountId: "7053823996");

        long chatId = -1002827825432L;
        long srvMsgId = 100L;

        cache.Put(new CachedMessage(chatId, srvMsgId, "Old Text", "7053823996", false, false, null, 1787000000L));

        // Atomicity test 1 (break c): write pending row inside cache-update transaction
        // If pending insert fails, message_cache must NOT retain the new text.
        using (var conn = new SqliteConnection($"Data Source={dbPath};"))
        {
            conn.Open();
            using var trigCmd = conn.CreateCommand();
            trigCmd.CommandText = @"
                CREATE TRIGGER fail_pending_insert BEFORE INSERT ON pending_edits
                BEGIN
                    SELECT RAISE(ABORT, 'forced test pending error');
                END;";
            trigCmd.ExecuteNonQuery();
        }

        long errBefore = handler.ErrorCount;
        handler.HandleUpdate($@"{{
            ""@type"": ""updateMessageContent"",
            ""chat_id"": {chatId},
            ""message_id"": {srvMsgId << 20},
            ""new_content"": {{ ""@type"": ""messageText"", ""text"": {{ ""text"": ""New Text"" }} }}
        }}");
        Assert.Equal(errBefore + 1, handler.ErrorCount);

        // Cache must still have old text
        Assert.Equal("Old Text", cache.Get(chatId, srvMsgId)!.Text);

        // Remove trigger to proceed with normal flow
        using (var conn = new SqliteConnection($"Data Source={dbPath};"))
        {
            conn.Open();
            using var dropCmd = conn.CreateCommand();
            dropCmd.CommandText = "DROP TRIGGER fail_pending_insert;";
            dropCmd.ExecuteNonQuery();
        }

        // Now do normal content stage
        handler.HandleUpdate($@"{{
            ""@type"": ""updateMessageContent"",
            ""chat_id"": {chatId},
            ""message_id"": {srvMsgId << 20},
            ""new_content"": {{ ""@type"": ""messageText"", ""text"": {{ ""text"": ""New Text"" }} }}
        }}");

        // Verify pending row exists in SQLite
        using (var conn = new SqliteConnection($"Data Source={dbPath};"))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM pending_edits WHERE chat_id = @c AND message_id = @m;";
            cmd.Parameters.AddWithValue("@c", chatId);
            cmd.Parameters.AddWithValue("@m", srvMsgId);
            Assert.Equal(1, Convert.ToInt64(cmd.ExecuteScalar()));

            // Add abort trigger on capture_outbox
            using var trigCmd = conn.CreateCommand();
            trigCmd.CommandText = @"
                CREATE TRIGGER fail_outbox_insert BEFORE INSERT ON capture_outbox
                BEGIN
                    SELECT RAISE(ABORT, 'forced test outbox error');
                END;";
            trigCmd.ExecuteNonQuery();
        }

        // updateMessageEdited arrives and pairing transaction fails
        handler.HandleUpdate($@"{{
            ""@type"": ""updateMessageEdited"",
            ""chat_id"": {chatId},
            ""message_id"": {srvMsgId << 20},
            ""edit_date"": 1787000010
        }}");

        // Atomicity requirement: the pending row MUST STILL EXIST!
        using (var conn = new SqliteConnection($"Data Source={dbPath};"))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM pending_edits WHERE chat_id = @c AND message_id = @m;";
            cmd.Parameters.AddWithValue("@c", chatId);
            cmd.Parameters.AddWithValue("@m", srvMsgId);
            Assert.Equal(1, Convert.ToInt64(cmd.ExecuteScalar()));
        }

        // Outbox is empty
        Assert.Empty(cache.GetOutboxRows());
    }

    [Fact]
    public void Test16_Logs_contain_no_message_text()
    {
        var dbPath = CreateTempDbPath();
        var cache = new MessageCache(dbPath);
        cache.Initialize();

        var logger = new TestLogger<CaptureUpdateHandler>();
        var handler = new CaptureUpdateHandler(cache, new AllowAllCaptureScope(), timeProvider: null, logger, accountId: "7053823996");

        long chatId = -1002827825432L;
        long srvMsgId = 100L;
        string secretText = "super_secret_payload_987654";

        cache.Put(new CachedMessage(chatId, srvMsgId, "Previous Text", "7053823996", false, false, null, 1787000000L));

        handler.HandleUpdate($@"{{
            ""@type"": ""updateMessageContent"",
            ""chat_id"": {chatId},
            ""message_id"": {srvMsgId << 20},
            ""new_content"": {{ ""@type"": ""messageText"", ""text"": {{ ""text"": ""{secretText}"" }} }}
        }}");

        handler.HandleUpdate($@"{{
            ""@type"": ""updateMessageEdited"",
            ""chat_id"": {chatId},
            ""message_id"": {srvMsgId << 20},
            ""edit_date"": 1787000010
        }}");

        foreach (var entry in logger.Entries)
        {
            Assert.DoesNotContain(secretText, entry.Message);
        }
    }
}
