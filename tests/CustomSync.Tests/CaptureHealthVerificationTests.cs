using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CustomSync.Capture.Capture;
using CustomSync.Capture.Maintenance;
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
/// Plan 05 Task 9b tekshiruvida qo'shilgan testlar (2026-10-02). Health
/// hisobot sync runner'ning token oqimiga maintenance oqimidan kiradi —
/// 9b gacha uni faqat sync sikli chaqirardi, shuning uchun ikki oqimning
/// bir vaqtdagi refresh'i hech qachon sinalmagan edi.
/// </summary>
public class CaptureHealthVerificationTests : IDisposable
{
    private readonly List<string> _paths = new();

    private string TempPath(string suffix)
    {
        var p = Path.Combine(Path.GetTempPath(), $"cs-t9bv-{Guid.NewGuid():N}{suffix}");
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
            lock (_entries) _entries.Add((logLevel, formatter(state, exception) + (exception is null ? "" : " | " + exception)));
        }
        public List<(LogLevel Level, string Text)> Entries { get { lock (_entries) return _entries.ToList(); } }
        public string AllText => string.Join("\n", Entries.Select(e => e.Text));
    }

    private sealed class QueueTransport : ITdTransport
    {
        private readonly BlockingCollection<string> _incoming = new();
        public Func<JsonObject, JsonObject?>? Reply { get; set; }

        public int CreateClientId() => 1;

        public void Send(int clientId, string requestJson)
        {
            var req = JsonNode.Parse(requestJson)!.AsObject();
            var resp = Reply?.Invoke(req);
            if (resp is not null)
            {
                resp["@extra"] = req["@extra"]?.GetValue<string>();
                _incoming.Add(resp.ToJsonString());
            }
        }

        public string? Receive(double timeoutSeconds)
            => _incoming.TryTake(out var item, TimeSpan.FromSeconds(Math.Min(timeoutSeconds, 0.05))) ? item : null;

        public string? Execute(string requestJson) => null;
        public void Dispose() { }
    }

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

    /// <summary>
    /// Haqiqiy server kabi: refresh token bir marta ishlaydi va har
    /// muvaffaqiyatli refresh'da almashadi (DeviceService.RefreshAsync).
    /// Javoblar snake_case — API'ning JSON sozlamasi shunday.
    /// </summary>
    private sealed class FakeServer : HttpMessageHandler
    {
        private readonly object _lock = new();
        private string _currentRefresh = "refresh-0";
        private int _issued;
        private int _refreshCount;

        public readonly ConcurrentQueue<(string Path, string? Auth, string? Body)> Requests = new();
        public TaskCompletionSource? HoldFirstRefresh { get; init; }
        public readonly TaskCompletionSource FirstRefreshArrived = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource SecondRefreshArrived = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public HttpStatusCode HealthStatus { get; init; } = HttpStatusCode.NoContent;

        public int RefreshCount => Volatile.Read(ref _refreshCount);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var path = request.RequestUri!.AbsolutePath;
            var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);
            Requests.Enqueue((path, request.Headers.Authorization?.ToString(), body));

            if (path == "/api/v1/devices/refresh")
            {
                var n = Interlocked.Increment(ref _refreshCount);
                if (n == 1)
                {
                    FirstRefreshArrived.TrySetResult();
                    if (HoldFirstRefresh is not null) await HoldFirstRefresh.Task;
                }
                else if (n == 2)
                {
                    SecondRefreshArrived.TrySetResult();
                }

                var presented = JsonDocument.Parse(body!).RootElement.GetProperty("refresh_token").GetString();
                lock (_lock)
                {
                    if (presented != _currentRefresh)
                        return new HttpResponseMessage(HttpStatusCode.Unauthorized);

                    _issued++;
                    _currentRefresh = $"refresh-{_issued}";
                    var json = JsonSerializer.Serialize(new
                    {
                        refresh_token = _currentRefresh,
                        access_token = $"access-{_issued}",
                        expires_at = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds()
                    });
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent(json, Encoding.UTF8, "application/json")
                    };
                }
            }

            if (path == "/api/v1/devices/health")
                return new HttpResponseMessage(HealthStatus) { Content = new StringContent("{\"error\":\"x\"}") };

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }
    }

    private sealed class RecordingReporter : ICaptureHealthReporter
    {
        public readonly ConcurrentQueue<StorageSnapshot> Reported = new();
        public Task<bool> ReportAsync(StorageSnapshot snapshot, CancellationToken ct = default)
        {
            Reported.Enqueue(snapshot);
            return Task.FromResult(true);
        }
    }

    /// <summary>5-qadamdagi oxirgi o'lchov paytida to'xtatishni so'raydi.</summary>
    private sealed class CancellingProbe(CancellationTokenSource cts) : IDiskSpaceProbe
    {
        public long? GetAvailableFreeBytes(string path)
        {
            cts.Cancel();
            return 100L * 1024 * 1024 * 1024;
        }
    }

    private MessageCache NewCache()
    {
        var cache = new MessageCache(TempPath(".db"));
        cache.Initialize();
        return cache;
    }

    /// <summary>Sync yoqilgan sozlamalar va haqiqiy shakldagi kalit/holat fayllari.</summary>
    private (Dictionary<string, string?> Settings, string KeyPath) SyncSettings()
    {
        var keyPath = TempPath(".key");
        File.WriteAllText(keyPath, new string('a', 64));
        var statePath = TempPath(".state.json");
        File.WriteAllText(statePath, JsonSerializer.Serialize(new { device_id = "dev-1", refresh_token = "refresh-0" }));

        var settings = new Dictionary<string, string?>
        {
            ["Capture:Sync:Enabled"] = "true",
            ["Capture:Sync:ServerUrl"] = "http://customsync.test",
            ["Capture:Sync:MasterKeyPath"] = keyPath,
            ["Capture:Sync:StatePath"] = statePath,
            ["Capture:CacheDatabasePath"] = TempPath(".db"),
        };
        return (settings, keyPath);
    }

    private static IConfiguration Build(Dictionary<string, string?> settings)
        => new ConfigurationBuilder().AddInMemoryCollection(settings).Build();

    private static StorageSnapshot Snapshot() => new(
        TakenAt: DateTimeOffset.UtcNow, ProcessRssBytes: 1, MemoryLimitBytes: null, CacheDatabaseBytes: 2,
        MediaStoreBytes: 3, MediaStoreFiles: 4, TdlibFilesBytes: null, TdlibDatabaseBytes: null, FreeDiskBytes: 5,
        OptimizeFreedBytes: 0, OptimizeDeletedFiles: 0, MediaRowsPruned: 0, MediaFilesDeleted: 0);

    // 1. Production yo'li: haqiqiy ro'yxatlar (AddCaptureHandlers +
    // AddCaptureSyncClient) bilan qurilgan StorageMaintenance bitta yurishda
    // aynan o'sha yurishning snapshot'ini haqiqiy reporter orqali, refresh
    // qilingan token bilan yuboradi. Delegate testlari maintenance'ni
    // qo'lda, reporter berib yaratardi — ro'yxatdagi ulanish sinalmagan.
    [Fact]
    public async Task Test01_Registered_maintenance_reports_its_snapshot_through_the_real_reporter()
    {
        var dir = TempDir();
        var (settings, _) = SyncSettings();
        settings["Telegram:ApiId"] = "12345";
        settings["Telegram:ApiHash"] = "test_hash";
        settings["Telegram:DatabaseDirectory"] = dir;
        settings["Telegram:FilesDirectory"] = dir;
        settings["Capture:Media:StorageDirectory"] = Path.Combine(dir, "media");
        var config = Build(settings);

        var server = new FakeServer();
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(config);
        services.AddTdlibClient(config);
        services.AddMessageCache(config);
        services.AddCaptureHandlers();
        services.AddCaptureSyncClient(config);
        services.AddSingleton<HttpMessageHandler>(server);
        services.RemoveAll<ITdTransport>();
        services.AddSingleton<ITdTransport>(new QueueTransport { Reply = StorageReplies });
        services.RemoveAll<IDiskSpaceProbe>();
        services.AddSingleton<IDiskSpaceProbe>(new FixedDiskSpaceProbe(777));

        using var provider = services.BuildServiceProvider();
        provider.GetRequiredService<MessageCache>().Initialize();
        var maintenance = provider.GetRequiredService<StorageMaintenance>();

        await maintenance.RunOnceAsync();

        var report = Assert.Single(server.Requests, r => r.Path == "/api/v1/devices/health");
        Assert.Equal("Bearer access-1", report.Auth);
        var snapshot = maintenance.LatestSnapshot!;
        using var body = JsonDocument.Parse(report.Body!);
        Assert.Equal(snapshot.ProcessRssBytes, body.RootElement.GetProperty("rss_bytes").GetInt64());
        Assert.Equal(snapshot.CacheDatabaseBytes, body.RootElement.GetProperty("cache_db_bytes").GetInt64());
        Assert.Equal(777, body.RootElement.GetProperty("free_disk_bytes").GetInt64());
    }

    // 2. Ikki oqim (production'da: sync sikli va maintenance'ning 6-qadami)
    // tokenni bir vaqtda so'raydi. Refresh token har ishlatilganda
    // almashadi: ikkalasi bir xil eski tokenni yuborsa ikkinchisi 401
    // oladi va runner "qurilma bekor qilingan" deb sync'ni to'xtatadi
    // (yoki xotirada server bilmaydigan token qolib, qurilmani qayta
    // enroll qilishga to'g'ri keladi). Refresh bitta bo'lishi shart.
    [Fact]
    public async Task Test02_Concurrent_callers_refresh_the_rotating_token_only_once()
    {
        var (settings, _) = SyncSettings();
        var server = new FakeServer { HoldFirstRefresh = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously) };
        var runner = new CaptureSyncRunner(NewCache(), new CaptureSyncHttpClient(new HttpClient(server)), Build(settings));

        var first = Task.Run(() => runner.ReportHealthAsync(Snapshot()));
        await server.FirstRefreshArrived.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var second = Task.Run(() => runner.ReportHealthAsync(Snapshot()));
        // Qulfsiz ikkinchi chaqiruv shu oraliqda o'z refresh'ini yuborardi.
        await Task.WhenAny(server.SecondRefreshArrived.Task, Task.Delay(500));
        server.HoldFirstRefresh!.SetResult();

        Assert.True(await first.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.True(await second.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.False(runner.IsStopped);
        Assert.Equal(1, server.RefreshCount);
        Assert.Equal(2, server.Requests.Count(r => r.Path == "/api/v1/devices/health"));
    }

    // 3. Kalit faylini o'qib bo'lmasa (I/O xatosi) hisobot false qaytaradi —
    // istisno chiqmaydi (shartnoma: faqat bekor qilish tashqariga chiqadi).
    // Avval credentials yuklash try'dan tashqarida edi.
    [Fact]
    public async Task Test03_Unreadable_credentials_make_the_report_return_false_without_throwing()
    {
        var (settings, keyPath) = SyncSettings();
        var server = new FakeServer();
        var runner = new CaptureSyncRunner(NewCache(), new CaptureSyncHttpClient(new HttpClient(server)), Build(settings));

        bool result;
        using (new FileStream(keyPath, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            result = await runner.ReportHealthAsync(Snapshot());
        }

        Assert.False(result);
        Assert.Empty(server.Requests);
    }

    // 4. Server hisobotni rad etsa (400/404/500) capture buni ko'rsatishi
    // kerak: avval hech narsa loglanmasdi, ya'ni doim rad etilayotgan
    // hisobot (masalan, eski server) butunlay ko'rinmas edi. Log'da faqat
    // status kodi — URL, token va javob tanasi yo'q.
    [Theory]
    [InlineData(400)]
    [InlineData(404)]
    [InlineData(500)]
    public async Task Test04_A_rejected_report_is_logged_with_its_status_code_only(int status)
    {
        var (settings, _) = SyncSettings();
        var server = new FakeServer { HealthStatus = (HttpStatusCode)status };
        var logger = new LevelLogger<CaptureSyncHttpClient>();
        var runner = new CaptureSyncRunner(NewCache(), new CaptureSyncHttpClient(new HttpClient(server), logger), Build(settings));

        Assert.False(await runner.ReportHealthAsync(Snapshot()));

        var warning = Assert.Single(logger.Entries, e => e.Level == LogLevel.Warning);
        Assert.Contains(status.ToString(), warning.Text);
        Assert.DoesNotContain("customsync.test", logger.AllText);
        Assert.DoesNotContain("access-", logger.AllText);
        Assert.DoesNotContain("\"error\"", logger.AllText);
    }

    // 5. To'xtatish 5-qadam paytida so'ralsa 6-qadam hisobot yubormaydi va
    // ogohlantirish yozmaydi; sikl jim tugaydi.
    [Fact]
    public async Task Test05_A_shutdown_requested_before_step_6_sends_no_report()
    {
        var transport = new QueueTransport { Reply = StorageReplies };
        using var client = new TdClient(transport);
        using var cts = new CancellationTokenSource();
        var reporter = new RecordingReporter();
        var logger = new LevelLogger<StorageMaintenance>();
        var maintenance = new StorageMaintenance(NewCache(), new MediaStore(TempDir()), client,
            new CancellingProbe(cts), new MediaCaptureConfig(), logger: logger, reporter: reporter);

        await maintenance.RunLoopAsync(cts.Token).WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Empty(reporter.Reported);
        Assert.DoesNotContain(logger.Entries, e => e.Level >= LogLevel.Warning);
    }
}
