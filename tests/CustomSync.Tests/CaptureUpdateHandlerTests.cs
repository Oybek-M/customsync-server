using System.Collections.Concurrent;
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

public class AllowAllCaptureScope : ICaptureScope
{
    public bool ShouldCache(string peerId) => true;
    public bool ShouldAntiDelete(string peerId) => true;
    public bool ShouldAntiEdit(string peerId) => true;
}

public class DenyAllCaptureScope : ICaptureScope
{
    public bool ShouldCache(string peerId) => false;
    public bool ShouldAntiDelete(string peerId) => false;
    public bool ShouldAntiEdit(string peerId) => false;
}

public class FilterCaptureScope(Func<string, bool> predicate) : ICaptureScope
{
    public bool ShouldCache(string peerId) => predicate(peerId);
    public bool ShouldAntiDelete(string peerId) => predicate(peerId);
    public bool ShouldAntiEdit(string peerId) => predicate(peerId);
}

public class TestLogger<T> : ILogger<T>
{
    public record LogEntry(LogLevel Level, EventId EventId, string Message, Exception? Exception);

    public readonly ConcurrentBag<LogEntry> Entries = new();

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        var message = formatter(state, exception);
        Entries.Add(new LogEntry(logLevel, eventId, message, exception));
    }
}

public class CaptureUpdateHandlerTests : IDisposable
{
    private readonly List<string> _tempFiles = new();

    private string CreateTempDbPath()
    {
        var path = Path.Combine(Path.GetTempPath(), $"capture_outbox_test_{Guid.NewGuid():N}.db");
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
    public void Test01_TdIdMapper_maps_peers_and_messages_correctly()
    {
        // User 7053823996 -> "7053823996"
        Assert.Equal("7053823996", TdIdMapper.ToPeerId(7053823996L));

        // Basic group: chat_id < 0 && chat_id >= -999999999999
        // -123456789 -> 123456789 | (1UL << 48)
        ulong expectedBasicGroup = 123456789UL | (1UL << 48);
        Assert.Equal(expectedBasicGroup.ToString(), TdIdMapper.ToPeerId(-123456789L));

        // Supergroup -1002827825432 -> "562952781246744" (= 2827825432 + 2^49)
        Assert.Equal("562952781246744", TdIdMapper.ToPeerId(-1002827825432L));

        // Secret chat -> none (null)
        Assert.Null(TdIdMapper.ToPeerId(-2000000000001L));
        Assert.Null(TdIdMapper.ToPeerId(0L));

        // Message: 395278 << 20 -> 395278
        long serverShifted = 395278L << 20;
        Assert.Equal(395278L, TdIdMapper.ToServerMessageId(serverShifted));

        // Non-server message: (5 << 20) + 1 -> none (null)
        long nonServerId = (5L << 20) + 1L;
        Assert.Null(TdIdMapper.ToServerMessageId(nonServerId));

        // Sender mapper
        using var userDoc = JsonDocument.Parse(@"{""@type"":""messageSenderUser"",""user_id"":7053823996}");
        Assert.Equal("7053823996", TdIdMapper.ToSenderId(userDoc.RootElement));

        using var chatDoc = JsonDocument.Parse(@"{""@type"":""messageSenderChat"",""chat_id"":-1002827825432}");
        Assert.Equal("562952781246744", TdIdMapper.ToSenderId(chatDoc.RootElement));
    }

    [Fact]
    public void Test02_New_in_scope_message_cached_with_fake_clock_and_out_of_scope_ignored()
    {
        var dbPath = CreateTempDbPath();
        var timeProvider = new TestTimeProvider(DateTimeOffset.FromUnixTimeSeconds(1787111222));
        var cache = new MessageCache(dbPath, timeProvider);
        cache.Initialize();

        // Scope allows only peer "562952781246744"
        var scope = new FilterCaptureScope(peer => peer == "562952781246744");
        var handler = new CaptureUpdateHandler(cache, scope, timeProvider, accountId: "7053823996");

        // 1. In-scope message (-1002827825432 -> 562952781246744)
        long inScopeChatId = -1002827825432L;
        long inScopeMsgId = 100L << 20;
        long messageDate = 1787000000L;

        var inScopeUpdate = $@"{{
            ""@type"": ""updateNewMessage"",
            ""message"": {{
                ""id"": {inScopeMsgId},
                ""chat_id"": {inScopeChatId},
                ""sender_id"": {{ ""@type"": ""messageSenderUser"", ""user_id"": 7053823996 }},
                ""is_outgoing"": false,
                ""date"": {messageDate},
                ""content"": {{
                    ""@type"": ""messageText"",
                    ""text"": {{ ""text"": ""Salom dunyo"" }}
                }}
            }}
        }}";

        handler.HandleUpdate(inScopeUpdate);

        var cachedInScope = cache.Get(inScopeChatId, 100L);
        Assert.NotNull(cachedInScope);
        Assert.Equal("Salom dunyo", cachedInScope.Text);
        Assert.Equal(messageDate, cachedInScope.Date);
        // CachedAt MUST come from timeProvider fake clock (1787111222), NOT message date!
        Assert.Equal(1787111222L, cachedInScope.CachedAt);

        // 2. Out-of-scope message (chatId = 123456 -> user 123456 not in scope)
        long outOfScopeChatId = 123456L;
        long outOfScopeMsgId = 200L << 20;

        var outOfScopeUpdate = $@"{{
            ""@type"": ""updateNewMessage"",
            ""message"": {{
                ""id"": {outOfScopeMsgId},
                ""chat_id"": {outOfScopeChatId},
                ""sender_id"": {{ ""@type"": ""messageSenderUser"", ""user_id"": 123456 }},
                ""is_outgoing"": false,
                ""date"": {messageDate},
                ""content"": {{
                    ""@type"": ""messageText"",
                    ""text"": {{ ""text"": ""Tashqi xabar"" }}
                }}
            }}
        }}";

        handler.HandleUpdate(outOfScopeUpdate);

        var cachedOutOfScope = cache.Get(outOfScopeChatId, 200L);
        Assert.Null(cachedOutOfScope);
    }

    [Fact]
    public void Test03_Permanent_delete_of_cached_message_emits_one_outbox_row_with_original_date()
    {
        var dbPath = CreateTempDbPath();
        var timeProvider = new TestTimeProvider(DateTimeOffset.FromUnixTimeSeconds(1787999999));
        var cache = new MessageCache(dbPath, timeProvider);
        cache.Initialize();

        var scope = new AllowAllCaptureScope();
        var handler = new CaptureUpdateHandler(cache, scope, timeProvider, accountId: "7053823996");

        long chatId = -1002827825432L;
        long srvMsgId = 395278L;
        long tdlibMsgId = srvMsgId << 20;
        long sendDate = 1787000000L;

        // Pre-cache message
        cache.Put(new CachedMessage(
            ChatId: chatId,
            MessageId: srvMsgId,
            Text: "O'chiriladigan xabar matni",
            SenderId: "7053823996",
            IsOut: false,
            IsMedia: false,
            MediaId: null,
            Date: sendDate,
            CachedAt: 1787000010L));

        // Delete update
        var deleteUpdate = $@"{{
            ""@type"": ""updateDeleteMessages"",
            ""chat_id"": {chatId},
            ""message_ids"": [{tdlibMsgId}],
            ""is_permanent"": true,
            ""from_cache"": false
        }}";

        handler.HandleUpdate(deleteUpdate);

        // Outbox assertions
        var rows = cache.GetOutboxRows();
        Assert.Single(rows);
        var row = rows[0];

        Assert.Equal("deleted", row.Kind);
        Assert.Equal("7053823996", row.AccountId);
        Assert.Equal("562952781246744", row.PeerId);
        Assert.Equal(srvMsgId, row.MsgId);
        // occurred_at must equal original send date, not deletion time!
        Assert.Equal(sendDate, row.OccurredAt);
        Assert.Equal(1787999999L, row.ObservedAt);

        // Payload assertions matching custom_sync_payload.cpp BuildDeleted exactly
        var payload = JsonNode.Parse(row.PayloadJson)!.AsObject();
        Assert.Equal("7053823996", payload["account_id"]!.GetValue<string>());
        Assert.Equal("562952781246744", payload["peer_id"]!.GetValue<string>());
        Assert.Equal("O'chiriladigan xabar matni", payload["text"]!.GetValue<string>());
        Assert.Equal("7053823996", payload["sender_id"]!.GetValue<string>());
        Assert.False(payload["is_out"]!.GetValue<bool>());
        Assert.False(payload["is_media"]!.GetValue<bool>());

        // Cache row must be gone
        Assert.Null(cache.Get(chatId, srvMsgId));
    }

    [Fact]
    public void Test04_Non_permanent_or_from_cache_delete_is_ignored()
    {
        var dbPath = CreateTempDbPath();
        var cache = new MessageCache(dbPath);
        cache.Initialize();

        var scope = new AllowAllCaptureScope();
        var handler = new CaptureUpdateHandler(cache, scope, accountId: "7053823996");

        long chatId = -1002827825432L;
        long srvMsgId = 123L;
        long tdlibMsgId = srvMsgId << 20;

        cache.Put(new CachedMessage(chatId, srvMsgId, "Matn", "7053823996", false, false, null, 1787000000, 1787000000));

        // 1. is_permanent: false
        handler.HandleUpdate($@"{{
            ""@type"": ""updateDeleteMessages"",
            ""chat_id"": {chatId},
            ""message_ids"": [{tdlibMsgId}],
            ""is_permanent"": false,
            ""from_cache"": false
        }}");

        Assert.Empty(cache.GetOutboxRows());
        Assert.NotNull(cache.Get(chatId, srvMsgId));

        // 2. from_cache: true
        handler.HandleUpdate($@"{{
            ""@type"": ""updateDeleteMessages"",
            ""chat_id"": {chatId},
            ""message_ids"": [{tdlibMsgId}],
            ""is_permanent"": true,
            ""from_cache"": true
        }}");

        Assert.Empty(cache.GetOutboxRows());
        Assert.NotNull(cache.Get(chatId, srvMsgId));
    }

    [Fact]
    public void Test05_Delete_of_uncached_id_writes_nothing_and_increments_counter()
    {
        var dbPath = CreateTempDbPath();
        var cache = new MessageCache(dbPath);
        cache.Initialize();

        var scope = new AllowAllCaptureScope();
        var handler = new CaptureUpdateHandler(cache, scope, accountId: "7053823996");

        long chatId = -1002827825432L;
        long uncachedTdlibId = 9999L << 20;

        handler.HandleUpdate($@"{{
            ""@type"": ""updateDeleteMessages"",
            ""chat_id"": {chatId},
            ""message_ids"": [{uncachedTdlibId}],
            ""is_permanent"": true,
            ""from_cache"": false
        }}");

        Assert.Empty(cache.GetOutboxRows());
        Assert.Equal(1, handler.UncachedDeleteCount);
    }

    private class FailingMessageCache : MessageCache
    {
        public FailingMessageCache(string databasePath) : base(databasePath) { }

        public override DeleteResult DeleteMessagesAndRecordOutbox(
            long chatId,
            string peerId,
            string accountId,
            IReadOnlyList<long> serverMessageIds,
            long? observedAt = null,
            Action<int>? onInsertAction = null)
        {
            return base.DeleteMessagesAndRecordOutbox(
                chatId,
                peerId,
                accountId,
                serverMessageIds,
                observedAt,
                idx =>
                {
                    if (idx == 1)
                    {
                        throw new InvalidOperationException("Simulated second insert failure for atomicity test");
                    }
                });
        }
    }

    [Fact]
    public void Test06_Multi_id_delete_is_atomic_on_failure()
    {
        var dbPath = CreateTempDbPath();
        var cache = new FailingMessageCache(dbPath);
        cache.Initialize();

        var scope = new AllowAllCaptureScope();
        var handler = new CaptureUpdateHandler(cache, scope, accountId: "7053823996");

        long chatId = -1002827825432L;
        long msg1 = 101L;
        long msg2 = 102L;

        cache.Put(new CachedMessage(chatId, msg1, "Message 1", "7053823996", false, false, null, 1787000000, 1787000000));
        cache.Put(new CachedMessage(chatId, msg2, "Message 2", "7053823996", false, false, null, 1787000000, 1787000000));

        var deleteUpdate = $@"{{
            ""@type"": ""updateDeleteMessages"",
            ""chat_id"": {chatId},
            ""message_ids"": [{msg1 << 20}, {msg2 << 20}],
            ""is_permanent"": true,
            ""from_cache"": false
        }}";

        handler.HandleUpdate(deleteUpdate);

        // Due to rollback: 0 outbox rows and BOTH messages must still exist in cache!
        Assert.Empty(cache.GetOutboxRows());
        Assert.NotNull(cache.Get(chatId, msg1));
        Assert.NotNull(cache.Get(chatId, msg2));
        Assert.True(handler.ErrorCount > 0);
    }

    [Fact]
    public void Test07_Same_delete_update_delivered_twice_results_in_one_outbox_row()
    {
        var dbPath = CreateTempDbPath();
        var cache = new MessageCache(dbPath);
        cache.Initialize();

        var scope = new AllowAllCaptureScope();
        var handler = new CaptureUpdateHandler(cache, scope, accountId: "7053823996");

        long chatId = -1002827825432L;
        long msgId = 555L;

        cache.Put(new CachedMessage(chatId, msgId, "Test message", "7053823996", false, false, null, 1787000000, 1787000000));

        var deleteUpdate = $@"{{
            ""@type"": ""updateDeleteMessages"",
            ""chat_id"": {chatId},
            ""message_ids"": [{msgId << 20}],
            ""is_permanent"": true,
            ""from_cache"": false
        }}";

        // Deliver first time
        handler.HandleUpdate(deleteUpdate);
        Assert.Single(cache.GetOutboxRows());

        // Deliver second time (replay)
        handler.HandleUpdate(deleteUpdate);
        Assert.Single(cache.GetOutboxRows());
    }

    [Fact]
    public void Test08_Edit_with_cached_text_creates_edited_row_and_updates_cache()
    {
        var dbPath = CreateTempDbPath();
        var timeProvider = new TestTimeProvider(DateTimeOffset.FromUnixTimeSeconds(1787555555));
        var cache = new MessageCache(dbPath, timeProvider);
        cache.Initialize();

        var scope = new AllowAllCaptureScope();
        var handler = new CaptureUpdateHandler(cache, scope, timeProvider, accountId: "7053823996");

        long chatId = -1002827825432L;
        long srvMsgId = 390234L;
        long sendDate = 1787000001L;

        cache.Put(new CachedMessage(chatId, srvMsgId, "Eski matn", "7053823996", false, false, null, sendDate, 1787000000));

        var editUpdate = $@"{{
            ""@type"": ""updateMessageContent"",
            ""chat_id"": {chatId},
            ""message_id"": {srvMsgId << 20},
            ""new_content"": {{
                ""@type"": ""messageText"",
                ""text"": {{ ""text"": ""Yangi tahrirlangan matn"" }}
            }}
        }}";

        handler.HandleUpdate(editUpdate);

        var rows = cache.GetOutboxRows();
        Assert.Single(rows);
        var row = rows[0];

        Assert.Equal("edited", row.Kind);
        Assert.Equal("7053823996", row.AccountId);
        Assert.Equal("562952781246744", row.PeerId);
        Assert.Equal(srvMsgId, row.MsgId);
        Assert.Equal(sendDate, row.OccurredAt); // original send date!

        var payload = JsonNode.Parse(row.PayloadJson)!.AsObject();
        Assert.Equal("7053823996", payload["account_id"]!.GetValue<string>());
        Assert.Equal("562952781246744", payload["peer_id"]!.GetValue<string>());
        Assert.Equal("Eski matn", payload["old_text"]!.GetValue<string>());
        Assert.Equal("Yangi tahrirlangan matn", payload["new_text"]!.GetValue<string>());
        Assert.False(payload["is_out"]!.GetValue<bool>());

        // Cache now holds new text
        var updatedCached = cache.Get(chatId, srvMsgId);
        Assert.NotNull(updatedCached);
        Assert.Equal("Yangi tahrirlangan matn", updatedCached.Text);
    }

    [Fact]
    public void Test09_Edit_with_unchanged_text_emits_nothing()
    {
        var dbPath = CreateTempDbPath();
        var cache = new MessageCache(dbPath);
        cache.Initialize();

        var scope = new AllowAllCaptureScope();
        var handler = new CaptureUpdateHandler(cache, scope, accountId: "7053823996");

        long chatId = -1002827825432L;
        long srvMsgId = 400L;

        cache.Put(new CachedMessage(chatId, srvMsgId, "Bir xil matn", "7053823996", false, false, null, 1787000000, 1787000000));

        var editUpdate = $@"{{
            ""@type"": ""updateMessageContent"",
            ""chat_id"": {chatId},
            ""message_id"": {srvMsgId << 20},
            ""new_content"": {{
                ""@type"": ""messageText"",
                ""text"": {{ ""text"": ""Bir xil matn"" }}
            }}
        }}";

        handler.HandleUpdate(editUpdate);

        Assert.Empty(cache.GetOutboxRows());
    }

    // FakeTdTransport'ga TDLib javobini beradi: so'rovning @extra'si
    // javobga ko'chiriladi, aks holda TdClient uni kutgan so'rovga bog'lamaydi.
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
    public async Task Test10_Edit_with_cache_miss_fetches_full_message_and_following_edit_emits_row()
    {
        var dbPath = CreateTempDbPath();
        var timeProvider = new TestTimeProvider(DateTimeOffset.FromUnixTimeSeconds(1787000100));
        var cache = new MessageCache(dbPath, timeProvider);
        cache.Initialize();

        var handler = new CaptureUpdateHandler(cache, new AllowAllCaptureScope(), timeProvider, accountId: "7053823996");
        var transport = new FakeTdTransport();
        using var client = new TdClient(transport);
        handler.Attach(client);

        long chatId = -1002827825432L;
        long srvMsgId = 500L;

        // getMessage javobi: asl sana, chiquvchi, yuboruvchi — updateMessageContent'da yo'q maydonlar
        RespondTo(transport, "getMessage", req => $@"{{
            ""@type"": ""message"",
            ""id"": {req["message_id"]!.GetValue<long>()},
            ""chat_id"": {req["chat_id"]!.GetValue<long>()},
            ""sender_id"": {{ ""@type"": ""messageSenderUser"", ""user_id"": 7053823996 }},
            ""is_outgoing"": true,
            ""date"": 1786000000,
            ""content"": {{ ""@type"": ""messageText"", ""text"": {{ ""text"": ""Boshlang'ich noma'lum matn"" }} }}
        }}");

        transport.EnqueueIncoming($@"{{
            ""@type"": ""updateMessageContent"",
            ""chat_id"": {chatId},
            ""message_id"": {srvMsgId << 20},
            ""new_content"": {{ ""@type"": ""messageText"", ""text"": {{ ""text"": ""Boshlang'ich noma'lum matn"" }} }}
        }}");

        var cached = await WaitFor(() => cache.Get(chatId, srvMsgId), c => c is not null);
        Assert.NotNull(cached);
        Assert.Empty(cache.GetOutboxRows());
        Assert.Equal(1, handler.UncachedEditCount);
        Assert.Equal(1786000000, cached!.Date);
        Assert.True(cached.IsOut);
        Assert.Equal("7053823996", cached.SenderId);
        Assert.Equal(1787000100, cached.CachedAt);

        transport.EnqueueIncoming($@"{{
            ""@type"": ""updateMessageContent"",
            ""chat_id"": {chatId},
            ""message_id"": {srvMsgId << 20},
            ""new_content"": {{ ""@type"": ""messageText"", ""text"": {{ ""text"": ""Ikkinchi tahrir matni"" }} }}
        }}");

        var rows = await WaitFor(() => cache.GetOutboxRows(), r => r.Count > 0);
        Assert.Single(rows);
        Assert.Equal(1786000000, rows[0].OccurredAt);
        var payload = JsonNode.Parse(rows[0].PayloadJson)!.AsObject();
        Assert.Equal("Boshlang'ich noma'lum matn", payload["old_text"]!.GetValue<string>());
        Assert.Equal("Ikkinchi tahrir matni", payload["new_text"]!.GetValue<string>());
        Assert.True(payload["is_out"]!.GetValue<bool>());
    }

    [Fact]
    public void Test10b_Edit_with_cache_miss_never_writes_a_fabricated_row()
    {
        var dbPath = CreateTempDbPath();
        var cache = new MessageCache(dbPath);
        cache.Initialize();
        var handler = new CaptureUpdateHandler(cache, new AllowAllCaptureScope(), accountId: "7053823996");

        long chatId = -1002827825432L;
        long srvMsgId = 501L;
        handler.HandleUpdate($@"{{
            ""@type"": ""updateMessageContent"",
            ""chat_id"": {chatId},
            ""message_id"": {srvMsgId << 20},
            ""new_content"": {{ ""@type"": ""messageText"", ""text"": {{ ""text"": ""x"" }} }}
        }}");

        // Sana/yuboruvchi/is_out noma'lum qator keyingi `deleted` ga yolg'on maydon beradi
        Assert.Null(cache.Get(chatId, srvMsgId));
        Assert.Empty(cache.GetOutboxRows());
        Assert.Equal(1, handler.UncachedEditCount);
    }

    [Fact]
    public async Task Test10c_Late_getMessage_reply_does_not_overwrite_a_newer_row()
    {
        var dbPath = CreateTempDbPath();
        var cache = new MessageCache(dbPath);
        cache.Initialize();
        var handler = new CaptureUpdateHandler(cache, new AllowAllCaptureScope(), accountId: "7053823996");
        var transport = new FakeTdTransport();
        using var client = new TdClient(transport);
        handler.Attach(client);

        long chatId = 7053823996L;
        long srvMsgId = 502L;
        var reply = new TaskCompletionSource<JsonObject>(TaskCreationOptions.RunContinuationsAsynchronously);
        transport.OnSend = (_, json) =>
        {
            var r = JsonNode.Parse(json)!.AsObject();
            if (r["@type"]!.GetValue<string>() == "getMessage") reply.TrySetResult(r);
        };

        transport.EnqueueIncoming($@"{{
            ""@type"": ""updateMessageContent"", ""chat_id"": {chatId}, ""message_id"": {srvMsgId << 20},
            ""new_content"": {{ ""@type"": ""messageText"", ""text"": {{ ""text"": ""eski"" }} }}
        }}");
        var req = await reply.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // Javob kelguncha xabar boshqa yo'l bilan keshga tushdi
        cache.Put(new CachedMessage(chatId, srvMsgId, "yangi", "7053823996", false, false, null, 1786000000));

        transport.EnqueueIncoming($@"{{
            ""@type"": ""message"", ""@extra"": ""{req["@extra"]!.GetValue<string>()}"",
            ""id"": {srvMsgId << 20}, ""chat_id"": {chatId},
            ""sender_id"": {{ ""@type"": ""messageSenderUser"", ""user_id"": 1 }},
            ""is_outgoing"": true, ""date"": 1,
            ""content"": {{ ""@type"": ""messageText"", ""text"": {{ ""text"": ""eski"" }} }}
        }}");
        await WaitFor(() => client.PendingRequestCount, n => n == 0);
        await Task.Delay(200);

        var cached = cache.Get(chatId, srvMsgId)!;
        Assert.Equal("yangi", cached.Text);
        Assert.Equal(1786000000, cached.Date);
    }

    [Fact]
    public void Test11_Two_edits_before_sending_results_in_one_pending_row_with_latest_payload()
    {
        var dbPath = CreateTempDbPath();
        var cache = new MessageCache(dbPath);
        cache.Initialize();

        var scope = new AllowAllCaptureScope();
        var handler = new CaptureUpdateHandler(cache, scope, accountId: "7053823996");

        long chatId = -1002827825432L;
        long srvMsgId = 600L;
        long sendDate = 1787000000L;

        cache.Put(new CachedMessage(chatId, srvMsgId, "Asl matn", "7053823996", false, false, null, sendDate, sendDate));

        // First edit
        handler.HandleUpdate($@"{{
            ""@type"": ""updateMessageContent"",
            ""chat_id"": {chatId},
            ""message_id"": {srvMsgId << 20},
            ""new_content"": {{
                ""@type"": ""messageText"",
                ""text"": {{ ""text"": ""Tahrir 1"" }}
            }}
        }}");

        Assert.Single(cache.GetOutboxRows());

        // Second edit before Task 6 has sent the first row
        handler.HandleUpdate($@"{{
            ""@type"": ""updateMessageContent"",
            ""chat_id"": {chatId},
            ""message_id"": {srvMsgId << 20},
            ""new_content"": {{
                ""@type"": ""messageText"",
                ""text"": {{ ""text"": ""Tahrir 2"" }}
            }}
        }}");

        // Still exactly one pending row in capture_outbox!
        var rows = cache.GetOutboxRows();
        Assert.Single(rows);
        var payload = JsonNode.Parse(rows[0].PayloadJson)!.AsObject();
        Assert.Equal("Tahrir 2", payload["new_text"]!.GetValue<string>());
    }

    [Fact]
    public async Task Test12_Ordering_new_message_and_deletion_back_to_back_through_real_TdClient()
    {
        var dbPath = CreateTempDbPath();
        var cache = new MessageCache(dbPath);
        cache.Initialize();

        var scope = new AllowAllCaptureScope();
        var handler = new CaptureUpdateHandler(cache, scope, accountId: "7053823996");

        var transport = new FakeTdTransport();
        using var client = new TdClient(transport);

        client.UpdateReceived += handler.HandleUpdate;

        long chatId = -1002827825432L;
        long srvMsgId = 777L;
        long tdlibMsgId = srvMsgId << 20;

        var newMsgJson = $@"{{
            ""@type"": ""updateNewMessage"",
            ""message"": {{
                ""id"": {tdlibMsgId},
                ""chat_id"": {chatId},
                ""sender_id"": {{ ""@type"": ""messageSenderUser"", ""user_id"": 7053823996 }},
                ""is_outgoing"": false,
                ""date"": 1787000000,
                ""content"": {{
                    ""@type"": ""messageText"",
                    ""text"": {{ ""text"": ""Tartib testi xabari"" }}
                }}
            }}
        }}";

        var deleteMsgJson = $@"{{
            ""@type"": ""updateDeleteMessages"",
            ""chat_id"": {chatId},
            ""message_ids"": [{tdlibMsgId}],
            ""is_permanent"": true,
            ""from_cache"": false
        }}";

        // Enqueue back-to-back
        transport.EnqueueIncoming(newMsgJson);
        transport.EnqueueIncoming(deleteMsgJson);

        // Wait up to 10 seconds for processing
        var deadline = DateTime.UtcNow.AddSeconds(10);
        IReadOnlyList<OutboxRow> rows = Array.Empty<OutboxRow>();
        while (DateTime.UtcNow < deadline)
        {
            rows = cache.GetOutboxRows();
            if (rows.Count > 0) break;
            await Task.Delay(20);
        }

        Assert.Single(rows);
        var payload = JsonNode.Parse(rows[0].PayloadJson)!.AsObject();
        Assert.Equal("Tartib testi xabari", payload["text"]!.GetValue<string>());
    }

    [Fact]
    public async Task Test13_Production_wiring_asserts_outbox_rows_through_registration()
    {
        var dbPath = CreateTempDbPath();
        var transport = new FakeTdTransport();

        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Capture:CacheDatabasePath"] = dbPath
        }).Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<ITdTransport>(transport);
        services.AddSingleton<ITdClient, TdClient>();
        services.AddMessageCache(config);
        services.AddCaptureHandlers();
        // Override fail-closed scope for this test with allow-all
        services.AddSingleton<ICaptureScope>(new AllowAllCaptureScope());

        var sp = services.BuildServiceProvider();

        var cache = sp.GetRequiredService<MessageCache>();
        cache.Initialize();

        // Resolving CaptureUpdateHandler subscribes to ITdClient.UpdateReceived
        var handler = sp.GetRequiredService<CaptureUpdateHandler>();
        handler.SetAccountId("7053823996");

        var client = sp.GetRequiredService<ITdClient>();

        long chatId = -1002827825432L;
        long srvMsgId = 888L;
        long tdlibMsgId = srvMsgId << 20;

        transport.EnqueueIncoming($@"{{
            ""@type"": ""updateNewMessage"",
            ""message"": {{
                ""id"": {tdlibMsgId},
                ""chat_id"": {chatId},
                ""sender_id"": {{ ""@type"": ""messageSenderUser"", ""user_id"": 7053823996 }},
                ""is_outgoing"": false,
                ""date"": 1787000000,
                ""content"": {{
                    ""@type"": ""messageText"",
                    ""text"": {{ ""text"": ""Production wiring message"" }}
                }}
            }}
        }}");

        transport.EnqueueIncoming($@"{{
            ""@type"": ""updateDeleteMessages"",
            ""chat_id"": {chatId},
            ""message_ids"": [{tdlibMsgId}],
            ""is_permanent"": true,
            ""from_cache"": false
        }}");

        var deadline = DateTime.UtcNow.AddSeconds(5);
        IReadOnlyList<OutboxRow> rows = Array.Empty<OutboxRow>();
        while (DateTime.UtcNow < deadline)
        {
            rows = cache.GetOutboxRows();
            if (rows.Count > 0) break;
            await Task.Delay(20);
        }

        Assert.Single(rows);
        Assert.Equal("deleted", rows[0].Kind);
    }

    [Fact]
    public void Test14_Logs_contain_no_message_text_or_captions()
    {
        var dbPath = CreateTempDbPath();
        var cache = new MessageCache(dbPath);
        cache.Initialize();

        var scope = new AllowAllCaptureScope();
        var logger = new TestLogger<CaptureUpdateHandler>();
        var handler = new CaptureUpdateHandler(cache, scope, logger: logger, accountId: "7053823996");

        const string secretText = "TOP_SECRET_TEXT_12345";
        const string secretCaption = "CONFIDENTIAL_CAPTION_67890";

        long chatId = -1002827825432L;
        long srvMsgId = 999L;
        long tdlibMsgId = srvMsgId << 20;

        // 1. New message
        handler.HandleUpdate($@"{{
            ""@type"": ""updateNewMessage"",
            ""message"": {{
                ""id"": {tdlibMsgId},
                ""chat_id"": {chatId},
                ""sender_id"": {{ ""@type"": ""messageSenderUser"", ""user_id"": 7053823996 }},
                ""is_outgoing"": false,
                ""date"": 1787000000,
                ""content"": {{
                    ""@type"": ""messagePhoto"",
                    ""caption"": {{ ""text"": ""{secretCaption}"" }}
                }}
            }}
        }}");

        // 2. Edit
        handler.HandleUpdate($@"{{
            ""@type"": ""updateMessageContent"",
            ""chat_id"": {chatId},
            ""message_id"": {tdlibMsgId},
            ""new_content"": {{
                ""@type"": ""messageText"",
                ""text"": {{ ""text"": ""{secretText}"" }}
            }}
        }}");

        // 3. Delete
        handler.HandleUpdate($@"{{
            ""@type"": ""updateDeleteMessages"",
            ""chat_id"": {chatId},
            ""message_ids"": [{tdlibMsgId}],
            ""is_permanent"": true,
            ""from_cache"": false
        }}");

        // Assert no logs contain secret text or caption
        foreach (var entry in logger.Entries)
        {
            Assert.DoesNotContain(secretText, entry.Message);
            Assert.DoesNotContain(secretCaption, entry.Message);
            if (entry.Exception != null)
            {
                Assert.DoesNotContain(secretText, entry.Exception.Message);
                Assert.DoesNotContain(secretCaption, entry.Exception.Message);
            }
        }
    }

    [Fact]
    public void Test15_Handler_failure_on_one_update_is_logged_at_Error_and_next_update_is_processed()
    {
        var dbPath = CreateTempDbPath();
        var cache = new MessageCache(dbPath);
        cache.Initialize();

        var scope = new AllowAllCaptureScope();
        var logger = new TestLogger<CaptureUpdateHandler>();
        var handler = new CaptureUpdateHandler(cache, scope, logger: logger, accountId: "7053823996");

        // Malformed update causes an exception
        handler.HandleUpdate("{ malformed json !!!");

        Assert.Equal(1, handler.ErrorCount);
        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Error);

        // Next valid update is processed successfully
        long chatId = -1002827825432L;
        long srvMsgId = 111L;
        long tdlibMsgId = srvMsgId << 20;

        handler.HandleUpdate($@"{{
            ""@type"": ""updateNewMessage"",
            ""message"": {{
                ""id"": {tdlibMsgId},
                ""chat_id"": {chatId},
                ""sender_id"": {{ ""@type"": ""messageSenderUser"", ""user_id"": 7053823996 }},
                ""is_outgoing"": false,
                ""date"": 1787000000,
                ""content"": {{
                    ""@type"": ""messageText"",
                    ""text"": {{ ""text"": ""Valid message"" }}
                }}
            }}
        }}");

        Assert.NotNull(cache.Get(chatId, srvMsgId));
    }

    [Fact]
    public void Test16_Non_ASCII_text_arrives_unchanged_in_payload_json()
    {
        var dbPath = CreateTempDbPath();
        var cache = new MessageCache(dbPath);
        cache.Initialize();

        var scope = new AllowAllCaptureScope();
        var handler = new CaptureUpdateHandler(cache, scope, accountId: "7053823996");

        long chatId = -1002827825432L;
        long srvMsgId = 222L;
        long tdlibMsgId = srvMsgId << 20;

        // Uzbek, Russian, and Emoji
        const string nonAsciiText = "O'chirildi 📌 —— test, Привет мир! Salom dunyo ✨";

        cache.Put(new CachedMessage(chatId, srvMsgId, nonAsciiText, "7053823996", false, false, null, 1787000000, 1787000000));

        handler.HandleUpdate($@"{{
            ""@type"": ""updateDeleteMessages"",
            ""chat_id"": {chatId},
            ""message_ids"": [{tdlibMsgId}],
            ""is_permanent"": true,
            ""from_cache"": false
        }}");

        var rows = cache.GetOutboxRows();
        Assert.Single(rows);

        Assert.Contains(nonAsciiText, rows[0].PayloadJson);
        Assert.DoesNotContain(@"\u", rows[0].PayloadJson);
    }

    [Fact]
    public void Test17_Updates_received_before_account_id_is_known_are_not_lost()
    {
        var dbPath = CreateTempDbPath();
        var cache = new MessageCache(dbPath);
        cache.Initialize();

        var scope = new AllowAllCaptureScope();
        // Handler initialized WITHOUT accountId
        var handler = new CaptureUpdateHandler(cache, scope, accountId: null);

        long chatId = -1002827825432L;
        long srvMsgId = 333L;
        long tdlibMsgId = srvMsgId << 20;

        // 1. Message arrives before account_id is known
        handler.HandleUpdate($@"{{
            ""@type"": ""updateNewMessage"",
            ""message"": {{
                ""id"": {tdlibMsgId},
                ""chat_id"": {chatId},
                ""sender_id"": {{ ""@type"": ""messageSenderUser"", ""user_id"": 7053823996 }},
                ""is_outgoing"": false,
                ""date"": 1787000000,
                ""content"": {{
                    ""@type"": ""messageText"",
                    ""text"": {{ ""text"": ""Early message"" }}
                }}
            }}
        }}");

        // 2. Deletion arrives before account_id is known
        handler.HandleUpdate($@"{{
            ""@type"": ""updateDeleteMessages"",
            ""chat_id"": {chatId},
            ""message_ids"": [{tdlibMsgId}],
            ""is_permanent"": true,
            ""from_cache"": false
        }}");

        // Nothing should be written to outbox yet because account_id is unknown
        Assert.Empty(cache.GetOutboxRows());

        // 3. updateOption with my_id arrives from TDLib
        handler.HandleUpdate(@"{
            ""@type"": ""updateOption"",
            ""name"": ""my_id"",
            ""value"": {
                ""@type"": ""optionValueInteger"",
                ""value"": 7053823996
            }
        }");

        Assert.Equal("7053823996", handler.AccountId);

        // Now both early updates must have been drained and processed!
        var rows = cache.GetOutboxRows();
        Assert.Single(rows);
        Assert.Equal("deleted", rows[0].Kind);
        Assert.Equal("7053823996", rows[0].AccountId);
        Assert.Equal("562952781246744", rows[0].PeerId);
        Assert.Equal(srvMsgId, rows[0].MsgId);

        var payload = JsonNode.Parse(rows[0].PayloadJson)!.AsObject();
        Assert.Equal("Early message", payload["text"]!.GetValue<string>());
    }

    [Fact]
    public async Task Test18_Resolving_only_ITdClient_attaches_the_handler()
    {
        // Worker faqat ITdClient'ni oladi — ulash shunga bog'liq bo'lishi kerak
        var dbPath = CreateTempDbPath();
        var transport = new FakeTdTransport();
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Capture:CacheDatabasePath"] = dbPath
        }).Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<ITdTransport>(transport);
        services.AddSingleton<ITdClient, TdClient>();
        services.AddMessageCache(config);
        services.AddCaptureHandlers();
        services.AddSingleton<ICaptureScope>(new AllowAllCaptureScope());
        using var sp = services.BuildServiceProvider();
        var cache = sp.GetRequiredService<MessageCache>();
        cache.Initialize();

        _ = sp.GetRequiredService<ITdClient>();

        long chatId = 7053823996L;
        long tdlibMsgId = 42L << 20;
        // TDLib int64 ni JSON'da satr qilib yuboradi
        transport.EnqueueIncoming(@"{ ""@type"": ""updateOption"", ""name"": ""my_id"",
            ""value"": { ""@type"": ""optionValueInteger"", ""value"": ""7053823996"" } }");
        transport.EnqueueIncoming($@"{{ ""@type"": ""updateNewMessage"", ""message"": {{
            ""id"": {tdlibMsgId}, ""chat_id"": {chatId},
            ""sender_id"": {{ ""@type"": ""messageSenderUser"", ""user_id"": 7053823996 }},
            ""is_outgoing"": true, ""date"": 1787000000,
            ""content"": {{ ""@type"": ""messageText"", ""text"": {{ ""text"": ""salom"" }} }} }} }}");
        transport.EnqueueIncoming($@"{{ ""@type"": ""updateDeleteMessages"", ""chat_id"": {chatId},
            ""message_ids"": [{tdlibMsgId}], ""is_permanent"": true, ""from_cache"": false }}");

        var rows = await WaitFor(() => cache.GetOutboxRows(), r => r.Count > 0);
        Assert.Single(rows);
        Assert.Equal("7053823996", rows[0].AccountId);
        Assert.Equal("7053823996", rows[0].PeerId);
        Assert.Equal(42L, rows[0].MsgId);
    }

    [Fact]
    public void Test16b_Payload_with_quotes_backslashes_and_control_chars_is_valid_json_and_round_trips()
    {
        // PayloadBuilder qo'lda yozilgan serializer: har bir maxsus belgi
        // noto'g'ri escape qilinsa, yozuv Task 6 da o'qib bo'lmaydigan bo'ladi.
        var dbPath = CreateTempDbPath();
        var cache = new MessageCache(dbPath);
        cache.Initialize();
        var handler = new CaptureUpdateHandler(cache, new AllowAllCaptureScope(), accountId: "7053823996");

        long chatId = 7053823996L;
        long srvMsgId = 223L;
        const string tricky = "u \"dedi\" C:\\yo'l\\fayl\nikkinchi\tqator\r\u0001\u001f 📌 </script> \u2028";
        cache.Put(new CachedMessage(chatId, srvMsgId, tricky, "7053823996", false, false, null, 1787000000));

        handler.HandleUpdate($@"{{ ""@type"": ""updateDeleteMessages"", ""chat_id"": {chatId},
            ""message_ids"": [{srvMsgId << 20}], ""is_permanent"": true, ""from_cache"": false }}");

        var rows = cache.GetOutboxRows();
        Assert.Single(rows);
        var payload = JsonNode.Parse(rows[0].PayloadJson)!.AsObject();
        Assert.Equal(tricky, payload["text"]!.GetValue<string>());
        Assert.Equal(new[] { "account_id", "peer_id", "text", "sender_id", "is_out", "is_media" },
            payload.Select(p => p.Key).ToArray());
    }

    [Fact]
    public void Test16c_Delete_after_peer_left_scope_emits_nothing()
    {
        // Task 5 da chat scope'dan chiqarilishi mumkin; keshda qolgan eski
        // xabarlar ham shundan keyin yozilmasligi kerak.
        var dbPath = CreateTempDbPath();
        var cache = new MessageCache(dbPath);
        cache.Initialize();
        bool inScope = true;
        var handler = new CaptureUpdateHandler(cache, new FilterCaptureScope(_ => inScope), accountId: "7053823996");

        long chatId = 7053823996L;
        long tdlibMsgId = 224L << 20;
        handler.HandleUpdate($@"{{ ""@type"": ""updateNewMessage"", ""message"": {{
            ""id"": {tdlibMsgId}, ""chat_id"": {chatId},
            ""sender_id"": {{ ""@type"": ""messageSenderUser"", ""user_id"": 1 }},
            ""is_outgoing"": false, ""date"": 1787000000,
            ""content"": {{ ""@type"": ""messageText"", ""text"": {{ ""text"": ""a"" }} }} }} }}");
        Assert.NotNull(cache.Get(chatId, 224L));

        inScope = false;
        handler.HandleUpdate($@"{{ ""@type"": ""updateMessageContent"", ""chat_id"": {chatId}, ""message_id"": {tdlibMsgId},
            ""new_content"": {{ ""@type"": ""messageText"", ""text"": {{ ""text"": ""b"" }} }} }}");
        handler.HandleUpdate($@"{{ ""@type"": ""updateDeleteMessages"", ""chat_id"": {chatId},
            ""message_ids"": [{tdlibMsgId}], ""is_permanent"": true, ""from_cache"": false }}");

        Assert.Empty(cache.GetOutboxRows());
    }

    [Fact]
    public void Test18b_AddCaptureHandlers_without_ITdClient_fails_loudly()
    {
        var services = new ServiceCollection();
        Assert.Throws<InvalidOperationException>(() => services.AddCaptureHandlers());
    }

    [Fact]
    public async Task Test19_Missing_my_id_falls_back_to_getMe_when_authorized()
    {
        var dbPath = CreateTempDbPath();
        var cache = new MessageCache(dbPath);
        cache.Initialize();
        var handler = new CaptureUpdateHandler(cache, new AllowAllCaptureScope(), accountId: null);
        var transport = new FakeTdTransport();
        using var client = new TdClient(transport);
        handler.Attach(client);
        RespondTo(transport, "getMe", _ => @"{ ""@type"": ""user"", ""id"": 7053823996 }");

        long chatId = -1002827825432L;
        long tdlibMsgId = 77L << 20;
        transport.EnqueueIncoming($@"{{ ""@type"": ""updateNewMessage"", ""message"": {{
            ""id"": {tdlibMsgId}, ""chat_id"": {chatId},
            ""sender_id"": {{ ""@type"": ""messageSenderUser"", ""user_id"": 1 }},
            ""is_outgoing"": false, ""date"": 1787000000,
            ""content"": {{ ""@type"": ""messageText"", ""text"": {{ ""text"": ""erta"" }} }} }} }}");
        transport.EnqueueIncoming($@"{{ ""@type"": ""updateDeleteMessages"", ""chat_id"": {chatId},
            ""message_ids"": [{tdlibMsgId}], ""is_permanent"": true, ""from_cache"": false }}");
        transport.EnqueueIncoming(@"{ ""@type"": ""updateAuthorizationState"",
            ""authorization_state"": { ""@type"": ""authorizationStateReady"" } }");

        var rows = await WaitFor(() => cache.GetOutboxRows(), r => r.Count > 0);
        Assert.Single(rows);
        Assert.Equal("7053823996", rows[0].AccountId);
        Assert.Equal("7053823996", handler.AccountId);
    }
}
