using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using CustomSync.Capture.Capture;
using CustomSync.Capture.Maintenance;
using CustomSync.Capture.Media;
using CustomSync.Capture.Preflight;
using CustomSync.Capture.Sync;
using CustomSync.Capture.Tdlib;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Xunit;

namespace CustomSync.Tests;

/// <summary>
/// Plan 05 Task 9a tekshiruvida qo'shilgan testlar (2026-10-02). Delegate
/// testlari media qatorini <c>msg_id == message_id</c> bilan yaratardi,
/// production esa <c>message_id</c> ga TDLib ID'sini (server ID &lt;&lt; 20)
/// yozadi — shu farq retention'ning eng og'ir nuqsonini yashirgan edi.
/// </summary>
public class CaptureStorageVerificationTests : IDisposable
{
    private readonly List<string> _paths = new();

    private string TempPath(string suffix)
    {
        var p = Path.Combine(Path.GetTempPath(), $"cs-t9v-{Guid.NewGuid():N}{suffix}");
        _paths.Add(p);
        return p;
    }

    private string TempDir()
    {
        var p = TempPath("-dir");
        Directory.CreateDirectory(p);
        return p;
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        foreach (var p in _paths)
        {
            foreach (var f in new[] { p, p + "-wal", p + "-shm" })
            {
                try { if (File.Exists(f)) File.Delete(f); else if (Directory.Exists(f)) Directory.Delete(f, true); } catch { }
            }
        }
    }

    // ---------- yordamchilar ----------

    private sealed class LevelLogger<T> : ILogger<T>
    {
        private readonly List<(LogLevel Level, string Text)> _entries = new();
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            // Haqiqiy konsol logger'i istisnoni ToString() bilan chiqaradi.
            lock (_entries) _entries.Add((logLevel, formatter(state, exception) + (exception is null ? "" : " | " + exception)));
        }
        public List<(LogLevel Level, string Text)> Entries { get { lock (_entries) return _entries.ToList(); } }
        public string AllText => string.Join("\n", Entries.Select(e => e.Text));
    }

    /// <summary>TdClient ostidagi soxta transport: so'rovlarga javob beradi, update'larni navbatdan beradi.</summary>
    private sealed class QueueTransport : ITdTransport
    {
        private readonly BlockingCollection<string> _incoming = new();
        public readonly ConcurrentQueue<string> Sent = new();
        public Func<JsonObject, JsonObject?>? Reply { get; set; }

        public int CreateClientId() => 1;

        public void Send(int clientId, string requestJson)
        {
            Sent.Enqueue(requestJson);
            var req = JsonNode.Parse(requestJson)!.AsObject();
            var resp = Reply?.Invoke(req);
            if (resp is not null)
            {
                resp["@extra"] = req["@extra"]?.GetValue<string>();
                _incoming.Add(resp.ToJsonString());
            }
        }

        public void PushUpdate(string json) => _incoming.Add(json);

        public string? Receive(double timeoutSeconds)
            => _incoming.TryTake(out var item, TimeSpan.FromSeconds(Math.Min(timeoutSeconds, 0.05))) ? item : null;

        public string? Execute(string requestJson) => null;
        public void Dispose() { }

        public int CountSent(string type) => Sent.Count(s => JsonNode.Parse(s)!["@type"]!.GetValue<string>() == type);
    }

    /// <summary>optimizeStorage va getStorageStatisticsFast ga javob beradi.</summary>
    private static JsonObject? StorageReplies(JsonObject req) => req["@type"]!.GetValue<string>() switch
    {
        "optimizeStorage" => new JsonObject { ["@type"] = "storageStatistics", ["size"] = 0, ["count"] = 0, ["by_chat"] = new JsonArray() },
        "getStorageStatisticsFast" => new JsonObject
        {
            ["@type"] = "storageStatisticsFast", ["files_size"] = 0, ["file_count"] = 0, ["database_size"] = 0,
            ["language_pack_database_size"] = 0, ["log_size"] = 0
        },
        _ => null
    };

    private static async Task<bool> WaitAsync(Func<bool> condition, int timeoutMs = 4000)
    {
        for (var waited = 0; waited < timeoutMs; waited += 25)
        {
            if (condition()) return true;
            await Task.Delay(25);
        }
        return condition();
    }

    private MessageCache NewCache()
    {
        var cache = new MessageCache(TempPath(".db"));
        cache.Initialize();
        return cache;
    }

    private static long Now() => DateTimeOffset.UtcNow.ToUnixTimeSeconds();

    private static CachedMessage Message(long chatId, long serverMsgId) =>
        new(ChatId: chatId, MessageId: serverMsgId, Text: "x", SenderId: "111", IsOut: false, IsMedia: true,
            MediaId: null, Date: 1700000000);

    // 1. Production yo'li: xabar haqiqiy handler orqali keshga va media
    // navbatiga tushadi (captured_media.message_id = TDLib ID = server ID << 20),
    // keyin haqiqiy ro'yxatdagi StorageMaintenance yuradi. Xabar keshda
    // turganda yoki uning `deleted` yozuvi hali outbox'da bo'lganda qator va
    // fayl yo'qolmasligi shart. Avval birinchi yurishdayoq pending qator
    // "yetim" deb o'chirilardi, yuklangan fayl esa 10 daqiqa ichida.
    [Fact]
    public async Task Test01_Media_of_a_cached_message_survives_maintenance_through_the_real_registration()
    {
        var dir = TempDir();
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Telegram:ApiId"] = "12345",
            ["Telegram:ApiHash"] = "test_hash",
            ["Telegram:DatabaseDirectory"] = dir,
            ["Telegram:FilesDirectory"] = dir,
            ["Capture:CacheDatabasePath"] = Path.Combine(dir, "cache.db"),
            ["Capture:Scope:DefaultEnabled"] = "true",
            ["Capture:Media:Enabled"] = "true",
            ["Capture:Media:PeerIds:0"] = "111",
            ["Capture:Media:StorageDirectory"] = Path.Combine(dir, "media"),
        }).Build();

        var transport = new QueueTransport
        {
            Reply = req => req["@type"]!.GetValue<string>() == "getMe"
                ? new JsonObject { ["@type"] = "user", ["id"] = 42 }
                : StorageReplies(req)
        };
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(config);
        services.AddTdlibClient(config);
        services.AddMessageCache(config);
        services.AddCaptureHandlers();
        services.AddCaptureSyncClient(config);
        services.RemoveAll<ITdTransport>();
        services.AddSingleton<ITdTransport>(transport);
        services.RemoveAll<IDiskSpaceProbe>();
        services.AddSingleton<IDiskSpaceProbe>(FixedDiskSpaceProbe.Ample());

        using var provider = services.BuildServiceProvider();
        var cache = provider.GetRequiredService<MessageCache>();
        cache.Initialize();
        _ = provider.GetRequiredService<ITdClient>();
        var store = provider.GetRequiredService<MediaStore>();
        var maintenance = provider.GetRequiredService<StorageMaintenance>();

        transport.PushUpdate("""{ "@type": "updateAuthorizationState", "authorization_state": { "@type": "authorizationStateReady" } }""");
        transport.PushUpdate($$"""
        { "@type": "updateNewMessage", "message": { "@type": "message", "id": {{21L << 20}}, "chat_id": 111, "date": 1700000000,
          "sender_id": { "@type": "messageSenderUser", "user_id": 111 }, "is_outgoing": false,
          "content": { "@type": "messageDocument", "document": { "@type": "document",
            "document": { "@type": "file", "id": 5, "size": 1000, "expected_size": 1000 } } } } }
        """);
        Assert.True(await WaitAsync(() => cache.GetCapturedMedia("111", 21) is not null));
        Assert.Equal(21L << 20, cache.GetCapturedMedia("111", 21)!.MessageId);

        // Navbatdagi qator, xabar keshda.
        await maintenance.RunOnceAsync();
        Assert.Equal("pending", cache.GetCapturedMedia("111", 21)?.Status);

        // Yuklangan qator va ombordagi fayl, xabar hali keshda.
        store.EnsureDirectoryCreated();
        var file = store.PathFor("111", 21);
        File.WriteAllBytes(file, new byte[1000]);
        cache.UpdateMediaDownloaded("111", 21, file, new string('a', 64), 1000);
        await maintenance.RunOnceAsync();
        Assert.Equal("downloaded", cache.GetCapturedMedia("111", 21)?.Status);
        Assert.True(File.Exists(file));

        // Xabar o'chirildi: `deleted` yozuvi outbox'da, media hali yuborilmagan.
        transport.PushUpdate($$"""
        { "@type": "updateDeleteMessages", "chat_id": 111, "message_ids": [{{21L << 20}}], "is_permanent": true, "from_cache": false }
        """);
        Assert.True(await WaitAsync(() => cache.GetOutboxRows("deleted").Count == 1));
        await maintenance.RunOnceAsync();
        Assert.Equal("downloaded", cache.GetCapturedMedia("111", 21)?.Status);
        Assert.True(File.Exists(file));
    }

    // 2. Retention qatorni xabar bilan server ID orqali bog'laydi: keshdagi
    // xabarning media'si qoladi, keshdan chiqib ketgan (o'chirilmagan)
    // xabarniki ketadi — ikkalasi ham production'dagi ID shaklida.
    [Fact]
    public void Test02_Retention_matches_the_message_cache_by_server_message_id()
    {
        var cache = NewCache();
        var store = new MediaStore(TempDir());
        cache.Put(Message(111, 31));
        cache.Put(Message(111, 33));

        foreach (var msgId in new long[] { 31, 32, 33, 34 })
            cache.QueueCapturedMedia("111", msgId, 111, msgId << 20, "messageDocument", 10);

        var kept = store.PathFor("111", 31);
        var gone = store.PathFor("111", 32);
        foreach (var (path, msgId) in new[] { (kept, 31L), (gone, 32L) })
        {
            File.WriteAllBytes(path, new byte[10]);
            cache.UpdateMediaDownloaded("111", msgId, path, new string('a', 64), 10);
        }

        cache.ApplyMediaRetention(Now(), 30, store);

        Assert.Equal("downloaded", cache.GetCapturedMedia("111", 31)?.Status);   // keshda
        Assert.True(File.Exists(kept));
        Assert.Null(cache.GetCapturedMedia("111", 32));                           // keshdan chiqqan
        Assert.False(File.Exists(gone));
        Assert.Equal("pending", cache.GetCapturedMedia("111", 33)?.Status);      // keshda
        Assert.Null(cache.GetCapturedMedia("111", 34));                           // yetim
    }

    // 3. Production kodida "joy yetarli" deb javob beradigan soxta o'lchagichli
    // konstruktor bor edi: u bilan yaratilgan yuklovchi disk to'lganini hech
    // qachon ko'rmasdi va standart ombor yo'liga yozardi (testlar ham shu
    // yo'l bilan C:\var\lib\... ga yozgan).
    [Fact]
    public void Test03_Every_downloader_constructor_requires_the_store_and_the_disk_probe()
    {
        foreach (var ctor in typeof(MediaDownloader).GetConstructors())
        {
            var types = ctor.GetParameters().Select(p => p.ParameterType).ToList();
            Assert.Contains(typeof(MediaStore), types);
            Assert.Contains(typeof(IDiskSpaceProbe), types);
        }

        var probes = typeof(IDiskSpaceProbe).Assembly.GetTypes()
            .Where(t => typeof(IDiskSpaceProbe).IsAssignableFrom(t) && !t.IsInterface)
            .ToList();
        Assert.Equal(new[] { typeof(SystemDiskSpaceProbe) }, probes);
    }

    // 4. Sozlamada oxiri "/" bilan yozilgan ombor. Avval IsManaged hech qachon
    // true bo'lmasdi: fayllar "ombordan tashqarida" hisoblanib o'chirilmasdi,
    // yetim tozalash ularni ko'rmasdi, o'lchov esa 0 ko'rsatardi — disk
    // jimgina to'lardi.
    [Fact]
    public void Test04_Store_configured_with_a_trailing_separator_still_owns_its_files()
    {
        var dir = TempDir();
        var store = new MediaStore(dir + Path.DirectorySeparatorChar);
        Assert.Equal(Path.GetFullPath(dir), store.StorageDirectory);

        var cache = NewCache();
        var uploaded = store.PathFor("111", 41);
        File.WriteAllBytes(uploaded, new byte[] { 1 });
        Assert.True(store.IsManaged(uploaded));
        cache.QueueCapturedMedia("111", 41, 111, 41L << 20, "messageDocument", 1);
        cache.UpdateMediaDownloaded("111", 41, uploaded, new string('b', 64), 1);
        cache.UpdateMediaUploaded("111", 41);

        var stray = store.PathFor("111", 42);
        File.WriteAllBytes(stray, new byte[] { 2, 2 });
        File.SetLastWriteTimeUtc(stray, DateTime.UtcNow.AddHours(-2));

        Assert.Equal((3L, 2), store.MeasureStore());

        var now = Now();
        cache.ApplyMediaRetention(now, 30, store);
        Assert.False(File.Exists(uploaded));
        Assert.Equal(1, store.SweepOrphans(cache, now));
        Assert.False(File.Exists(stray));
    }

    private Dictionary<string, string?> PreflightBase()
    {
        var dir = TempDir();
        return new()
        {
            ["Telegram:ApiId"] = "12345",
            ["Telegram:ApiHash"] = "test_hash",
            ["Telegram:DatabaseDirectory"] = Path.Combine(dir, "tdlib"),
            ["Telegram:FilesDirectory"] = Path.Combine(dir, "files"),
            ["Capture:CacheDatabasePath"] = Path.Combine(dir, "cache.db"),
            ["Capture:Media:StorageDirectory"] = Path.Combine(dir, "media"),
        };
    }

    private static PreflightReport Preflight(Dictionary<string, string?> settings) =>
        CapturePreflight.Check(new ConfigurationBuilder().AddInMemoryCollection(settings).Build(), _ => true);

    // 5. Standart TdlibImmunitySeconds (3600) ham DownloadTimeoutSeconds ning
    // ikki baravaridan kam bo'lmasligi kerak. Avval qoida faqat kalit ochiq
    // yozilganda tekshirilardi, DownloadTimeoutSeconds esa yuqori chegarasiz.
    [Theory]
    [InlineData(null, "1800", true)]
    [InlineData(null, "1801", false)]
    [InlineData("4000", "2000", true)]
    [InlineData("3999", "2000", false)]
    public void Test05_Preflight_checks_the_effective_immunity_against_the_download_timeout(string? immunity, string timeout, bool ok)
    {
        var settings = PreflightBase();
        settings["Capture:Media:DownloadTimeoutSeconds"] = timeout;
        if (immunity is not null) settings["Capture:Storage:TdlibImmunitySeconds"] = immunity;

        var report = Preflight(settings);

        if (ok) Assert.DoesNotContain(report.Errors, e => e.Contains("TdlibImmunitySeconds"));
        else Assert.Contains(report.Errors, e => e.Contains("TdlibImmunitySeconds"));
    }

    // 5b. Har bir ziddiyat o'z xatosini beradi. Delegate testi omborni barcha
    // fayllarni o'z ichiga olgan papkaga qo'yardi va faqat Success=false ni
    // tekshirardi: StatePath tekshiruvi olib tashlansa ham suite yashil edi.
    [Theory]
    [InlineData("Capture:CacheDatabasePath")]
    [InlineData("Capture:Sync:StatePath")]
    [InlineData("Capture:Sync:MasterKeyPath")]
    [InlineData("Telegram:DatabaseDirectory")]
    [InlineData("Telegram:FilesDirectory")]
    public void Test05b_Preflight_reports_each_store_conflict_on_its_own(string conflictingKey)
    {
        var settings = PreflightBase();
        var store = settings["Capture:Media:StorageDirectory"]!;
        settings["Capture:Media:Enabled"] = "true";
        settings["Capture:Sync:StatePath"] = Path.Combine(TempDir(), "device-state.json");
        settings["Capture:Sync:MasterKeyPath"] = Path.Combine(TempDir(), "master.key");
        Assert.True(Preflight(settings).Success, string.Join("; ", Preflight(settings).Errors));

        settings[conflictingKey] = conflictingKey.StartsWith("Telegram:")
            ? Path.Combine(store, "inner")
            : Path.Combine(store, "file.bin");
        var report = Preflight(settings);

        var error = Assert.Single(report.Errors);
        Assert.Contains("StorageDirectory", error);
        Assert.Contains(conflictingKey, error);
    }

    // 6. To'xtash (restart, deploy) optimizeStorage o'rtasida keladi: sikl jim
    // tugaydi. Avval bekor qilish oddiy xato deb ushlanardi — har to'xtashda
    // ikki ogohlantirish va to'xtash paytida o'lchangan "snapshot" chiqardi.
    [Fact]
    public async Task Test06_Shutdown_during_optimizeStorage_ends_the_loop_quietly()
    {
        var transport = new QueueTransport { Reply = _ => null };
        using var client = new TdClient(transport);
        var logger = new LevelLogger<StorageMaintenance>();
        var maintenance = new StorageMaintenance(NewCache(), new MediaStore(TempDir()), client,
            FixedDiskSpaceProbe.Ample(), new MediaCaptureConfig(), logger: logger);

        using var cts = new CancellationTokenSource();
        var loop = maintenance.RunLoopAsync(cts.Token);
        Assert.True(await WaitAsync(() => transport.CountSent("optimizeStorage") == 1));
        cts.Cancel();
        await loop.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Null(maintenance.LatestSnapshot);
        Assert.Equal(0, transport.CountSent("getStorageStatisticsFast"));
        Assert.DoesNotContain(logger.Entries, e => e.Level >= LogLevel.Warning);
    }

    private sealed class ThrowingProbe : IDiskSpaceProbe
    {
        public long? GetAvailableFreeBytes(string path) => throw new IOException($"probe failed at {path}");
    }

    // 7. O'lchov qadamidagi istisno keyingi yurishni to'xtatmaydi. Avval
    // o'lchov va xulosa try/catch'siz edi: istisno RunLoopAsync'dan chiqib,
    // fon vazifasini jimgina o'ldirardi — tozalash butunlay to'xtardi.
    [Fact]
    public async Task Test07_A_failing_measurement_does_not_end_the_loop()
    {
        var transport = new QueueTransport { Reply = StorageReplies };
        using var client = new TdClient(transport);
        var logger = new LevelLogger<StorageMaintenance>();
        var storeDir = TempDir();
        using var cts = new CancellationTokenSource();
        var delays = 0;
        var maintenance = new StorageMaintenance(NewCache(), new MediaStore(storeDir), client,
            new ThrowingProbe(), new MediaCaptureConfig(), logger: logger,
            delay: (_, _) =>
            {
                if (++delays >= 2) cts.Cancel();
                return Task.CompletedTask;
            });

        await maintenance.RunLoopAsync(cts.Token).WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(2, transport.CountSent("optimizeStorage"));
        Assert.NotNull(maintenance.LatestSnapshot);
        Assert.Null(maintenance.LatestSnapshot!.FreeDiskBytes);
        Assert.Contains("IOException", logger.AllText);
        Assert.DoesNotContain(storeDir, logger.AllText);
    }

    private sealed class FailingRetentionCache(string path) : MessageCache(path)
    {
        public override (int RowsPruned, int FilesDeleted) ApplyMediaRetention(
            long now, int retentionDays, MediaStore mediaStore, ILogger? logger = null) =>
            throw new InvalidOperationException("retention failed");
    }

    // 8. Retention yiqilsa ham yetim tozalash, optimizeStorage va o'lchov
    // bajariladi. Delegate testi faqat optimizeStorage xatosini tekshirardi:
    // retention'dagi catch olib tashlansa ham suite yashil edi.
    [Fact]
    public async Task Test08_A_failing_retention_does_not_skip_the_other_steps()
    {
        var cache = new FailingRetentionCache(TempPath(".db"));
        cache.Initialize();
        var store = new MediaStore(TempDir());
        var stray = store.PathFor("111", 71);
        File.WriteAllBytes(stray, new byte[] { 1 });
        File.SetLastWriteTimeUtc(stray, DateTime.UtcNow.AddHours(-2));

        var transport = new QueueTransport { Reply = StorageReplies };
        using var client = new TdClient(transport);
        var logger = new LevelLogger<StorageMaintenance>();
        var maintenance = new StorageMaintenance(cache, store, client, FixedDiskSpaceProbe.Ample(),
            new MediaCaptureConfig(), logger: logger);

        await maintenance.RunOnceAsync();

        Assert.False(File.Exists(stray));
        Assert.Equal(1, transport.CountSent("optimizeStorage"));
        Assert.NotNull(maintenance.LatestSnapshot);
        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Error && e.Text.Contains("InvalidOperationException"));
    }

    // 9. Ombordagi nusxa yangi vaqt tamg'asini oladi. File.Copy manbaning eski
    // vaqtini olib o'tadi: yuklovchi nusxalab, qatorni hali yozmagan paytda
    // yetim tozalash yangi faylni "1 soatdan eski, havolasiz" deb o'chirardi.
    [Fact]
    public async Task Test09_Copy_in_gives_the_stored_file_a_fresh_timestamp()
    {
        var store = new MediaStore(TempDir());
        var source = TempPath(".src");
        File.WriteAllBytes(source, new byte[] { 1, 2, 3 });
        File.SetLastWriteTimeUtc(source, DateTime.UtcNow.AddDays(-3));

        var (path, _, _) = await store.CopyInAsync("111", 51, source, 100);

        Assert.Equal(0, store.SweepOrphans(NewCache(), Now()));
        Assert.True(File.Exists(path));
    }

    // 10. Task 8 qatorlari TDLib keshiga ishora qiladi. Yetim `downloaded`
    // qator o'chiriladi, lekin ombordan tashqaridagi fayl qoladi (5-qoida).
    // Delegate testi 5-qoidani faqat 1-qoida holatlari uchun tekshirardi.
    [Fact]
    public void Test10_An_orphan_downloaded_row_outside_the_store_keeps_its_file()
    {
        var cache = NewCache();
        var store = new MediaStore(TempDir());
        var outside = TempPath(".bin");
        File.WriteAllBytes(outside, new byte[] { 7 });
        cache.QueueCapturedMedia("111", 61, 111, 61L << 20, "messageDocument", 1);
        cache.UpdateMediaDownloaded("111", 61, outside, new string('c', 64), 1);

        cache.ApplyMediaRetention(Now(), 30, store);

        Assert.Null(cache.GetCapturedMedia("111", 61));
        Assert.True(File.Exists(outside));
    }

    // 11. Darvoza: promptdagi qolgan shakllar (null, -1, chat_limit va
    // return_deleted_file_statistics) — delegate Theory'sida yo'q edi.
    [Theory]
    [InlineData("size_null", """{"@type":"optimizeStorage","size":null,"ttl":3600,"count":-1,"immunity_delay":600}""")]
    [InlineData("count_null", """{"@type":"optimizeStorage","size":16777216,"ttl":3600,"count":null,"immunity_delay":600}""")]
    [InlineData("ttl_neg1", """{"@type":"optimizeStorage","size":16777216,"ttl":-1,"count":-1,"immunity_delay":600}""")]
    [InlineData("ttl_3599", """{"@type":"optimizeStorage","size":16777216,"ttl":3599,"count":-1,"immunity_delay":600}""")]
    [InlineData("immunity_neg1", """{"@type":"optimizeStorage","size":16777216,"ttl":3600,"count":-1,"immunity_delay":-1}""")]
    [InlineData("chat_limit_101", """{"@type":"optimizeStorage","size":16777216,"ttl":3600,"count":-1,"immunity_delay":600,"chat_limit":101}""")]
    [InlineData("chat_limit_neg1", """{"@type":"optimizeStorage","size":16777216,"ttl":3600,"count":-1,"immunity_delay":600,"chat_limit":-1}""")]
    [InlineData("rdfs_number", """{"@type":"optimizeStorage","size":16777216,"ttl":3600,"count":-1,"immunity_delay":600,"return_deleted_file_statistics":1}""")]
    [InlineData("chat_ids_null", """{"@type":"optimizeStorage","size":16777216,"ttl":3600,"count":-1,"immunity_delay":600,"chat_ids":null}""")]
    [InlineData("fast_stats_extra", """{"@type":"getStorageStatisticsFast","chat_limit":1}""")]
    [InlineData("verbosity_null", """{"@type":"setLogVerbosityLevel","new_verbosity_level":null}""")]
    public async Task Test11_Gate_rejects_the_remaining_bad_shapes(string caseName, string payload)
    {
        Assert.False(string.IsNullOrEmpty(caseName));
        var transport = new QueueTransport();
        using var client = new TdClient(transport);
        await Assert.ThrowsAnyAsync<TdRequestNotAllowedException>(() => client.SendAsync(payload));
        Assert.True(transport.Sent.IsEmpty);
    }

    // 12. Xulosa qatori yetim tozalash nechta fayl o'chirganini ham aytadi —
    // aks holda tozalash ishlayaptimi yoki yo'qmi, log'dan ko'rinmaydi.
    [Fact]
    public async Task Test12_Summary_reports_the_orphan_sweep_count()
    {
        var store = new MediaStore(TempDir());
        foreach (var msgId in new long[] { 81, 82 })
        {
            var path = store.PathFor("111", msgId);
            File.WriteAllBytes(path, new byte[] { 1 });
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddHours(-2));
        }

        var transport = new QueueTransport { Reply = StorageReplies };
        using var client = new TdClient(transport);
        var logger = new LevelLogger<StorageMaintenance>();
        var maintenance = new StorageMaintenance(NewCache(), store, client, FixedDiskSpaceProbe.Ample(),
            new MediaCaptureConfig(), logger: logger);

        await maintenance.RunOnceAsync();

        var summary = Assert.Single(logger.Entries, e => e.Level == LogLevel.Information);
        Assert.Contains("SweptOrphans=2", summary.Text);
    }

    // 13. optimizeStorage'ning TdException bo'lmagan xatosi (bu yerda transport)
    // ham o'lchov va xulosani tashlab ketmaydi. Delegate testi faqat `error`
    // javobini tekshirardi — u esa haqiqiy TdClient'da TdException bo'ladi.
    [Fact]
    public async Task Test13_A_transport_failure_in_optimizeStorage_does_not_skip_the_snapshot()
    {
        var transport = new QueueTransport
        {
            Reply = req => req["@type"]!.GetValue<string>() == "optimizeStorage"
                ? throw new IOException("transport failed")
                : StorageReplies(req)
        };
        using var client = new TdClient(transport);
        var logger = new LevelLogger<StorageMaintenance>();
        var maintenance = new StorageMaintenance(NewCache(), new MediaStore(TempDir()), client,
            FixedDiskSpaceProbe.Ample(), new MediaCaptureConfig(), logger: logger);

        await maintenance.RunOnceAsync();

        Assert.NotNull(maintenance.LatestSnapshot);
        Assert.Equal(1, transport.CountSent("getStorageStatisticsFast"));
        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Warning && e.Text.Contains("IOException"));
    }

    private sealed class InformationThrowingLogger<T> : ILogger<T>
    {
        private readonly List<string> _errors = new();
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Information) throw new InvalidOperationException("log sink failed");
            if (logLevel == LogLevel.Error) lock (_errors) _errors.Add(formatter(state, exception));
        }
        public List<string> Errors { get { lock (_errors) return _errors.ToList(); } }
    }

    // 14. Oxirgi to'siq: qadamlardan tashqaridagi istisno (bu yerda xulosa
    // log'i) siklni o'ldirmaydi — keyingi yurish bo'ladi.
    [Fact]
    public async Task Test14_An_exception_outside_the_steps_does_not_end_the_loop()
    {
        var transport = new QueueTransport { Reply = StorageReplies };
        using var client = new TdClient(transport);
        var logger = new InformationThrowingLogger<StorageMaintenance>();
        using var cts = new CancellationTokenSource();
        var delays = 0;
        var maintenance = new StorageMaintenance(NewCache(), new MediaStore(TempDir()), client,
            FixedDiskSpaceProbe.Ample(), new MediaCaptureConfig(), logger: logger,
            delay: (_, _) =>
            {
                if (++delays >= 2) cts.Cancel();
                return Task.CompletedTask;
            });

        await maintenance.RunLoopAsync(cts.Token).WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(2, transport.CountSent("optimizeStorage"));
        Assert.Contains(logger.Errors, e => e.Contains("InvalidOperationException"));
    }
}
