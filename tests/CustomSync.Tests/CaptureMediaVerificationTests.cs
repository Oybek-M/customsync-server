using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CustomSync.Capture.Capture;
using CustomSync.Capture.Media;
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
/// Plan 05 Task 8 tekshiruvida qo'shilgan testlar (2026-10-01). Delegate
/// testlari soxta ITdClient (TDLib xatosini istisnoga aylantirmaydi) va
/// push'ga bo'sh 200 qaytaradigan soxta server bilan ishlardi; bu yerda
/// haqiqiy TdClient, haqiqiy push javobi shakli va haqiqiy ro'yxat.
/// </summary>
public class CaptureMediaVerificationTests : IDisposable
{
    private readonly List<string> _paths = new();

    private string TempPath(string ext)
    {
        var p = Path.Combine(Path.GetTempPath(), $"cs-t8v-{Guid.NewGuid():N}{ext}");
        _paths.Add(p);
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

    private sealed class RenderingLogger<T> : ILogger<T>
    {
        private readonly List<string> _lines = new();
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            // Haqiqiy konsol logger'i istisnoni ToString() bilan chiqaradi —
            // shuning uchun bu yerda ham, aks holda yo'l sizishi ko'rinmaydi.
            lock (_lines) _lines.Add(formatter(state, exception) + (exception is null ? "" : " | " + exception));
        }
        public string AllText { get { lock (_lines) return string.Join("\n", _lines); } }
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
    }

    private sealed class FakeServer : HttpMessageHandler
    {
        public readonly List<string> Calls = new();
        public readonly List<JsonObject> PushedRecords = new();
        public readonly Dictionary<string, (byte[] Body, string Nonce)> Blobs = new();
        public Func<HttpRequestMessage, HttpResponseMessage?>? Override { get; set; }
        public bool FailNextPush { get; set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
        {
            var path = req.RequestUri!.AbsolutePath;
            lock (Calls) Calls.Add($"{req.Method} {path}");

            var overridden = Override?.Invoke(req);
            if (overridden is not null) return overridden;

            if (path.EndsWith("/api/v1/devices/refresh"))
                return Json(new { refresh_token = "ref-123", access_token = "acc-token", expires_at = 9999999999L });

            if (path.StartsWith("/api/v1/media/"))
            {
                var hash = path["/api/v1/media/".Length..];
                if (req.Method == HttpMethod.Head)
                {
                    if (!Blobs.TryGetValue(hash, out var blob)) return new HttpResponseMessage(HttpStatusCode.NotFound);
                    var ok = new HttpResponseMessage(HttpStatusCode.OK);
                    ok.Headers.Add("X-Nonce", blob.Nonce);
                    return ok;
                }
                if (req.Method == HttpMethod.Put)
                {
                    var body = await req.Content!.ReadAsByteArrayAsync(ct);
                    var nonce = req.Headers.GetValues("X-Nonce").Single();
                    Blobs.TryAdd(hash, (body, nonce));
                    return new HttpResponseMessage(HttpStatusCode.OK);
                }
            }

            if (path.EndsWith("/api/v1/sync/push"))
            {
                if (FailNextPush)
                {
                    FailNextPush = false;
                    throw new HttpRequestException("push yiqildi");
                }
                var doc = JsonNode.Parse(await req.Content!.ReadAsStringAsync(ct))!.AsObject();
                var results = new JsonArray();
                foreach (var r in doc["records"]!.AsArray())
                {
                    PushedRecords.Add(r!.AsObject().DeepClone().AsObject());
                    results.Add(new JsonObject { ["record_id"] = r!["record_id"]!.GetValue<string>(), ["status"] = "created", ["seq"] = 1 });
                }
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(new JsonObject { ["results"] = results }.ToJsonString(), Encoding.UTF8, "application/json")
                };
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }

        private static HttpResponseMessage Json(object o) => new(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(o), Encoding.UTF8, "application/json")
        };

        public int Count(string prefix) { lock (Calls) return Calls.Count(c => c.StartsWith(prefix)); }
    }

    private sealed class RunnerSetup
    {
        public required MessageCache Cache { get; init; }
        public required CaptureSyncRunner Runner { get; init; }
        public required FakeServer Server { get; init; }
        public required byte[] MasterKey { get; init; }
        public required string DbPath { get; init; }
    }

    private RunnerSetup CreateRunner(ILogger<CaptureSyncRunner>? logger = null)
    {
        var dbPath = TempPath(".db");
        var cache = new MessageCache(dbPath);
        cache.Initialize();

        var keyPath = TempPath(".key");
        var statePath = TempPath(".json");
        var master = RandomNumberGenerator.GetBytes(32);
        File.WriteAllText(keyPath, Convert.ToHexString(master).ToLowerInvariant());
        File.WriteAllText(statePath, JsonSerializer.Serialize(new { device_id = "test-device", refresh_token = "ref-123" }));

        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Capture:Sync:Enabled"] = "true",
            ["Capture:Sync:ServerUrl"] = "https://localhost:5001",
            ["Capture:Sync:MasterKeyPath"] = keyPath,
            ["Capture:Sync:StatePath"] = statePath,
        }).Build();

        var server = new FakeServer();
        var runner = new CaptureSyncRunner(cache, new CaptureSyncHttpClient(new HttpClient(server)), config, logger: logger);
        return new RunnerSetup { Cache = cache, Runner = runner, Server = server, MasterKey = master, DbPath = dbPath };
    }

    private static void InsertDeletedRow(string dbPath, string peerId, long msgId)
    {
        using var conn = new SqliteConnection($"Data Source={dbPath}");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            INSERT INTO capture_outbox (kind, account_id, peer_id, msg_id, occurred_at, observed_at, payload_json, created_at)
            VALUES ('deleted', 'acc1', @peer, @msg, 1000, 1000, @payload, 1000);";
        cmd.Parameters.AddWithValue("@peer", peerId);
        cmd.Parameters.AddWithValue("@msg", msgId);
        cmd.Parameters.AddWithValue("@payload", JsonSerializer.Serialize(new { account_id = "acc1", peer_id = peerId, text = "matn" }));
        cmd.ExecuteNonQuery();
    }

    private (string Path, byte[] Bytes, string Sha) DownloadedMedia(MessageCache cache, string peerId, long msgId, string marker = "MAXFIY-FAYL")
    {
        var path = TempPath($"-{marker}.bin");
        var bytes = Encoding.UTF8.GetBytes($"{marker} mazmuni {Guid.NewGuid()}");
        File.WriteAllBytes(path, bytes);
        var sha = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        cache.QueueCapturedMedia(peerId, msgId, 555, msgId << 20, "messageDocument", bytes.Length);
        cache.UpdateMediaDownloaded(peerId, msgId, path, sha, bytes.Length);
        return (path, bytes, sha);
    }

    private static JsonObject? PushedFor(FakeServer server, long msgId)
        => server.PushedRecords.LastOrDefault(r => r["msg_id"]!.GetValue<long>() == msgId);

    // ---------- testlar ----------

    // 18. Vaqtinchalik server xatosi media'ni yo'qotmasligi kerak. Yozuv
    // o'zgarmas (record_id deterministik), ya'ni bir marta mediasiz ketsa,
    // media unga hech qachon ulanmaydi.
    [Fact]
    public async Task Test18_Put_5xx_holds_the_row_and_a_later_cycle_pushes_it_with_media()
    {
        var s = CreateRunner();
        InsertDeletedRow(s.DbPath, "111", 7);
        var media = DownloadedMedia(s.Cache, "111", 7);

        s.Server.Override = req => req.Method == HttpMethod.Put ? new HttpResponseMessage(HttpStatusCode.InternalServerError) : null;
        await s.Runner.PushCycleAsync();

        Assert.Null(PushedFor(s.Server, 7));
        Assert.Single(s.Cache.GetOutboxRows());
        Assert.Equal("downloaded", s.Cache.GetCapturedMedia("111", 7)!.Status);

        s.Server.Override = null;
        ForceRowDue(s.DbPath);
        await s.Runner.PushCycleAsync();

        var pushed = PushedFor(s.Server, 7);
        Assert.NotNull(pushed);
        var refs = pushed!["media"]!.AsArray();
        Assert.Single(refs);
        Assert.Equal(media.Sha, refs[0]!["hash"]!.GetValue<string>());
    }

    // 19. HEAD 200 nonce'siz (yoki 12 bayt bo'lmagan nonce bilan) — qayta PUT
    // qilinmaydi (server dedup tufayli bizning nonce saqlanmasdi) va yozuv
    // noto'g'ri nonce bilan ketmaydi.
    [Theory]
    [InlineData(null)]
    [InlineData("AAAAAAAAAAAAAAAAAAAAAA==")] // 16 bayt
    public async Task Test19_Head_200_without_a_valid_nonce_holds_the_row_and_never_puts(string? nonceHeader)
    {
        var s = CreateRunner();
        InsertDeletedRow(s.DbPath, "111", 8);
        DownloadedMedia(s.Cache, "111", 8);

        s.Server.Override = req =>
        {
            if (req.Method != HttpMethod.Head) return null;
            var ok = new HttpResponseMessage(HttpStatusCode.OK);
            if (nonceHeader is not null) ok.Headers.Add("X-Nonce", nonceHeader);
            return ok;
        };

        await s.Runner.PushCycleAsync();

        Assert.Equal(0, s.Server.Count("PUT"));
        Assert.Null(PushedFor(s.Server, 8));
        Assert.Single(s.Cache.GetOutboxRows());
    }

    // 20. Yuklab olingandan keyin o'zgargan fayl boshqa fayl — u o'chirilgan
    // xabarga ulanmasligi kerak.
    [Fact]
    public async Task Test20_Changed_file_is_not_uploaded_and_the_record_goes_without_media()
    {
        var s = CreateRunner();
        InsertDeletedRow(s.DbPath, "111", 9);
        var media = DownloadedMedia(s.Cache, "111", 9);
        File.WriteAllBytes(media.Path, Encoding.UTF8.GetBytes("boshqa fayl"));

        await s.Runner.PushCycleAsync();

        Assert.Equal(0, s.Server.Count("HEAD"));
        Assert.Equal(0, s.Server.Count("PUT"));
        var pushed = PushedFor(s.Server, 9);
        Assert.NotNull(pushed);
        Assert.True(pushed!["media"] is null || pushed["media"]!.AsArray().Count == 0);
        Assert.Equal("failed", s.Cache.GetCapturedMedia("111", 9)!.Status);
    }

    // 21. O'qib bo'lmaydigan fayl butun sync'ni to'xtatmasligi va matnni
    // ushlab qolmasligi kerak; log'da yo'l ham, fayl nomi ham bo'lmasin.
    [Fact]
    public async Task Test21_Unreadable_file_does_not_stall_other_rows_or_leak_the_path()
    {
        if (!OperatingSystem.IsWindows()) return; // eksklyuziv qulf faqat Windows'da majburiy

        var logger = new RenderingLogger<CaptureSyncRunner>();
        var s = CreateRunner(logger);
        InsertDeletedRow(s.DbPath, "111", 10);
        var media = DownloadedMedia(s.Cache, "111", 10, marker: "SIRLI-HUJJAT");
        InsertDeletedRow(s.DbPath, "222", 11); // mediasiz qator

        using (new FileStream(media.Path, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            await s.Runner.PushCycleAsync();
        }

        Assert.NotNull(PushedFor(s.Server, 11));
        var pushed = PushedFor(s.Server, 10);
        Assert.NotNull(pushed);
        Assert.True(pushed!["media"] is null || pushed["media"]!.AsArray().Count == 0);
        Assert.Equal("failed", s.Cache.GetCapturedMedia("111", 10)!.Status);
        Assert.DoesNotContain("SIRLI-HUJJAT", logger.AllText);
    }

    // 22. PUT'dan keyin, push'dan oldin yiqilish: keyingi siklda ikkinchi PUT
    // yo'q, yozuv server saqlagan nonce bilan ketadi (K4).
    [Fact]
    public async Task Test22_Crash_after_put_before_push_does_not_upload_twice()
    {
        var s = CreateRunner();
        InsertDeletedRow(s.DbPath, "111", 12);
        var media = DownloadedMedia(s.Cache, "111", 12);

        s.Server.FailNextPush = true;
        try { await s.Runner.PushCycleAsync(); } catch (HttpRequestException) { }

        ForceRowDue(s.DbPath);
        await s.Runner.PushCycleAsync();

        Assert.Equal(1, s.Server.Count("PUT"));
        var pushed = PushedFor(s.Server, 12);
        Assert.NotNull(pushed);
        var nonce = pushed!["media"]!.AsArray()[0]!["nonce"]!.GetValue<string>();
        Assert.Equal(s.Server.Blobs[media.Sha].Nonce, nonce);
    }

    // 23. Media kaliti vektorga mos, sim formati = ciphertext ‖ tag.
    [Fact]
    public void Test23_Media_key_matches_the_vector_and_the_wire_format_round_trips()
    {
        var hkdf = TestVectors.Get("hkdf");
        var master = Convert.FromHexString(hkdf.GetProperty("master_key_hex").GetString()!);
        var expected = hkdf.GetProperty("derived").GetProperty("customsync-media-v1").GetString()!;

        var key = SyncCrypto.DeriveMediaKey(master);
        Assert.Equal(expected, Convert.ToHexString(key).ToLowerInvariant());

        var plain = Encoding.UTF8.GetBytes("media mazmuni");
        var (wire, nonce) = SyncCrypto.EncryptMedia(key, plain);
        Assert.Equal(12, nonce.Length);
        Assert.Equal(plain.Length + 16, wire.Length);

        var back = new byte[plain.Length];
        using var gcm = new AesGcm(key, 16);
        gcm.Decrypt(nonce, wire[..^16], wire[^16..], back);
        Assert.Equal(plain, back);
    }

    // 24. Rasm: sig'adigan eng katta o'lcham tanlanadi.
    [Fact]
    public void Test24_Photo_picks_the_largest_size_that_fits()
    {
        var content = JsonDocument.Parse("""
        { "@type": "messagePhoto", "photo": { "sizes": [
            { "type": "s", "photo": { "@type": "file", "id": 1, "size": 100, "expected_size": 100 } },
            { "type": "x", "photo": { "@type": "file", "id": 2, "size": 5000, "expected_size": 5000 } },
            { "type": "y", "photo": { "@type": "file", "id": 3, "size": 20000, "expected_size": 20000 } }
        ] } }
        """).RootElement;

        Assert.True(MediaExtractor.TryGetMediaInfo(content, 10000, out var type, out var size, out _));
        Assert.Equal("messagePhoto", type);
        Assert.Equal(5000, size);
        Assert.False(MediaExtractor.TryGetMediaInfo(content, 50, out _, out _, out _));
    }

    // 25. O'chirilgan xabar (haqiqiy TdClient TDLib 404 ni istisnoga
    // aylantiradi) — darhol `failed`, qayta urinishlarsiz.
    [Fact]
    public async Task Test25_Deleted_message_fails_at_once_through_the_real_client()
    {
        var dbPath = TempPath(".db");
        var cache = new MessageCache(dbPath);
        cache.Initialize();
        cache.QueueCapturedMedia("111", 13, 555, 13L << 20, "messageDocument", 100);

        var transport = new QueueTransport
        {
            Reply = req => new JsonObject { ["@type"] = "error", ["code"] = 404, ["message"] = "Not Found" }
        };
        using var client = new TdClient(transport);
        var config = new MediaCaptureConfig { Enabled = true, PeerIds = new HashSet<string> { "111" }, MaxBytes = 10485760, MaxAttempts = 5 };
        var downloader = new MediaDownloader(client, cache, config);

        await downloader.ProcessPendingOnceAsync();

        var row = cache.GetCapturedMedia("111", 13)!;
        Assert.Equal("failed", row.Status);
    }

    // 26. Yuklovchi xato bo'lganda ham yo'lni (fayl nomini) log'ga chiqarmaydi.
    [Fact]
    public async Task Test26_Downloader_never_logs_the_local_path()
    {
        if (!OperatingSystem.IsWindows()) return;

        var dbPath = TempPath(".db");
        var cache = new MessageCache(dbPath);
        cache.Initialize();
        cache.QueueCapturedMedia("111", 14, 555, 14L << 20, "messageDocument", 10);

        var filePath = TempPath("-YASHIRIN-NOM.bin");
        File.WriteAllBytes(filePath, new byte[10]);

        var transport = new QueueTransport
        {
            Reply = req => req["@type"]!.GetValue<string>() == "getMessage"
                ? JsonNode.Parse($$"""
                  { "@type": "message", "id": {{14L << 20}}, "chat_id": 555, "content": { "@type": "messageDocument",
                    "document": { "@type": "document", "document": { "@type": "file", "id": 9, "size": 10, "expected_size": 10,
                      "local": { "@type": "localFile", "path": {{JsonSerializer.Serialize(filePath)}}, "is_downloading_completed": true } } } } }
                  """)!.AsObject()
                : null
        };
        using var client = new TdClient(transport);
        var logger = new RenderingLogger<MediaDownloader>();
        var config = new MediaCaptureConfig { Enabled = true, PeerIds = new HashSet<string> { "111" }, MaxBytes = 10485760, MaxAttempts = 5 };
        var downloader = new MediaDownloader(client, cache, config, logger: logger);

        using (new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            await downloader.ProcessPendingOnceAsync();
        }

        Assert.DoesNotContain("YASHIRIN-NOM", logger.AllText);
    }

    // 27. Ishlab chiqarish yo'li: sozlama IConfiguration'dan, haqiqiy ro'yxat,
    // haqiqiy TdClient. Delegate testlari handler'ni AllowAll scope va qo'lda
    // yasalgan config bilan yaratardi, ya'ni ShouldAntiDelete hech qachon
    // false bo'lmasdi va ro'yxatdagi uzilish ko'rinmasdi.
    [Theory]
    [InlineData("allowed", true)]
    [InlineData("blocked_by_scope", false)]
    [InlineData("sticker", false)]
    [InlineData("not_in_peer_ids", false)]
    public async Task Test27_Queueing_follows_every_condition_through_the_real_registration(string variant, bool expectRow)
    {
        var dir = TempPath("-dir");
        Directory.CreateDirectory(dir);
        var settings = new Dictionary<string, string?>
        {
            ["Telegram:ApiId"] = "12345",
            ["Telegram:ApiHash"] = "test_hash",
            ["Telegram:DatabaseDirectory"] = dir,
            ["Telegram:FilesDirectory"] = dir,
            ["Capture:CacheDatabasePath"] = Path.Combine(dir, "cache.db"),
            ["Capture:Scope:DefaultEnabled"] = "true",
            ["Capture:Media:Enabled"] = "true",
            ["Capture:Media:PeerIds:0"] = variant == "not_in_peer_ids" ? "999" : "111",
        };
        if (variant == "blocked_by_scope") settings["Capture:Scope:Block:0"] = "111";
        var config = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();

        // Handler akkaunt ID'si ma'lum bo'lmaguncha update'larni buferda tutadi;
        // production'dagi kabi: ready -> getMe -> keyin xabar.
        var transport = new QueueTransport
        {
            Reply = req => req["@type"]!.GetValue<string>() == "getMe"
                ? new JsonObject { ["@type"] = "user", ["id"] = 42 }
                : null
        };
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(config);
        services.AddTdlibClient(config);
        services.AddMessageCache(config);
        services.AddCaptureHandlers();
        services.AddCaptureSyncClient(config);
        services.RemoveAll<ITdTransport>();
        services.AddSingleton<ITdTransport>(transport);

        using var provider = services.BuildServiceProvider();
        var cache = provider.GetRequiredService<MessageCache>();
        cache.Initialize();
        _ = provider.GetRequiredService<ITdClient>();

        transport.PushUpdate("""{ "@type": "updateAuthorizationState", "authorization_state": { "@type": "authorizationStateReady" } }""");

        var content = variant == "sticker"
            ? """{ "@type": "messageSticker", "sticker": { "@type": "sticker", "sticker": { "@type": "file", "id": 5, "size": 1000, "expected_size": 1000 } } }"""
            : """{ "@type": "messageDocument", "document": { "@type": "document", "document": { "@type": "file", "id": 5, "size": 1000, "expected_size": 1000 } } }""";
        transport.PushUpdate($$"""
        { "@type": "updateNewMessage", "message": { "@type": "message", "id": {{21L << 20}}, "chat_id": 111, "date": 1700000000,
          "sender_id": { "@type": "messageSenderUser", "user_id": 111 }, "is_outgoing": false, "content": {{content}} } }
        """);

        CapturedMediaRow? row = null;
        for (var i = 0; i < 40 && row is null; i++)
        {
            await Task.Delay(50);
            row = cache.GetCapturedMedia("111", 21);
        }
        if (!expectRow) await Task.Delay(300);
        row = cache.GetCapturedMedia("111", 21);

        if (expectRow) Assert.NotNull(row);
        else Assert.Null(row);
    }

    // 28. Servis to'xtatilganda (restart, deploy) yuklash o'rtasida qolgan
    // qator urinish hisoblanmaydi va xato sifatida log qilinmaydi. Aks holda
    // har restart bitta urinishni yeydi va MaxAttempts ta restartdan keyin
    // media abadiy `failed` bo'lib qoladi.
    [Fact]
    public async Task Test28_Shutdown_during_download_does_not_burn_an_attempt()
    {
        var dbPath = TempPath(".db");
        var cache = new MessageCache(dbPath);
        cache.Initialize();
        cache.QueueCapturedMedia("111", 15, 555, 15L << 20, "messageDocument", 10);

        // Javob yo'q — so'rov uzoq yuklash kabi osilib turadi.
        var transport = new QueueTransport { Reply = _ => null };
        using var client = new TdClient(transport);
        var logger = new RenderingLogger<MediaDownloader>();
        var config = new MediaCaptureConfig { Enabled = true, PeerIds = new HashSet<string> { "111" }, MaxBytes = 10485760, MaxAttempts = 5 };
        var downloader = new MediaDownloader(client, cache, config, logger: logger);

        using var cts = new CancellationTokenSource();
        var run = downloader.ProcessPendingOnceAsync(cts.Token);
        for (var i = 0; i < 80 && transport.Sent.IsEmpty; i++) await Task.Delay(25);
        Assert.False(transport.Sent.IsEmpty);
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);

        var row = cache.GetCapturedMedia("111", 15)!;
        Assert.Equal("pending", row.Status);
        Assert.Equal(0, row.Attempts);
        Assert.Null(row.NextAttemptAt);
        Assert.DoesNotContain("Error", logger.AllText);
    }

    // 29. Har bir rasm bo'lmagan media turi uchun MaxBytes chegarasi: aynan
    // MaxBytes — navbatga, bir bayt ortiq — yo'q. Delegate testi faqat rasmni
    // tekshirardi, shuning uchun bu turlarda chegara olib tashlansa ham suite
    // yashil qolardi.
    [Theory]
    [InlineData("messageVideo", "video", "video")]
    [InlineData("messageDocument", "document", "document")]
    [InlineData("messageAudio", "audio", "audio")]
    [InlineData("messageVoiceNote", "voice_note", "voice")]
    [InlineData("messageVideoNote", "video_note", "video")]
    [InlineData("messageAnimation", "animation", "animation")]
    public void Test29_Every_non_photo_type_respects_MaxBytes(string type, string outer, string inner)
    {
        JsonElement Content(long size) => JsonDocument.Parse($$"""
            { "@type": "{{type}}", "{{outer}}": { "{{inner}}": { "@type": "file", "id": 7, "size": {{size}}, "expected_size": {{size}} } } }
            """).RootElement;

        Assert.True(MediaExtractor.TryGetMediaInfo(Content(1000), 1000, out var t, out var size, out _));
        Assert.Equal(type, t);
        Assert.Equal(1000, size);
        Assert.False(MediaExtractor.TryGetMediaInfo(Content(1001), 1000, out _, out _, out _));
    }

    // 30. PUT yo'li: MediaRef.size — ochiq matn hajmi, blob = shifr ‖ 16 bayt
    // tag, va yozuvdagi nonce bilan media kaliti uni aynan asl baytlarga ochadi.
    [Fact]
    public async Task Test30_Put_path_reports_plaintext_size_and_a_blob_the_record_nonce_opens()
    {
        var s = CreateRunner();
        InsertDeletedRow(s.DbPath, "111", 16);
        var media = DownloadedMedia(s.Cache, "111", 16);

        await s.Runner.PushCycleAsync();

        var pushed = PushedFor(s.Server, 16);
        Assert.NotNull(pushed);
        var mediaRef = pushed!["media"]!.AsArray().Single()!;
        Assert.Equal(media.Bytes.Length, mediaRef["size"]!.GetValue<long>());

        var blob = s.Server.Blobs[media.Sha];
        Assert.Equal(media.Bytes.Length + 16, blob.Body.Length);
        var nonce = Convert.FromBase64String(mediaRef["nonce"]!.GetValue<string>());
        Assert.Equal(blob.Nonce, Convert.ToBase64String(nonce));

        var plain = new byte[media.Bytes.Length];
        using var aes = new AesGcm(SyncCrypto.DeriveMediaKey(s.MasterKey), 16);
        aes.Decrypt(nonce, blob.Body.AsSpan(0, plain.Length), blob.Body.AsSpan(plain.Length), plain);
        Assert.Equal(media.Bytes, plain);
    }

    // 31. TDLib e'lon qilgan hajm kichik, diskdagi tayyor fayl esa MaxBytes dan
    // katta — media o'tkazib yuboriladi, `downloaded` bo'lmaydi.
    [Fact]
    public async Task Test31_Downloaded_file_larger_than_MaxBytes_is_skipped()
    {
        var dbPath = TempPath(".db");
        var cache = new MessageCache(dbPath);
        cache.Initialize();
        cache.QueueCapturedMedia("111", 17, 555, 17L << 20, "messageDocument", 10);

        var filePath = TempPath(".bin");
        File.WriteAllBytes(filePath, new byte[200]);

        var transport = new QueueTransport
        {
            Reply = req => req["@type"]!.GetValue<string>() == "getMessage"
                ? JsonNode.Parse($$"""
                  { "@type": "message", "id": {{17L << 20}}, "chat_id": 555, "content": { "@type": "messageDocument",
                    "document": { "@type": "document", "document": { "@type": "file", "id": 9, "size": 10, "expected_size": 10,
                      "local": { "@type": "localFile", "path": {{JsonSerializer.Serialize(filePath)}}, "is_downloading_completed": true } } } } }
                  """)!.AsObject()
                : null
        };
        using var client = new TdClient(transport);
        var config = new MediaCaptureConfig { Enabled = true, PeerIds = new HashSet<string> { "111" }, MaxBytes = 100, MaxAttempts = 5 };
        var downloader = new MediaDownloader(client, cache, config);

        await downloader.ProcessPendingOnceAsync();

        var row = cache.GetCapturedMedia("111", 17)!;
        Assert.Equal("skipped", row.Status);
        Assert.Null(row.Sha256);
    }

    private static void ForceRowDue(string dbPath)
    {
        using var conn = new SqliteConnection($"Data Source={dbPath}");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE capture_outbox SET next_retry_at = NULL;";
        cmd.ExecuteNonQuery();
    }
}
