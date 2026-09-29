using System.Text.Json.Nodes;
using CustomSync.Capture.Capture;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CustomSync.Tests;

/// <summary>
/// Task 4c tekshiruvida topilgan bo'shliqlar: tahrir juftlanmay turib
/// xabar o'chirilsa, sweep xatosi, sweep chastotasi va
/// `Capture:EditPairingTimeoutSeconds` ning haqiqiy ro'yxatdan o'tishi.
/// </summary>
public class CaptureEditDateVerificationTests : IDisposable
{
    private const string AccountId = "7053823996";
    private const long ChatId = -1002827825432L; // peer "562952781246744"
    private const long SendDate = 1787000005L;
    private static readonly DateTimeOffset T0 = DateTimeOffset.FromUnixTimeSeconds(1787000100);

    private readonly List<string> _tempFiles = new();

    private string CreateTempDbPath()
    {
        var path = Path.Combine(Path.GetTempPath(), $"capture_edit_verify_{Guid.NewGuid():N}.db");
        _tempFiles.Add(path);
        return path;
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        foreach (var file in _tempFiles)
        {
            foreach (var f in new[] { file, file + "-wal", file + "-shm" })
            {
                try { if (File.Exists(f)) File.Delete(f); } catch { }
            }
        }
    }

    private static string Content(long srvMsgId, string text) => $@"{{
        ""@type"": ""updateMessageContent"",
        ""chat_id"": {ChatId},
        ""message_id"": {srvMsgId << 20},
        ""new_content"": {{ ""@type"": ""messageText"", ""text"": {{ ""text"": ""{text}"" }} }}
    }}";

    private static string Delete(long srvMsgId) => $@"{{
        ""@type"": ""updateDeleteMessages"",
        ""chat_id"": {ChatId},
        ""message_ids"": [{srvMsgId << 20}],
        ""is_permanent"": true,
        ""from_cache"": false
    }}";

    private const string Noise = @"{ ""@type"": ""updateUserStatus"", ""user_id"": 1, ""status"": { ""@type"": ""userStatusEmpty"" } }";

    private static void Exec(string dbPath, string sql)
    {
        using var conn = new SqliteConnection($"Data Source={dbPath};");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private static long PendingCount(string dbPath)
    {
        using var conn = new SqliteConnection($"Data Source={dbPath};");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM pending_edits;";
        return Convert.ToInt64(cmd.ExecuteScalar());
    }

    [Fact]
    public void V01_Delete_while_an_edit_waits_for_its_edit_date_keeps_the_edit()
    {
        // Tahrirdan keyin 60 soniya ichida o'chirish — anti-edit/anti-delete
        // ning eng odatiy holati. Kutayotgan tahrir jimgina tashlansa, eski
        // matn hech qayerda qolmaydi: `deleted` yozuvi yangi matnni oladi.
        var dbPath = CreateTempDbPath();
        var cache = new MessageCache(dbPath);
        cache.Initialize();
        var handler = new CaptureUpdateHandler(cache, new AllowAllCaptureScope(), accountId: AccountId);

        cache.Put(new CachedMessage(ChatId, 100, "Old Text", AccountId, false, false, null, SendDate));
        handler.HandleUpdate(Content(100, "New Text"));
        handler.HandleUpdate(Delete(100));

        var rows = cache.GetOutboxRows();
        var edited = Assert.Single(rows, r => r.Kind == "edited");
        Assert.Equal(SendDate, edited.OccurredAt);
        var ep = JsonNode.Parse(edited.PayloadJson)!.AsObject();
        Assert.Equal("Old Text", ep["old_text"]!.GetValue<string>());
        Assert.Equal("New Text", ep["new_text"]!.GetValue<string>());

        var deleted = Assert.Single(rows, r => r.Kind == "deleted");
        Assert.Equal("New Text", JsonNode.Parse(deleted.PayloadJson)!["text"]!.GetValue<string>());
        Assert.Equal(0, PendingCount(dbPath));
    }

    [Fact]
    public void V02_Delete_with_only_an_edit_date_waiting_emits_just_the_delete()
    {
        var dbPath = CreateTempDbPath();
        var cache = new MessageCache(dbPath);
        cache.Initialize();
        var handler = new CaptureUpdateHandler(cache, new AllowAllCaptureScope(), accountId: AccountId);

        cache.Put(new CachedMessage(ChatId, 100, "Text", AccountId, false, false, null, SendDate));
        handler.HandleUpdate($@"{{ ""@type"": ""updateMessageEdited"", ""chat_id"": {ChatId}, ""message_id"": {100L << 20}, ""edit_date"": 1787000010 }}");
        handler.HandleUpdate(Delete(100));

        var row = Assert.Single(cache.GetOutboxRows());
        Assert.Equal("deleted", row.Kind);
        Assert.Equal(0, PendingCount(dbPath));
    }

    [Fact]
    public void V03_A_failing_sweep_does_not_swallow_the_update_being_handled()
    {
        var dbPath = CreateTempDbPath();
        var time = new TestTimeProvider(T0);
        var cache = new MessageCache(dbPath, time);
        cache.Initialize();
        var handler = new CaptureUpdateHandler(cache, new AllowAllCaptureScope(), time, accountId: AccountId);

        cache.Put(new CachedMessage(ChatId, 100, "Old", AccountId, false, false, null, SendDate));
        cache.Put(new CachedMessage(ChatId, 200, "Keep me", AccountId, false, false, null, SendDate));
        handler.HandleUpdate(Content(100, "New"));

        // 100 ning tahriri muddatidan o'tdi, lekin uni sweep qilish yiqiladi.
        Exec(dbPath, "CREATE TRIGGER fail_sweep BEFORE DELETE ON pending_edits BEGIN SELECT RAISE(ABORT, 'forced'); END;");
        time.Advance(TimeSpan.FromSeconds(120));

        long errorsBefore = handler.ErrorCount;
        handler.HandleUpdate(Delete(200));

        var deleted = Assert.Single(cache.GetOutboxRows(), r => r.Kind == "deleted");
        Assert.Equal(200, deleted.MsgId);
        Assert.True(handler.ErrorCount > errorsBefore);
        Assert.Equal(1, PendingCount(dbPath));
    }

    private sealed class CountingCache(string path, TimeProvider time) : MessageCache(path, time)
    {
        public int Sweeps;

        public override int SweepPendingEdits(long now, int timeoutSeconds = 60)
        {
            Sweeps++;
            return base.SweepPendingEdits(now, timeoutSeconds);
        }
    }

    [Fact]
    public void V04_The_per_update_sweep_runs_at_most_once_per_second()
    {
        // Har bir update'da sweep yozish tranzaksiyasini ochardi — faol
        // akkauntda soniyasiga yuzlab marta. Muddat soniyalarda o'lchanadi,
        // bir soniya ichida ikkinchi sweep yangi hech narsa topmaydi.
        var dbPath = CreateTempDbPath();
        var time = new TestTimeProvider(T0);
        var cache = new CountingCache(dbPath, time);
        cache.Initialize();
        var handler = new CaptureUpdateHandler(cache, new AllowAllCaptureScope(), time, accountId: AccountId);

        for (int i = 0; i < 20; i++)
            handler.HandleUpdate(Noise);
        Assert.Equal(1, cache.Sweeps);

        time.Advance(TimeSpan.FromSeconds(1));
        handler.HandleUpdate(Noise);
        handler.HandleUpdate(Noise);
        Assert.Equal(2, cache.Sweeps);
    }

    private static (ServiceProvider Sp, TestTimeProvider Time) BuildProduction(string dbPath, string timeoutSeconds)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Capture:CacheDatabasePath"] = dbPath,
                ["Capture:Scope:DefaultEnabled"] = "true",
                ["Capture:EditPairingTimeoutSeconds"] = timeoutSeconds,
            })
            .Build();
        var time = new TestTimeProvider(T0);
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(config);
        services.AddSingleton<TimeProvider>(time);
        services.AddMessageCache(config);
        services.AddSingleton<CustomSync.Capture.Tdlib.ITdTransport, FakeTdTransport>();
        services.AddSingleton<CustomSync.Capture.Tdlib.ITdClient, CustomSync.Capture.Tdlib.TdClient>();
        services.AddCaptureHandlers();
        return (services.BuildServiceProvider(), time);
    }

    [Fact]
    public void V05_Registered_handler_uses_the_configured_pairing_timeout()
    {
        var dbPath = CreateTempDbPath();
        var (sp, time) = BuildProduction(dbPath, "10");
        using var _ = sp;
        var cache = sp.GetRequiredService<MessageCache>();
        cache.Initialize();
        var handler = sp.GetRequiredService<CaptureUpdateHandler>();
        handler.HandleUpdate($@"{{ ""@type"": ""updateOption"", ""name"": ""my_id"", ""value"": {{ ""@type"": ""optionValueInteger"", ""value"": {AccountId} }} }}");

        cache.Put(new CachedMessage(ChatId, 100, "Old", AccountId, false, false, null, SendDate));
        handler.HandleUpdate(Content(100, "New"));

        time.Advance(TimeSpan.FromSeconds(15));
        handler.HandleUpdate(Noise);

        var row = Assert.Single(cache.GetOutboxRows());
        Assert.Equal(SendDate, row.OccurredAt);
    }

    [Fact]
    public void V06_Registered_pruner_sweeps_with_the_configured_timeout()
    {
        // Pruner sikli ishga tushishda va har intervalda sweep qiladi —
        // update kelmaydigan jim akkauntda juftsiz tahrir faqat shu yo'l
        // bilan chiqadi.
        var dbPath = CreateTempDbPath();
        var (sp, time) = BuildProduction(dbPath, "10");
        using var _ = sp;
        var cache = sp.GetRequiredService<MessageCache>();
        cache.Initialize();
        var handler = new CaptureUpdateHandler(cache, new AllowAllCaptureScope(), time, accountId: AccountId);

        cache.Put(new CachedMessage(ChatId, 100, "Old", AccountId, false, false, null, SendDate));
        handler.HandleUpdate(Content(100, "New"));
        time.Advance(TimeSpan.FromSeconds(15));

        sp.GetRequiredService<PeriodicCachePruner>().PruneOnce();

        var row = Assert.Single(cache.GetOutboxRows());
        Assert.Equal("edited", row.Kind);
        Assert.Equal(SendDate, row.OccurredAt);
    }
}
