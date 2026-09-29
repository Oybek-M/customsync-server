using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CustomSync.Capture.Capture;
using CustomSync.Capture.Sync;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace CustomSync.Tests;

/// <summary>
/// Task 6a tekshiruvida topilgan bo'shliqlar: serverning haqiqiy token
/// javobi, rotatsiya qilingan refresh token'ning saqlanishi, zaharlangan
/// qatorlar, noma'lum push holati va eski bazaning migratsiyasi.
/// </summary>
public class CaptureSyncVerificationTests : IDisposable
{
    private readonly List<string> _tempFiles = new();

    private string Temp(string ext)
    {
        var path = Path.Combine(Path.GetTempPath(), $"sync_verify_{Guid.NewGuid():N}{ext}");
        _tempFiles.Add(path);
        return path;
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        foreach (var file in _tempFiles)
            foreach (var f in new[] { file, file + "-wal", file + "-shm" })
                try { if (File.Exists(f)) File.Delete(f); } catch { }
    }

    private sealed class Handler(Func<HttpRequestMessage, string, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public int PushCalls;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);
            if (request.RequestUri!.AbsolutePath.EndsWith("/sync/push")) PushCalls++;
            return respond(request, body);
        }
    }

    private static readonly JsonSerializerOptions ServerJson = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    /// <summary>`DeviceEndpoints` /refresh javobining aynan o'zi: `expiresAt` — `DateTime`.</summary>
    private static HttpResponseMessage ServerRefreshResponse(string refreshToken, object? expiresAt = null)
        => Json(new
        {
            refreshToken,
            accessToken = "at-1",
            expiresAt = expiresAt ?? DateTime.UtcNow.AddHours(1),
        });

    private static HttpResponseMessage Json(object body)
        => new(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(body, ServerJson), Encoding.UTF8, "application/json")
        };

    /// <summary>Har bir yuborilgan yozuvga `status` bilan javob beradi.</summary>
    private static HttpResponseMessage PushResults(string body, string status)
    {
        var records = JsonNode.Parse(body)!["records"]!.AsArray();
        return Json(new
        {
            results = records.Select(r => new { recordId = r!["record_id"]!.GetValue<string>(), status }).ToArray()
        });
    }

    private (string Db, string Key, string State) Files()
    {
        var key = Temp(".key");
        var state = Temp(".json");
        DeviceCredentials.SaveMasterKey(key, new byte[32]);
        DeviceCredentials.SaveDeviceState(state, new DeviceState("dev-1", "rt-old"));
        return (Temp(".db"), key, state);
    }

    private static IConfiguration Config(string key, string state, string batch = "500") =>
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Capture:Sync:Enabled"] = "true",
            ["Capture:Sync:ServerUrl"] = "https://sync.example",
            ["Capture:Sync:MasterKeyPath"] = key,
            ["Capture:Sync:StatePath"] = state,
            ["Capture:Sync:PushBatchSize"] = batch,
        }).Build();

    private static void InsertRow(string db, long msgId, string payloadAccount = "111")
    {
        using var conn = new SqliteConnection($"Data Source={db}");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            INSERT INTO capture_outbox (kind, account_id, peer_id, msg_id, occurred_at, observed_at, payload_json, created_at)
            VALUES ('deleted', '111', '222', @m, 100, 100, @p, 100);";
        cmd.Parameters.AddWithValue("@m", msgId);
        cmd.Parameters.AddWithValue("@p", $"{{\"account_id\":\"{payloadAccount}\",\"peer_id\":\"222\",\"text\":\"t\"}}");
        cmd.ExecuteNonQuery();
    }

    private static CaptureSyncRunner Runner(MessageCache cache, Handler handler, IConfiguration config)
        => new(cache, new CaptureSyncHttpClient(new HttpClient(handler)), config);

    [Fact]
    public async Task S01_The_servers_real_refresh_response_is_understood_and_the_row_is_pushed()
    {
        // Server `expires_at` ni ISO sana sifatida yuboradi (JwtIssuer:
        // DateTime). Uni son deb o'qish har bir refresh'ni yiqitardi —
        // productionda birorta ham yozuv yuborilmasdi.
        var (db, key, state) = Files();
        var cache = new MessageCache(db);
        cache.Initialize();
        InsertRow(db, 1);

        var handler = new Handler((req, body) => req.RequestUri!.AbsolutePath.EndsWith("/devices/refresh")
            ? ServerRefreshResponse("rt-new")
            : PushResults(body, "created"));

        Assert.True(await Runner(cache, handler, Config(key, state)).PushCycleAsync());

        Assert.Equal(1, handler.PushCalls);
        Assert.Empty(cache.GetOutboxRows());
        Assert.Equal("rt-new", DeviceCredentials.LoadDeviceState(state, permissionChecker: _ => true)!.RefreshToken);
    }

    [Fact]
    public async Task S02_A_rotated_refresh_token_is_saved_even_if_the_rest_of_the_response_is_odd()
    {
        // Server tokenni allaqachon almashtirgan: yangi refresh token diskka
        // tushmasa, eskisi o'lik — qurilmani qayta enroll qilish kerak bo'ladi.
        var (db, key, state) = Files();
        var cache = new MessageCache(db);
        cache.Initialize();
        InsertRow(db, 1);

        var handler = new Handler((req, body) => req.RequestUri!.AbsolutePath.EndsWith("/devices/refresh")
            ? ServerRefreshResponse("rt-new", expiresAt: "not-a-date")
            : PushResults(body, "created"));

        await Runner(cache, handler, Config(key, state)).PushCycleAsync();

        Assert.Equal("rt-new", DeviceCredentials.LoadDeviceState(state, permissionChecker: _ => true)!.RefreshToken);
    }

    [Fact]
    public async Task S03_Poisoned_rows_are_kept_but_do_not_block_the_queue()
    {
        var (db, key, state) = Files();
        var cache = new MessageCache(db);
        cache.Initialize();
        InsertRow(db, 1, payloadAccount: "999");
        InsertRow(db, 2, payloadAccount: "999");
        InsertRow(db, 3);

        var handler = new Handler((req, body) => req.RequestUri!.AbsolutePath.EndsWith("/devices/refresh")
            ? ServerRefreshResponse("rt-new")
            : PushResults(body, "created"));
        var runner = Runner(cache, handler, Config(key, state, batch: "2"));

        for (int i = 0; i < 3; i++)
            await runner.PushCycleAsync();

        var left = cache.GetOutboxRows();
        Assert.Equal(new long[] { 1, 2 }, left.Select(r => r.MsgId).OrderBy(x => x).ToArray());
        Assert.All(left, r => Assert.NotNull(r.LastError));
        Assert.Equal(2, runner.PoisonCount);
    }

    [Fact]
    public async Task S04_An_unknown_push_status_is_retried_later_not_immediately()
    {
        var (db, key, state) = Files();
        var cache = new MessageCache(db);
        cache.Initialize();
        InsertRow(db, 1);

        var handler = new Handler((req, body) => req.RequestUri!.AbsolutePath.EndsWith("/devices/refresh")
            ? ServerRefreshResponse("rt-new")
            : PushResults(body, "queued"));

        await Runner(cache, handler, Config(key, state)).PushCycleAsync();

        var row = Assert.Single(cache.GetOutboxRows());
        Assert.NotNull(row.NextRetryAt);
        Assert.Equal(1, row.RetryCount);
    }

    private static bool IsRefresh(HttpRequestMessage req) => req.RequestUri!.AbsolutePath.EndsWith("/devices/refresh");

    [Fact]
    public async Task S06_A_valid_access_token_is_reused_instead_of_rotating_every_cycle()
    {
        // Har siklda refresh — har 30 s da rotatsiya va har safar "server
        // almashtirdi, disk hali yozilmadi" oynasi.
        var (db, key, state) = Files();
        var cache = new MessageCache(db);
        cache.Initialize();
        int refreshes = 0;
        var handler = new Handler((req, body) =>
        {
            if (!IsRefresh(req)) return PushResults(body, "created");
            refreshes++;
            return ServerRefreshResponse($"rt-{refreshes}");
        });
        var runner = Runner(cache, handler, Config(key, state));

        InsertRow(db, 1);
        await runner.PushCycleAsync();
        InsertRow(db, 2);
        await runner.PushCycleAsync();

        Assert.Equal(2, handler.PushCalls);
        Assert.Equal(1, refreshes);
    }

    private static readonly DateTimeOffset T0 = DateTimeOffset.FromUnixTimeSeconds(1787000000);

    [Fact]
    public async Task S07_A_row_that_keeps_failing_waits_longer_each_time()
    {
        var (db, key, state) = Files();
        var cache = new MessageCache(db);
        cache.Initialize();
        InsertRow(db, 1);
        var time = new TestTimeProvider(T0);
        var handler = new Handler((req, body) => IsRefresh(req) ? ServerRefreshResponse("rt-new") : PushResults(body, "error"));
        var runner = new CaptureSyncRunner(cache, new CaptureSyncHttpClient(new HttpClient(handler)), Config(key, state), time);

        await runner.PushCycleAsync();
        long first = Assert.Single(cache.GetOutboxRows()).NextRetryAt!.Value - T0.ToUnixTimeSeconds();

        time.Advance(TimeSpan.FromSeconds(first));
        await runner.PushCycleAsync();
        long second = Assert.Single(cache.GetOutboxRows()).NextRetryAt!.Value - time.GetUtcNow().ToUnixTimeSeconds();

        Assert.Equal(1, first);
        Assert.Equal(2, second);
    }

    [Fact]
    public async Task S08_A_record_missing_from_the_response_is_scheduled_like_an_error()
    {
        var (db, key, state) = Files();
        var cache = new MessageCache(db);
        cache.Initialize();
        InsertRow(db, 1);
        var handler = new Handler((req, body) => IsRefresh(req) ? ServerRefreshResponse("rt-new") : Json(new { results = Array.Empty<object>() }));

        await Runner(cache, handler, Config(key, state)).PushCycleAsync();

        var row = Assert.Single(cache.GetOutboxRows());
        Assert.Equal(1, row.RetryCount);
        Assert.NotNull(row.NextRetryAt);
    }

    [Fact]
    public async Task S09_The_loop_waits_for_the_growing_backoff_after_a_network_error()
    {
        var (db, key, state) = Files();
        var cache = new MessageCache(db);
        cache.Initialize();
        InsertRow(db, 1);
        var handler = new Handler((req, body) => IsRefresh(req)
            ? ServerRefreshResponse("rt-new")
            : throw new HttpRequestException("down"));
        var config = Config(key, state);
        var runner = Runner(cache, handler, config);
        var loop = new CaptureSyncLoop(runner, TimeProvider.System, config);

        Assert.Equal(TimeSpan.FromSeconds(30), loop.NextDelay(lastCycleSucceeded: true));
        Assert.False(await runner.PushCycleAsync());
        Assert.Equal(TimeSpan.FromSeconds(1), loop.NextDelay(lastCycleSucceeded: false));
        Assert.False(await runner.PushCycleAsync());
        Assert.Equal(TimeSpan.FromSeconds(2), loop.NextDelay(lastCycleSucceeded: false));
        Assert.Single(cache.GetOutboxRows());
    }

    private sealed class ScriptedPrompt(params string[] answers) : CustomSync.Capture.Tdlib.IConsolePrompt
    {
        private readonly Queue<string> _answers = new(answers);
        public string? Prompt(string message, bool isSecret = false) => _answers.Dequeue();
    }

    [Fact]
    public async Task S10_Enroll_registers_as_a_service_device_and_never_prints_the_code()
    {
        var state = Temp(".json");
        string? sent = null;
        var handler = new Handler((req, body) =>
        {
            sent = body;
            return Json(new { deviceId = "service-1", refreshToken = "rt-1", accessToken = "at-1", expiresAt = DateTime.UtcNow.AddHours(1) });
        });
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Capture:Sync:ServerUrl"] = "https://sync.example",
            ["Capture:Sync:StatePath"] = state,
        }).Build();
        var output = new StringWriter();

        int exit = await SyncCliCommands.EnrollAsync(config, new ScriptedPrompt("SECRET-CODE-42", ""), new HttpClient(handler), output);

        Assert.Equal(0, exit);
        var body = JsonNode.Parse(sent!)!;
        Assert.Equal("service", body["platform"]!.GetValue<string>());
        Assert.Equal("capture-vps", body["name"]!.GetValue<string>());
        Assert.Equal("SECRET-CODE-42", body["code"]!.GetValue<string>());
        Assert.DoesNotContain("SECRET-CODE-42", output.ToString());
        Assert.Equal("rt-1", DeviceCredentials.LoadDeviceState(state, permissionChecker: _ => true)!.RefreshToken);
    }

    [Fact]
    public void S11_BuildRecord_itself_refuses_a_row_whose_payload_names_another_account()
    {
        var row = new OutboxRow(1, "deleted", "111", "222", 5, 100, 100,
            "{\"account_id\":\"999\",\"peer_id\":\"222\"}", 100);

        Assert.Throws<InvalidOperationException>(() => SyncCrypto.BuildRecord(row, new byte[32], "dev-1"));
    }

    [Fact]
    public void S05_A_database_from_before_pending_edits_upgrades_completely()
    {
        // Task 4b davridagi v1 fayl: outbox'da retry ustunlari yo'q,
        // `pending_edits` jadvali ham yo'q. `PRAGMA journal_mode` qator
        // qaytargani uchun skriptdagi keyingi xato jimgina yutilardi va
        // qolgan jadvallar yaratilmay qolardi.
        var db = Temp(".db");
        using (var conn = new SqliteConnection($"Data Source={db}"))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                CREATE TABLE message_cache (chat_id INTEGER NOT NULL, message_id INTEGER NOT NULL, text TEXT, sender_id TEXT,
                    is_out INTEGER NOT NULL, is_media INTEGER NOT NULL, media_id TEXT, date INTEGER NOT NULL, cached_at INTEGER NOT NULL,
                    PRIMARY KEY (chat_id, message_id));
                CREATE TABLE capture_outbox (id INTEGER PRIMARY KEY AUTOINCREMENT, kind TEXT NOT NULL, account_id TEXT NOT NULL,
                    peer_id TEXT NOT NULL, msg_id INTEGER NOT NULL, occurred_at INTEGER NOT NULL, observed_at INTEGER NOT NULL,
                    payload_json TEXT NOT NULL, created_at INTEGER NOT NULL, UNIQUE (kind, account_id, peer_id, msg_id, occurred_at));
                CREATE TABLE activity_latest (peer_id TEXT NOT NULL, field TEXT NOT NULL, value TEXT NOT NULL,
                    observed_at INTEGER NOT NULL, PRIMARY KEY (peer_id, field));
                PRAGMA user_version = 1;";
            cmd.ExecuteNonQuery();
        }
        InsertRow(db, 7);

        var cache = new MessageCache(db);
        cache.Initialize();

        using (var conn = new SqliteConnection($"Data Source={db}"))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'pending_edits';";
            Assert.Equal(1L, Convert.ToInt64(cmd.ExecuteScalar()));
            cmd.CommandText = "SELECT COUNT(*) FROM pragma_index_list('pending_edits') WHERE name = 'idx_pending_edits_observed_at';";
            Assert.Equal(1L, Convert.ToInt64(cmd.ExecuteScalar()));
            cmd.CommandText = "PRAGMA user_version;";
            Assert.Equal(2L, Convert.ToInt64(cmd.ExecuteScalar()));
        }

        var row = Assert.Single(cache.GetEligibleOutboxRows(10, 200));
        Assert.Equal(7, row.MsgId);
    }
}
