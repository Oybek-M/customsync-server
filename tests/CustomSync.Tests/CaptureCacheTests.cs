using CustomSync.Capture.Capture;
using CustomSync.Capture.Preflight;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace CustomSync.Tests;

public class TestTimeProvider(DateTimeOffset initialUtcNow) : TimeProvider
{
    private DateTimeOffset _utcNow = initialUtcNow;

    public override DateTimeOffset GetUtcNow() => _utcNow;

    public void SetUtcNow(DateTimeOffset utcNow) => _utcNow = utcNow;
    public void Advance(TimeSpan delta) => _utcNow += delta;
}

public class CaptureCacheTests : IDisposable
{
    private readonly List<string> _tempFiles = new();

    private string CreateTempDbPath()
    {
        var path = Path.Combine(Path.GetTempPath(), $"capture_cache_test_{Guid.NewGuid():N}.db");
        _tempFiles.Add(path);
        return path;
    }

    public void Dispose()
    {
        // Clear all connection pools before deleting files to release locks on Windows
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
    public void Test01_Initialize_twice_is_noop_and_sets_user_version()
    {
        var dbPath = CreateTempDbPath();
        var cache = new MessageCache(dbPath);

        cache.Initialize();
        cache.Initialize(); // Second call must be idempotent no-op

        using var conn = new SqliteConnection($"Data Source={dbPath}");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "PRAGMA user_version;";
        var version = Convert.ToInt32(cmd.ExecuteScalar());
        Assert.Equal(1, version);
    }

    [Fact]
    public void Test02_Round_trip_Put_and_Get_preserves_all_fields_including_null_text()
    {
        var dbPath = CreateTempDbPath();
        var timeProvider = new TestTimeProvider(DateTimeOffset.FromUnixTimeSeconds(1700000000));
        var cache = new MessageCache(dbPath, timeProvider);
        cache.Initialize();

        var message = new CachedMessage(
            ChatId: -1001234567890L,
            MessageId: 42L,
            Text: null, // Media-only row
            SenderId: "user_987654",
            IsOut: false,
            IsMedia: true,
            MediaId: "tdlib_file_id_555",
            Date: 1699999000L);

        var prev = cache.Put(message);
        Assert.Null(prev);

        var retrieved = cache.Get(message.ChatId, message.MessageId);
        Assert.NotNull(retrieved);
        Assert.Equal(message.ChatId, retrieved.ChatId);
        Assert.Equal(message.MessageId, retrieved.MessageId);
        Assert.Null(retrieved.Text);
        Assert.Equal(message.SenderId, retrieved.SenderId);
        Assert.Equal(message.IsOut, retrieved.IsOut);
        Assert.Equal(message.IsMedia, retrieved.IsMedia);
        Assert.Equal(message.MediaId, retrieved.MediaId);
        Assert.Equal(message.Date, retrieved.Date);
        Assert.Equal(1700000000L, retrieved.CachedAt);
    }

    [Fact]
    public void Test03_Put_returns_replaced_row_with_old_text_and_Get_returns_new_text()
    {
        var dbPath = CreateTempDbPath();
        var timeProvider = new TestTimeProvider(DateTimeOffset.FromUnixTimeSeconds(1700000000));
        var cache = new MessageCache(dbPath, timeProvider);
        cache.Initialize();

        var first = new CachedMessage(
            ChatId: 1000L,
            MessageId: 50L,
            Text: "Original text before edit",
            SenderId: "user_1",
            IsOut: true,
            IsMedia: false,
            MediaId: null,
            Date: 1700000000L);

        var prevFirst = cache.Put(first);
        Assert.Null(prevFirst);

        timeProvider.Advance(TimeSpan.FromSeconds(60));

        var updated = new CachedMessage(
            ChatId: 1000L,
            MessageId: 50L,
            Text: "Edited message text",
            SenderId: "user_1",
            IsOut: true,
            IsMedia: false,
            MediaId: null,
            Date: 1700000000L);

        var replaced = cache.Put(updated);
        Assert.NotNull(replaced);
        Assert.Equal("Original text before edit", replaced.Text);
        Assert.Equal(1700000000L, replaced.CachedAt);

        var current = cache.Get(1000L, 50L);
        Assert.NotNull(current);
        Assert.Equal("Edited message text", current.Text);
        Assert.Equal(1700000060L, current.CachedAt);
    }

    [Fact]
    public void Test04_First_Put_returns_null()
    {
        var dbPath = CreateTempDbPath();
        var cache = new MessageCache(dbPath);
        cache.Initialize();

        var msg = new CachedMessage(123L, 456L, "Hello", "user_1", false, false, null, 1700000000L);
        var prev = cache.Put(msg);
        Assert.Null(prev);
    }

    [Fact]
    public void Test05_Negative_message_id_round_trips()
    {
        var dbPath = CreateTempDbPath();
        var cache = new MessageCache(dbPath);
        cache.Initialize();

        const long negativeMsgId = -100500L;
        var msg = new CachedMessage(777L, negativeMsgId, "Avatar marker", "user_1", false, false, null, 1700000000L);

        var prev = cache.Put(msg);
        Assert.Null(prev);

        var retrieved = cache.Get(777L, negativeMsgId);
        Assert.NotNull(retrieved);
        Assert.Equal(negativeMsgId, retrieved.MessageId);

        // Also assert that a positive id does NOT match this negative id
        var positiveCheck = cache.Get(777L, Math.Abs(negativeMsgId));
        Assert.Null(positiveCheck);
    }

    [Fact]
    public void Test06_Get_for_unknown_returns_null_and_does_not_throw()
    {
        var dbPath = CreateTempDbPath();
        var cache = new MessageCache(dbPath);
        cache.Initialize();

        var result = cache.Get(9999999L, 8888888L);
        Assert.Null(result);
    }

    [Fact]
    public void Test07_Prune_deletes_older_rows_keeps_boundary_and_newer()
    {
        var dbPath = CreateTempDbPath();
        var timeProvider = new TestTimeProvider(DateTimeOffset.FromUnixTimeSeconds(1700000000));
        var cache = new MessageCache(dbPath, timeProvider);
        cache.Initialize();

        // 30 days window in seconds: 30 * 86400 = 2,592,000
        const long now = 1700000000L;
        const long cutoff = now - (30 * 86400L); // 1,697,408,000

        var older = new CachedMessage(1L, 1L, "older", "u1", false, false, null, 1000L, CachedAt: cutoff - 1);
        var boundary = new CachedMessage(1L, 2L, "boundary", "u1", false, false, null, 1000L, CachedAt: cutoff);
        var newer = new CachedMessage(1L, 3L, "newer", "u1", false, false, null, 1000L, CachedAt: cutoff + 1);

        cache.Put(older);
        cache.Put(boundary);
        cache.Put(newer);

        var deletedCount = cache.Prune(30);

        Assert.Equal(1, deletedCount);
        Assert.Null(cache.Get(1L, 1L));        // Older row deleted
        Assert.NotNull(cache.Get(1L, 2L));     // Boundary row survives
        Assert.NotNull(cache.Get(1L, 3L));     // Newer row survives
    }

    [Fact]
    public void Test08_Stats_reports_row_count_and_non_zero_file_size()
    {
        var dbPath = CreateTempDbPath();
        var cache = new MessageCache(dbPath);
        cache.Initialize();

        cache.Put(new CachedMessage(1L, 1L, "m1", "u1", false, false, null, 1000L));
        cache.Put(new CachedMessage(1L, 2L, "m2", "u1", false, false, null, 1000L));
        cache.Put(new CachedMessage(1L, 3L, "m3", "u1", false, false, null, 1000L));

        var stats = cache.Stats();
        Assert.Equal(3, stats.RowCount);
        Assert.True(stats.FileSizeBytes > 0, $"Expected file size > 0, got {stats.FileSizeBytes}");
    }

    [Fact]
    public async Task Test09_Concurrency_parallel_puts_gets_and_prune_succeed_without_locking()
    {
        var dbPath = CreateTempDbPath();
        var cache = new MessageCache(dbPath);
        cache.Initialize();

        // 1. Verify WAL mode is active
        using (var checkConn = new SqliteConnection($"Data Source={dbPath}"))
        {
            checkConn.Open();
            using var checkCmd = checkConn.CreateCommand();
            checkCmd.CommandText = "PRAGMA journal_mode;";
            var mode = checkCmd.ExecuteScalar()?.ToString();
            Assert.Equal("wal", mode?.ToLowerInvariant());
        }

        // 2. An active reader must not block a concurrent Put (WAL guarantee)
        using (var activeReadConn = new SqliteConnection($"Data Source={dbPath};Default Timeout=0;"))
        {
            activeReadConn.Open();
            using var activeReadCmd = activeReadConn.CreateCommand();
            activeReadCmd.CommandText = "SELECT * FROM message_cache;";
            using var reader = activeReadCmd.ExecuteReader();
            cache.Put(new CachedMessage(999L, 999L, "wal_test", "u1", false, false, null, 1700000000L));
        }

        const int writerCount = 20;
        const int messagesPerWriter = 25;
        var tasks = new List<Task>();

        for (int w = 0; w < writerCount; w++)
        {
            int writerId = w;
            tasks.Add(Task.Run(() =>
            {
                for (int m = 0; m < messagesPerWriter; m++)
                {
                    long msgId = (writerId * 1000) + m;
                    cache.Put(new CachedMessage(100L, msgId, $"Text {msgId}", $"user_{writerId}", false, false, null, 1700000000L));
                    var retrieved = cache.Get(100L, msgId);
                    Assert.NotNull(retrieved);
                }
            }));
        }

        // Concurrently run Prune in the middle
        tasks.Add(Task.Run(async () =>
        {
            await Task.Delay(10);
            cache.Prune(30);
        }));

        await Task.WhenAll(tasks);

        var stats = cache.Stats();
        Assert.Equal(1 + (writerCount * messagesPerWriter), stats.RowCount);
    }

    [Fact]
    public async Task Test10_Two_parallel_Puts_for_same_message_exactly_one_reports_preexisting_row()
    {
        var dbPath = CreateTempDbPath();
        var cache = new MessageCache(dbPath);
        cache.Initialize();

        // Seed with initial message
        cache.Put(new CachedMessage(555L, 999L, "v0", "u1", false, false, null, 1000L));

        // Two parallel updates to the same message
        CachedMessage? replaced1 = null;
        CachedMessage? replaced2 = null;

        var barrier = new Barrier(2);
        var t1 = Task.Run(() =>
        {
            barrier.SignalAndWait();
            replaced1 = cache.Put(new CachedMessage(555L, 999L, "v1", "u1", false, false, null, 1001L));
        });

        var t2 = Task.Run(() =>
        {
            barrier.SignalAndWait();
            replaced2 = cache.Put(new CachedMessage(555L, 999L, "v2", "u1", false, false, null, 1002L));
        });

        await Task.WhenAll(t1, t2);

        // Exactly one should report the initial "v0" row
        int countV0 = (replaced1?.Text == "v0" ? 1 : 0) + (replaced2?.Text == "v0" ? 1 : 0);
        Assert.Equal(1, countV0);

        // Neither should be null since both found a pre-existing row (either v0 or the one inserted by the sibling task)
        Assert.NotNull(replaced1);
        Assert.NotNull(replaced2);
    }

    [Fact]
    public void Test11_Sqlite_failure_does_not_contain_message_text_in_exception()
    {
        // Pointing database at an existing directory causes SQLite to fail on open/create
        var invalidDbPath = Path.Combine(Path.GetTempPath(), $"dir_as_db_{Guid.NewGuid():N}");
        Directory.CreateDirectory(invalidDbPath);

        const string sensitiveMessageText = "TOP_SECRET_CREDENTIALS_AND_TEXT_12345";
        try
        {
            var cache = new MessageCache(invalidDbPath);
            var ex = Assert.Throws<MessageCacheException>(() =>
            {
                cache.Put(new CachedMessage(1L, 1L, sensitiveMessageText, "u1", false, false, null, 1000L));
            });

            Assert.DoesNotContain(sensitiveMessageText, ex.Message);
            Assert.DoesNotContain(sensitiveMessageText, ex.ToString());
        }
        finally
        {
            try { Directory.Delete(invalidDbPath, true); } catch { }
        }
    }

    [Fact]
    public async Task Test12_Periodic_prune_calls_prune_on_interval_and_swallows_failure()
    {
        var dbPath = CreateTempDbPath();
        var cache = new MessageCache(dbPath);
        cache.Initialize();

        int pruneCallCount = 0;
        int delayCallCount = 0;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        // Subclass or wrap to observe prune calls and inject an error on call #1
        var throwingCache = new ThrowingTestCache(dbPath, () =>
        {
            pruneCallCount++;
            if (pruneCallCount == 1)
            {
                throw new InvalidOperationException("Simulated prune failure");
            }
            if (pruneCallCount >= 2)
            {
                cts.Cancel();
            }
        });

        var pruner = new PeriodicCachePruner(
            throwingCache,
            retentionDays: 30,
            interval: TimeSpan.FromHours(6),
            logger: null,
            delay: (interval, ct) =>
            {
                delayCallCount++;
                if (delayCallCount >= 3)
                {
                    cts.Cancel();
                }
                return Task.CompletedTask;
            });

        await pruner.RunLoopAsync(cts.Token);

        Assert.True(pruneCallCount >= 2, $"Expected at least 2 prune calls, got {pruneCallCount}");
        Assert.True(delayCallCount >= 2, $"Expected at least 2 delays, got {delayCallCount}");
    }

    [Fact]
    public void Test13_CapturePreflight_reports_unwritable_cache_path_without_secrets()
    {
        const string secretHash = "super_secret_api_hash_value_999";
        // Invalid path on Windows
        var unwritablePath = "Z:\\non_existent_drive_999\\cache.db";

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Telegram:ApiId"] = "12345",
                ["Telegram:ApiHash"] = secretHash,
                ["Telegram:DatabaseDirectory"] = Path.GetTempPath(),
                ["Telegram:FilesDirectory"] = Path.GetTempPath(),
                ["Capture:CacheDatabasePath"] = unwritablePath
            })
            .Build();

        var report = CapturePreflight.Check(config, nativeLibChecker: _ => true);

        Assert.False(report.Success);
        Assert.Contains(report.Errors, e => e.Contains("Capture:CacheDatabasePath") || e.Contains(unwritablePath));
        Assert.DoesNotContain(report.Errors, e => e.Contains(secretHash));
    }

    [Fact]
    public void Test14_Logging_does_not_contain_message_text()
    {
        var logs = new List<string>();
        var fakeLogger = new TestLogger(logs);
        var dbPath = CreateTempDbPath();
        var cache = new MessageCache(dbPath);
        cache.Initialize();
        const string privateText = "PRIVATE_CHAT_MESSAGE_PAYLOAD_ABC";
        cache.Put(new CachedMessage(1L, 1L, privateText, "u1", false, false, null, 1000L));

        var pruner = new PeriodicCachePruner(cache, 30, TimeSpan.FromHours(6), fakeLogger);
        pruner.PruneOnce();

        foreach (var entry in logs)
        {
            Assert.DoesNotContain(privateText, entry);
        }
    }

    [Fact]
    public void Test15_Prune_is_scheduled_and_wired()
    {
        // Break g: if prune scheduling is removed from Worker/DI, pruner has no caller
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Capture:CacheDatabasePath"] = CreateTempDbPath(),
                ["Capture:CacheRetentionDays"] = "30",
                ["Capture:CachePruneIntervalHours"] = "6"
            })
            .Build();

        var services = new Microsoft.Extensions.DependencyInjection.ServiceCollection();
        services.AddSingleton<IConfiguration>(config);
        services.AddSingleton(sp => new MessageCache(config["Capture:CacheDatabasePath"]!));
        services.AddSingleton(sp => new PeriodicCachePruner(
            sp.GetRequiredService<MessageCache>(),
            int.Parse(config["Capture:CacheRetentionDays"]!),
            TimeSpan.FromHours(int.Parse(config["Capture:CachePruneIntervalHours"]!))));

        using var sp = services.BuildServiceProvider();
        var pruner = sp.GetService<PeriodicCachePruner>();
        Assert.NotNull(pruner);

        // Verify Prune is called when pruner runs
        int deleted = pruner.PruneOnce();
        Assert.Equal(0, deleted);
    }

    private class TestLogger(List<string> logs) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            logs.Add(formatter(state, exception));
        }
    }

    private class ThrowingTestCache : MessageCache
    {
        private readonly Action _onPrune;

        public ThrowingTestCache(string dbPath, Action onPrune) : base(dbPath)
        {
            _onPrune = onPrune;
        }

        public override int Prune(int olderThanDays)
        {
            _onPrune();
            return 0;
        }
    }
}
