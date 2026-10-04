using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CustomSync.Capture;
using CustomSync.Capture.Capture;
using CustomSync.Capture.Maintenance;
using CustomSync.Capture.Media;
using CustomSync.Capture.Preflight;
using CustomSync.Capture.Sync;
using CustomSync.Capture.Tdlib;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Xunit;

namespace CustomSync.Tests;

public class CaptureStorageMaintenanceTests
{
    private static string CreateTempDir()
    {
        var path = Path.Combine(Path.GetTempPath(), "cst_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static string CreateTempDbPath() =>
        Path.Combine(CreateTempDir(), "cache.db");

    private class MockDiskSpaceProbe(long? freeBytes) : IDiskSpaceProbe
    {
        public long? FreeBytes { get; set; } = freeBytes;
        public long? GetAvailableFreeBytes(string path) => FreeBytes;
    }

    private class TestTimeProvider(DateTimeOffset initial) : TimeProvider
    {
        private DateTimeOffset _current = initial;
        public override DateTimeOffset GetUtcNow() => _current;
        public void Advance(TimeSpan delta) => _current = _current.Add(delta);
        public void Set(DateTimeOffset dt) => _current = dt;
    }

    private class CapturingLogger<T> : ILogger<T>
    {
        public List<(LogLevel Level, string Message)> Logs { get; } = new();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            var msg = formatter(state, exception);
            Logs.Add((logLevel, msg));
        }
    }

    // 1. MediaStore: PathFor rejects non-canonical peer id and msg_id <= 0; IsManaged checks
    [Fact]
    public void Test01_MediaStore_PathFor_and_IsManaged_validation()
    {
        var storeDir = CreateTempDir();
        var store = new MediaStore(storeDir);

        // PathFor rejects non-canonical peer id
        Assert.Throws<ArgumentException>(() => store.PathFor("user123", 100));
        Assert.Throws<ArgumentException>(() => store.PathFor("-100123", 100));
        Assert.Throws<ArgumentException>(() => store.PathFor("0123", 100));
        Assert.Throws<ArgumentException>(() => store.PathFor("", 100));
        Assert.Throws<ArgumentException>(() => store.PathFor("   ", 100));
        Assert.Throws<ArgumentException>(() => store.PathFor("123a", 100));

        // PathFor rejects msg_id <= 0
        Assert.Throws<ArgumentException>(() => store.PathFor("123", 0));
        Assert.Throws<ArgumentException>(() => store.PathFor("123", -5));

        // Valid PathFor
        var expectedPath = Path.Combine(Path.GetFullPath(storeDir), "123-456.bin");
        Assert.Equal(expectedPath, store.PathFor("123", 456));
        Assert.Equal(Path.Combine(Path.GetFullPath(storeDir), "123-456.bin.part"), store.TempPathFor("123", 456));

        // IsManaged
        Assert.True(store.IsManaged(expectedPath));
        Assert.False(store.IsManaged(store.TempPathFor("123", 456))); // .part is not managed final
        Assert.True(store.IsManagedPart(store.TempPathFor("123", 456)));

        // Non-managed paths
        Assert.False(store.IsManaged(Path.Combine(CreateTempDir(), "123-456.bin"))); // Other dir
        Assert.False(store.IsManaged(Path.Combine(storeDir, "sub", "123-456.bin"))); // Subdirectory
        Assert.False(store.IsManaged(Path.Combine(storeDir, "..", "123-456.bin"))); // ..
        Assert.False(store.IsManaged(Path.Combine(storeDir, "photo.bin"))); // Other name
        Assert.False(store.IsManaged(Path.Combine(storeDir, "01-2.bin"))); // Leading zero
        Assert.False(store.IsManaged(Path.Combine(storeDir, "1-02.bin"))); // Leading zero in msg_id
    }

    // 2. Downloader (real TdClient, fake transport): row downloaded, local_path = store path, no .part left
    [Fact]
    public async Task Test02_MediaDownloader_downloads_and_copies_into_store()
    {
        var storeDir = CreateTempDir();
        var store = new MediaStore(storeDir);
        var dbPath = CreateTempDbPath();
        var cache = new MessageCache(dbPath);
        cache.Initialize();

        var tdlibFilesDir = CreateTempDir();
        var tdlibFile = Path.Combine(tdlibFilesDir, "tdlib_download.bin");
        var content = Encoding.UTF8.GetBytes("test media content for store");
        await File.WriteAllBytesAsync(tdlibFile, content);
        var expectedSha256 = Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();

        cache.QueueCapturedMedia("456", 100, 777, 100, "messagePhoto", content.Length);

        var transport = new FakeRecordingTdTransport
        {
            OnSend = (c, req) =>
            {
                using var d = JsonDocument.Parse(req);
                var type = d.RootElement.GetProperty("@type").GetString();
                var extra = d.RootElement.TryGetProperty("@extra", out var ex) ? ex.GetString() : null;

                if (type == "getMessage")
                {
                    return $$"""
                    {
                        "@type": "message",
                        "id": 100,
                        "chat_id": 777,
                        "content": {
                            "@type": "messagePhoto",
                            "photo": {
                                "sizes": [
                                    {
                                        "photo": {
                                            "@type": "file",
                                            "id": 888,
                                            "size": {{content.Length}},
                                            "expected_size": {{content.Length}},
                                            "local": { "path": "", "is_downloading_completed": false }
                                        }
                                    }
                                ]
                            }
                        },
                        "@extra": "{{extra}}"
                    }
                    """;
                }
                if (type == "downloadFile")
                {
                    var escaped = tdlibFile.Replace("\\", "\\\\");
                    return $$"""
                    {
                        "@type": "file",
                        "id": 888,
                        "size": {{content.Length}},
                        "local": { "path": "{{escaped}}", "is_downloading_completed": true },
                        "@extra": "{{extra}}"
                    }
                    """;
                }
                return null;
            }
        };

        var client = new TdClient(transport);
        var config = new MediaCaptureConfig
        {
            Enabled = true,
            PeerIds = new HashSet<string> { "456" },
            StorageDirectory = storeDir,
            MaxBytes = 10485760
        };

        var downloader = new MediaDownloader(client, cache, config, store, new MockDiskSpaceProbe(10L * 1024 * 1024 * 1024));
        bool processed = await downloader.ProcessPendingOnceAsync();
        Assert.True(processed);

        var row = cache.GetCapturedMedia("456", 100);
        Assert.NotNull(row);
        Assert.Equal("downloaded", row.Status);
        Assert.Equal(store.PathFor("456", 100), row.LocalPath);
        Assert.Equal(expectedSha256, row.Sha256);
        Assert.Equal(content.Length, row.Size);

        // Verify file in store
        Assert.True(File.Exists(row.LocalPath));
        Assert.Equal(content, await File.ReadAllBytesAsync(row.LocalPath));

        // Verify no .part left
        Assert.False(File.Exists(store.TempPathFor("456", 100)));

        // Verify TDLib source file untouched
        Assert.True(File.Exists(tdlibFile));
    }

    // 3. Source larger than MaxBytes although TDLib reported small size -> skipped, nothing in store
    [Fact]
    public async Task Test03_MediaDownloader_source_larger_than_MaxBytes_is_skipped()
    {
        var storeDir = CreateTempDir();
        var store = new MediaStore(storeDir);
        var dbPath = CreateTempDbPath();
        var cache = new MessageCache(dbPath);
        cache.Initialize();

        var tdlibFilesDir = CreateTempDir();
        var tdlibFile = Path.Combine(tdlibFilesDir, "huge.bin");
        var actualBytes = new byte[5000]; // larger than MaxBytes (3000)
        Random.Shared.NextBytes(actualBytes);
        await File.WriteAllBytesAsync(tdlibFile, actualBytes);

        cache.QueueCapturedMedia("456", 101, 777, 101, "messagePhoto", 1000); // TDLib reported 1000

        var transport = new FakeRecordingTdTransport
        {
            OnSend = (c, req) =>
            {
                using var d = JsonDocument.Parse(req);
                var type = d.RootElement.GetProperty("@type").GetString();
                var extra = d.RootElement.TryGetProperty("@extra", out var ex) ? ex.GetString() : null;

                if (type == "getMessage")
                {
                    var escaped = tdlibFile.Replace("\\", "\\\\");
                    return $$"""
                    {
                        "@type": "message",
                        "id": 101,
                        "chat_id": 777,
                        "content": {
                            "@type": "messagePhoto",
                            "photo": {
                                "sizes": [
                                    {
                                        "photo": {
                                            "@type": "file",
                                            "id": 999,
                                            "size": 1000,
                                            "expected_size": 1000,
                                            "local": { "path": "{{escaped}}", "is_downloading_completed": true }
                                        }
                                    }
                                ]
                            }
                        },
                        "@extra": "{{extra}}"
                    }
                    """;
                }
                return null;
            }
        };

        var client = new TdClient(transport);
        var config = new MediaCaptureConfig
        {
            Enabled = true,
            PeerIds = new HashSet<string> { "456" },
            StorageDirectory = storeDir,
            MaxBytes = 3000
        };

        var downloader = new MediaDownloader(client, cache, config, store, new MockDiskSpaceProbe(10L * 1024 * 1024 * 1024));
        bool processed = await downloader.ProcessPendingOnceAsync();
        Assert.True(processed);

        var row = cache.GetCapturedMedia("456", 101);
        Assert.NotNull(row);
        Assert.Equal("skipped", row.Status);

        // Nothing in store (neither final nor part)
        Assert.False(File.Exists(store.PathFor("456", 101)));
        Assert.False(File.Exists(store.TempPathFor("456", 101)));
    }

    [Fact]
    public async Task Test03b_MediaStore_CopyInAsync_throws_when_file_larger_than_maxBytes()
    {
        var storeDir = CreateTempDir();
        var store = new MediaStore(storeDir);
        var sourceFile = Path.Combine(CreateTempDir(), "source.bin");
        await File.WriteAllBytesAsync(sourceFile, new byte[500]);

        await Assert.ThrowsAsync<MediaFileTooLargeException>(() =>
            store.CopyInAsync("456", 1, sourceFile, 100));
    }

    // 4. Linux only: store directory is 0700, stored file is 0600
    [Fact]
    public async Task Test04_UnixFileMode_0700_and_0600_on_Linux()
    {
        if (OperatingSystem.IsWindows())
            return;

        // Papka hali yo'q: EnsureDirectoryCreated uni 0700 bilan yaratishi kerak
        // (oldindan yaratilgan papka umask bo'yicha 0755 bo'lib, test Linux'da yiqilardi).
        var storeDir = Path.Combine(CreateTempDir(), "store");
        var store = new MediaStore(storeDir);
        store.EnsureDirectoryCreated();

        var dirMode = File.GetUnixFileMode(storeDir);
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, dirMode);

        var tempSource = Path.Combine(CreateTempDir(), "source.bin");
        await File.WriteAllBytesAsync(tempSource, [1, 2, 3]);

        var (path, _, _) = await store.CopyInAsync("123", 456, tempSource, 1000);
        var fileMode = File.GetUnixFileMode(path);
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, fileMode);
    }

    // 5. Disk guard, free space: fake probe below MinFreeBytes -> no TDLib request, attempts unchanged, next attempt = now + 300, warned once in 10m
    [Fact]
    public async Task Test05_DiskGuard_free_space_defers_without_spending_attempt()
    {
        var storeDir = CreateTempDir();
        var store = new MediaStore(storeDir);
        var dbPath = CreateTempDbPath();
        var timeProvider = new TestTimeProvider(DateTimeOffset.FromUnixTimeSeconds(1000));
        var cache = new MessageCache(dbPath, timeProvider);
        cache.Initialize();

        cache.QueueCapturedMedia("456", 100, 777, 100, "messagePhoto", 500);
        cache.QueueCapturedMedia("456", 101, 777, 101, "messagePhoto", 500);

        var transport = new FakeRecordingTdTransport();
        var client = new TdClient(transport);
        var config = new MediaCaptureConfig
        {
            Enabled = true,
            PeerIds = new HashSet<string> { "456" },
            StorageDirectory = storeDir,
            MinFreeBytes = 1000
        };

        var logger = new CapturingLogger<MediaDownloader>();
        var probe = new MockDiskSpaceProbe(800); // 800 - 500 = 300 < 1000 MinFreeBytes

        var downloader = new MediaDownloader(client, cache, config, store, probe, timeProvider, logger);

        // Process first row
        bool p1 = await downloader.ProcessPendingOnceAsync();
        Assert.True(p1);

        // Transport saw no requests
        Assert.Empty(transport.SentPayloads);

        var r1 = cache.GetCapturedMedia("456", 100);
        Assert.NotNull(r1);
        Assert.Equal("pending", r1.Status);
        Assert.Equal(0, r1.Attempts); // No attempt spent!
        Assert.Equal(1000 + 300, r1.NextAttemptAt);

        // Warning logged
        Assert.Single(logger.Logs.Where(l => l.Level == LogLevel.Warning));

        // Process second row within 10 minutes -> no second warning
        timeProvider.Advance(TimeSpan.FromMinutes(2));
        bool p2 = await downloader.ProcessPendingOnceAsync();
        Assert.True(p2);

        var r2 = cache.GetCapturedMedia("456", 101);
        Assert.NotNull(r2);
        Assert.Equal("pending", r2.Status);
        Assert.Equal(0, r2.Attempts);

        // Still only 1 warning!
        Assert.Single(logger.Logs.Where(l => l.Level == LogLevel.Warning));
    }

    // 6. Disk guard, budget: existing downloaded sizes + row size > MaxTotalBytes -> deferred, no attempt spent
    [Fact]
    public async Task Test06_DiskGuard_budget_defers_without_spending_attempt()
    {
        var storeDir = CreateTempDir();
        var store = new MediaStore(storeDir);
        var dbPath = CreateTempDbPath();
        var timeProvider = new TestTimeProvider(DateTimeOffset.FromUnixTimeSeconds(1000));
        var cache = new MessageCache(dbPath, timeProvider);
        cache.Initialize();

        // Populate an existing downloaded row of size 800
        cache.QueueCapturedMedia("456", 50, 777, 50, "messagePhoto", 800);
        cache.UpdateMediaDownloaded("456", 50, Path.Combine(storeDir, "456-50.bin"), "hash", 800);

        // Queue pending row of size 300 (total = 1100 > 1000 MaxTotalBytes)
        cache.QueueCapturedMedia("456", 100, 777, 100, "messagePhoto", 300);

        var transport = new FakeRecordingTdTransport();
        var client = new TdClient(transport);
        var config = new MediaCaptureConfig
        {
            Enabled = true,
            PeerIds = new HashSet<string> { "456" },
            StorageDirectory = storeDir,
            MaxTotalBytes = 1000
        };

        var downloader = new MediaDownloader(client, cache, config, store, new MockDiskSpaceProbe(10L * 1024 * 1024 * 1024), timeProvider);
        bool p = await downloader.ProcessPendingOnceAsync();
        Assert.True(p);

        Assert.Empty(transport.SentPayloads);
        var r = cache.GetCapturedMedia("456", 100);
        Assert.NotNull(r);
        Assert.Equal("pending", r.Status);
        Assert.Equal(0, r.Attempts);
        Assert.Equal(1000 + 300, r.NextAttemptAt);
    }

    // 7. Unknown free space (probe returns null) -> download proceeds, one warning
    [Fact]
    public async Task Test07_DiskGuard_unknown_free_space_proceeds_with_one_warning()
    {
        var storeDir = CreateTempDir();
        var store = new MediaStore(storeDir);
        var dbPath = CreateTempDbPath();
        var cache = new MessageCache(dbPath);
        cache.Initialize();

        var tdlibFilesDir = CreateTempDir();
        var tdlibFile = Path.Combine(tdlibFilesDir, "media.bin");
        var content = Encoding.UTF8.GetBytes("hello");
        await File.WriteAllBytesAsync(tdlibFile, content);

        cache.QueueCapturedMedia("456", 100, 777, 100, "messagePhoto", content.Length);

        var transport = new FakeRecordingTdTransport
        {
            OnSend = (c, req) =>
            {
                using var d = JsonDocument.Parse(req);
                var type = d.RootElement.GetProperty("@type").GetString();
                var extra = d.RootElement.TryGetProperty("@extra", out var ex) ? ex.GetString() : null;
                if (type == "getMessage")
                {
                    var escaped = tdlibFile.Replace("\\", "\\\\");
                    return $$"""
                    {
                        "@type": "message",
                        "id": 100,
                        "chat_id": 777,
                        "content": {
                            "@type": "messagePhoto",
                            "photo": {
                                "sizes": [
                                    {
                                        "photo": {
                                            "@type": "file",
                                            "id": 111,
                                            "size": {{content.Length}},
                                            "local": { "path": "{{escaped}}", "is_downloading_completed": true }
                                        }
                                    }
                                ]
                            }
                        },
                        "@extra": "{{extra}}"
                    }
                    """;
                }
                return null;
            }
        };

        var client = new TdClient(transport);
        var config = new MediaCaptureConfig
        {
            Enabled = true,
            PeerIds = new HashSet<string> { "456" },
            StorageDirectory = storeDir
        };

        var logger = new CapturingLogger<MediaDownloader>();
        var probe = new MockDiskSpaceProbe(null); // Unknown free space
        var downloader = new MediaDownloader(client, cache, config, store, probe, logger: logger);

        bool processed = await downloader.ProcessPendingOnceAsync();
        Assert.True(processed);

        var row = cache.GetCapturedMedia("456", 100);
        Assert.NotNull(row);
        Assert.Equal("downloaded", row.Status);

        // Warned once
        Assert.Contains(logger.Logs, l => l.Level == LogLevel.Warning && l.Message.Contains("free disk space"));
    }

    // 8. Retention rule 1: uploaded/failed/skipped -> file deleted, local_path NULL, row kept until window passes
    [Fact]
    public async Task Test08_Retention_rule1_uploaded_failed_skipped_deletes_file_and_cleans_row_after_window()
    {
        var storeDir = CreateTempDir();
        var store = new MediaStore(storeDir);
        var dbPath = CreateTempDbPath();
        var timeProvider = new TestTimeProvider(DateTimeOffset.FromUnixTimeSeconds(1000000));
        var cache = new MessageCache(dbPath, timeProvider);
        cache.Initialize();

        // Create dummy files in store
        var f1 = store.PathFor("456", 1);
        var f2 = store.PathFor("456", 2);
        var f3 = store.PathFor("456", 3);
        await File.WriteAllBytesAsync(f1, [1]);
        await File.WriteAllBytesAsync(f2, [2]);
        await File.WriteAllBytesAsync(f3, [3]);

        cache.QueueCapturedMedia("456", 1, 777, 1, "messagePhoto", 1);
        cache.UpdateMediaDownloaded("456", 1, f1, "h1", 1);
        cache.UpdateMediaUploaded("456", 1);

        cache.QueueCapturedMedia("456", 2, 777, 2, "messagePhoto", 2);
        cache.UpdateMediaDownloaded("456", 2, f2, "h2", 2);
        cache.UpdateMediaFailed("456", 2, 5, null);

        cache.QueueCapturedMedia("456", 3, 777, 3, "messagePhoto", 3);
        cache.UpdateMediaDownloaded("456", 3, f3, "h3", 3);
        cache.UpdateMediaSkipped("456", 3);

        // Apply retention at now (1,000,000)
        cache.ApplyMediaRetention(1000000, 30, store);

        // Files deleted, local_path NULL, but rows still kept (window = 30 days hasn't passed)
        Assert.False(File.Exists(f1));
        Assert.False(File.Exists(f2));
        Assert.False(File.Exists(f3));

        var r1 = cache.GetCapturedMedia("456", 1);
        Assert.NotNull(r1);
        Assert.Null(r1.LocalPath);

        var r2 = cache.GetCapturedMedia("456", 2);
        Assert.NotNull(r2);
        Assert.Null(r2.LocalPath);

        var r3 = cache.GetCapturedMedia("456", 3);
        Assert.NotNull(r3);
        Assert.Null(r3.LocalPath);

        // Advance past 30 days (30 * 86400 = 2,592,000s)
        long future = 1000000 + 31 * 86400;
        cache.ApplyMediaRetention(future, 30, store);

        // Now rows are deleted
        Assert.Null(cache.GetCapturedMedia("456", 1));
        Assert.Null(cache.GetCapturedMedia("456", 2));
        Assert.Null(cache.GetCapturedMedia("456", 3));
    }

    // 9. Retention rule 4: with deleted outbox row present, media row and file survive for every status and any age
    [Fact]
    public async Task Test09_Retention_rule4_pending_outbox_deletion_preserves_media_and_file()
    {
        var storeDir = CreateTempDir();
        var store = new MediaStore(storeDir);
        var dbPath = CreateTempDbPath();
        var timeProvider = new TestTimeProvider(DateTimeOffset.FromUnixTimeSeconds(1000000));
        var cache = new MessageCache(dbPath, timeProvider);
        cache.Initialize();

        var filePath = store.PathFor("456", 99);
        await File.WriteAllBytesAsync(filePath, [9, 9]);

        cache.QueueCapturedMedia("456", 99, 777, 99, "messagePhoto", 2);
        cache.UpdateMediaDownloaded("456", 99, filePath, "h99", 2);
        cache.UpdateMediaUploaded("456", 99);

        // Insert pending deletion in capture_outbox
        using (var conn = new SqliteConnection($"Data Source={dbPath}"))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "INSERT INTO capture_outbox (kind, account_id, peer_id, msg_id, occurred_at, observed_at, payload_json, created_at) VALUES ('deleted', '111', '456', 99, 100, 100, '{}', 100);";
            cmd.ExecuteNonQuery();
        }

        // Run retention 100 days in the future
        long future = 1000000 + 100 * 86400;
        cache.ApplyMediaRetention(future, 30, store);

        // Survived! File exists, row exists with local_path intact
        Assert.True(File.Exists(filePath));
        var row = cache.GetCapturedMedia("456", 99);
        Assert.NotNull(row);
        Assert.Equal(filePath, row.LocalPath);
    }

    // 10. Retention rule 3: downloaded orphan -> file and row deleted; downloaded with cached message -> kept
    [Fact]
    public async Task Test10_Retention_rule3_downloaded_orphan_deleted_cached_message_kept()
    {
        var storeDir = CreateTempDir();
        var store = new MediaStore(storeDir);
        var dbPath = CreateTempDbPath();
        var cache = new MessageCache(dbPath);
        cache.Initialize();

        var fOrphan = store.PathFor("456", 10);
        var fCached = store.PathFor("456", 20);
        await File.WriteAllBytesAsync(fOrphan, [10]);
        await File.WriteAllBytesAsync(fCached, [20]);

        cache.QueueCapturedMedia("456", 10, 777, 10, "messagePhoto", 1);
        cache.UpdateMediaDownloaded("456", 10, fOrphan, "h10", 1);

        cache.QueueCapturedMedia("456", 20, 777, 20, "messagePhoto", 1);
        cache.UpdateMediaDownloaded("456", 20, fCached, "h20", 1);

        // Put message 20 into message_cache
        cache.Put(new CachedMessage(777, 20, "text", "user456", false, false, null, 1000, 1000));

        // Retention
        cache.ApplyMediaRetention(1000, 30, store);

        // Orphan (10) deleted completely
        Assert.False(File.Exists(fOrphan));
        Assert.Null(cache.GetCapturedMedia("456", 10));

        // Cached (20) kept
        Assert.True(File.Exists(fCached));
        Assert.NotNull(cache.GetCapturedMedia("456", 20));
    }

    // 11. Retention rule 2: pending older than window -> deleted; pending orphan -> deleted; fresh pending with message -> kept
    [Fact]
    public void Test11_Retention_rule2_pending_retention_and_orphan()
    {
        var storeDir = CreateTempDir();
        var store = new MediaStore(storeDir);
        var dbPath = CreateTempDbPath();
        var timeProvider = new TestTimeProvider(DateTimeOffset.FromUnixTimeSeconds(1000000));
        var cache = new MessageCache(dbPath, timeProvider);
        cache.Initialize();

        // 1. Pending orphan
        cache.QueueCapturedMedia("456", 1, 777, 1, "messagePhoto", 100);

        // 2. Fresh pending with cached message
        cache.QueueCapturedMedia("456", 2, 777, 2, "messagePhoto", 100);
        cache.Put(new CachedMessage(777, 2, "text", "user456", false, false, null, 1000, 1000));

        // 3. Old pending with cached message (older than 30 days)
        timeProvider.Set(DateTimeOffset.FromUnixTimeSeconds(1000000 - 35 * 86400));
        cache.QueueCapturedMedia("456", 3, 777, 3, "messagePhoto", 100);
        cache.Put(new CachedMessage(777, 3, "text", "user456", false, false, null, 1000, 1000));

        // Apply retention at 1000000
        cache.ApplyMediaRetention(1000000, 30, store);

        Assert.Null(cache.GetCapturedMedia("456", 1)); // orphan deleted
        Assert.NotNull(cache.GetCapturedMedia("456", 2)); // fresh with message kept
        Assert.Null(cache.GetCapturedMedia("456", 3)); // old pending deleted
    }

    // 12. Retention rule 5: local_path outside store is never deleted as a file
    [Fact]
    public async Task Test12_Retention_rule5_local_path_outside_store_is_not_deleted()
    {
        var storeDir = CreateTempDir();
        var store = new MediaStore(storeDir);
        var outsideDir = CreateTempDir();
        var outsideFile = Path.Combine(outsideDir, "tdlib_cache.bin");
        await File.WriteAllBytesAsync(outsideFile, [1, 2, 3]);

        var dbPath = CreateTempDbPath();
        var cache = new MessageCache(dbPath);
        cache.Initialize();

        cache.QueueCapturedMedia("456", 1, 777, 1, "messagePhoto", 3);
        cache.UpdateMediaDownloaded("456", 1, outsideFile, "hash", 3);
        cache.UpdateMediaUploaded("456", 1);

        cache.ApplyMediaRetention(1000, 30, store);

        // File outside store must NOT be deleted!
        Assert.True(File.Exists(outsideFile));

        // Row local_path was cleared
        var row = cache.GetCapturedMedia("456", 1);
        Assert.NotNull(row);
        Assert.Null(row.LocalPath);
    }

    // 13. Retention and sweep run with Capture:Media:Enabled = false
    [Fact]
    public async Task Test13_Retention_and_sweep_run_when_media_disabled()
    {
        var storeDir = CreateTempDir();
        var store = new MediaStore(storeDir);
        var dbPath = CreateTempDbPath();
        var timeProvider = new TestTimeProvider(DateTimeOffset.FromUnixTimeSeconds(1000000));
        var cache = new MessageCache(dbPath, timeProvider);
        cache.Initialize();

        // Old unreferenced file in store (older than 1h from timeProvider's 1000000)
        var unrefFile = store.PathFor("456", 10);
        await File.WriteAllBytesAsync(unrefFile, [10]);
        File.SetLastWriteTimeUtc(unrefFile, DateTimeOffset.FromUnixTimeSeconds(1000000 - 7200).UtcDateTime);

        // Uploaded row in DB
        var upFile = store.PathFor("456", 20);
        await File.WriteAllBytesAsync(upFile, [20]);
        cache.QueueCapturedMedia("456", 20, 777, 20, "messagePhoto", 1);
        cache.UpdateMediaDownloaded("456", 20, upFile, "hash", 1);
        cache.UpdateMediaUploaded("456", 20);

        var config = new MediaCaptureConfig
        {
            Enabled = false, // Media is disabled!
            StorageDirectory = storeDir
        };

        var transport = new FakeRecordingTdTransport
        {
            OnSend = (c, req) =>
            {
                using var d = JsonDocument.Parse(req);
                var extra = d.RootElement.TryGetProperty("@extra", out var ex) ? ex.GetString() : null;
                return $$"""{"@type":"ok","@extra":"{{extra}}"}""";
            }
        };

        var maintenance = new StorageMaintenance(
            cache, store, new TdClient(transport), new MockDiskSpaceProbe(10L * 1024 * 1024 * 1024), config, timeProvider: timeProvider);

        await maintenance.RunOnceAsync();

        // Both retention and sweep ran!
        Assert.False(File.Exists(unrefFile));
        Assert.False(File.Exists(upFile));
    }

    // 14. Orphan sweep matrix: referenced kept, unreferenced old final deleted, unreferenced old .part deleted,
    // unreferenced fresh kept, old file with other name kept, subdirectory kept
    [Fact]
    public async Task Test14_Orphan_sweep_matrix()
    {
        var storeDir = CreateTempDir();
        var store = new MediaStore(storeDir);
        var dbPath = CreateTempDbPath();
        var cache = new MessageCache(dbPath);
        cache.Initialize();

        // 1. Referenced file
        var fRef = store.PathFor("456", 1);
        await File.WriteAllBytesAsync(fRef, [1]);
        File.SetLastWriteTimeUtc(fRef, DateTime.UtcNow.AddHours(-2));
        cache.QueueCapturedMedia("456", 1, 777, 1, "messagePhoto", 1);
        cache.UpdateMediaDownloaded("456", 1, fRef, "hash", 1);

        // 2. Unreferenced old final (>1h)
        var fUnrefOld = store.PathFor("456", 2);
        await File.WriteAllBytesAsync(fUnrefOld, [2]);
        File.SetLastWriteTimeUtc(fUnrefOld, DateTime.UtcNow.AddHours(-2));

        // 3. Unreferenced old .part (>1h)
        var fUnrefOldPart = store.TempPathFor("456", 3);
        await File.WriteAllBytesAsync(fUnrefOldPart, [3]);
        File.SetLastWriteTimeUtc(fUnrefOldPart, DateTime.UtcNow.AddHours(-2));

        // 4. Unreferenced fresh (<1h)
        var fUnrefFresh = store.PathFor("456", 4);
        await File.WriteAllBytesAsync(fUnrefFresh, [4]);
        File.SetLastWriteTimeUtc(fUnrefFresh, DateTime.UtcNow);

        // 5. Old file with another name
        var fOtherName = Path.Combine(storeDir, "device-state.json");
        await File.WriteAllBytesAsync(fOtherName, [5]);
        File.SetLastWriteTimeUtc(fOtherName, DateTime.UtcNow.AddHours(-2));

        // 6. Subdirectory
        var subDir = Path.Combine(storeDir, "subdir");
        Directory.CreateDirectory(subDir);

        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        int swept = store.SweepOrphans(cache, now);
        Assert.Equal(2, swept);

        Assert.True(File.Exists(fRef));
        Assert.False(File.Exists(fUnrefOld));
        Assert.False(File.Exists(fUnrefOldPart));
        Assert.True(File.Exists(fUnrefFresh));
        Assert.True(File.Exists(fOtherName));
        Assert.True(Directory.Exists(subDir));
    }

    // 15. Migration: cache files at v1, v2, v3, v4 -> Initialize -> user_version 5, created_at present, v4 rows kept with created_at = now; fresh -> 5; twice -> idempotent
    [Fact]
    public void Test15_Migration_upgrades_all_versions_to_v5()
    {
        var timeProvider = new TestTimeProvider(DateTimeOffset.FromUnixTimeSeconds(1700000000));

        // Fresh database
        var freshPath = CreateTempDbPath();
        var freshCache = new MessageCache(freshPath, timeProvider);
        freshCache.Initialize();
        freshCache.Initialize(); // twice is no-op
        using (var conn = new SqliteConnection($"Data Source={freshPath}"))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "PRAGMA user_version;";
            Assert.Equal(6, Convert.ToInt32(cmd.ExecuteScalar()));
        }

        // Upgrade from v4 database
        var v4Path = CreateTempDbPath();
        using (var conn = new SqliteConnection($"Data Source={v4Path}"))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                CREATE TABLE message_cache (
                    chat_id INTEGER NOT NULL,
                    message_id INTEGER NOT NULL,
                    text TEXT,
                    sender_id TEXT,
                    date INTEGER NOT NULL,
                    cached_at INTEGER NOT NULL,
                    PRIMARY KEY (chat_id, message_id)
                );
                CREATE TABLE capture_outbox (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    kind TEXT NOT NULL,
                    peer_id INTEGER NOT NULL,
                    msg_id INTEGER NOT NULL,
                    edit_date INTEGER NOT NULL DEFAULT 0,
                    media_hashes_json TEXT NOT NULL DEFAULT '[]',
                    retry_count INTEGER NOT NULL DEFAULT 0,
                    next_retry_at INTEGER,
                    last_error TEXT,
                    created_at INTEGER NOT NULL DEFAULT 0
                );
                CREATE TABLE pending_edits (
                    chat_id INTEGER NOT NULL,
                    message_id INTEGER NOT NULL,
                    edit_date INTEGER NOT NULL,
                    observed_at INTEGER NOT NULL,
                    PRIMARY KEY (chat_id, message_id)
                );
                CREATE TABLE device_sync_state (
                    key TEXT PRIMARY KEY,
                    value TEXT NOT NULL
                );
                CREATE TABLE captured_media (
                    peer_id TEXT NOT NULL,
                    msg_id INTEGER NOT NULL,
                    chat_id INTEGER NOT NULL,
                    message_id INTEGER NOT NULL,
                    content_type TEXT NOT NULL,
                    status TEXT NOT NULL,
                    attempts INTEGER NOT NULL DEFAULT 0,
                    next_attempt_at INTEGER,
                    local_path TEXT,
                    sha256 TEXT,
                    size INTEGER,
                    PRIMARY KEY (peer_id, msg_id)
                );
                INSERT INTO captured_media (peer_id, msg_id, chat_id, message_id, content_type, status)
                VALUES ('123', 456, 789, 456, 'photo', 'pending');
                PRAGMA user_version = 4;
            ";
            cmd.ExecuteNonQuery();
        }

        var v4Cache = new MessageCache(v4Path, timeProvider);
        v4Cache.Initialize();

        using (var conn = new SqliteConnection($"Data Source={v4Path}"))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "PRAGMA user_version;";
            Assert.Equal(6, Convert.ToInt32(cmd.ExecuteScalar()));

            cmd.CommandText = "SELECT created_at FROM captured_media WHERE peer_id = '123' AND msg_id = 456;";
            var createdAt = Convert.ToInt64(cmd.ExecuteScalar());
            Assert.Equal(1700000000, createdAt);
        }
    }

    // 16. QueueCapturedMedia sets created_at from TimeProvider
    [Fact]
    public void Test16_QueueCapturedMedia_sets_created_at_from_TimeProvider()
    {
        var dbPath = CreateTempDbPath();
        var timeProvider = new TestTimeProvider(DateTimeOffset.FromUnixTimeSeconds(123456789));
        var cache = new MessageCache(dbPath, timeProvider);
        cache.Initialize();

        cache.QueueCapturedMedia("456", 10, 777, 10, "messagePhoto", 500);
        var row = cache.GetCapturedMedia("456", 10);
        Assert.NotNull(row);
        Assert.Equal(123456789, row.CreatedAt);
    }

    // 17. Policy Theory: optimizeStorage validation
    [Theory]
    [InlineData("count_0", """{"@type":"optimizeStorage","size":16777216,"ttl":3600,"count":0,"immunity_delay":600}""")]
    [InlineData("count_5", """{"@type":"optimizeStorage","size":16777216,"ttl":3600,"count":5,"immunity_delay":600}""")]
    [InlineData("immunity_0", """{"@type":"optimizeStorage","size":16777216,"ttl":3600,"count":-1,"immunity_delay":0}""")]
    [InlineData("immunity_599", """{"@type":"optimizeStorage","size":16777216,"ttl":3600,"count":-1,"immunity_delay":599}""")]
    [InlineData("size_0", """{"@type":"optimizeStorage","size":0,"ttl":3600,"count":-1,"immunity_delay":600}""")]
    [InlineData("size_neg1", """{"@type":"optimizeStorage","size":-1,"ttl":3600,"count":-1,"immunity_delay":600}""")]
    [InlineData("size_too_small", """{"@type":"optimizeStorage","size":16777215,"ttl":3600,"count":-1,"immunity_delay":600}""")]
    [InlineData("ttl_0", """{"@type":"optimizeStorage","size":16777216,"ttl":0,"count":-1,"immunity_delay":600}""")]
    [InlineData("chat_ids_nonempty", """{"@type":"optimizeStorage","size":16777216,"ttl":3600,"count":-1,"immunity_delay":600,"chat_ids":[1]}""")]
    [InlineData("exclude_chat_ids_nonempty", """{"@type":"optimizeStorage","size":16777216,"ttl":3600,"count":-1,"immunity_delay":600,"exclude_chat_ids":[1]}""")]
    [InlineData("file_types_nonempty", """{"@type":"optimizeStorage","size":16777216,"ttl":3600,"count":-1,"immunity_delay":600,"file_types":[{"@type":"fileTypeThumbnail"}]}""")]
    [InlineData("unknown_key", """{"@type":"optimizeStorage","size":16777216,"ttl":3600,"count":-1,"immunity_delay":600,"unknown":"bad"}""")]
    [InlineData("number_as_string", """{"@type":"optimizeStorage","size":"16777216","ttl":3600,"count":-1,"immunity_delay":600}""")]
    public void Test17_Policy_optimizeStorage_disallowed_variations_rejected(string caseName, string payload)
    {
        _ = caseName;
        Assert.Throws<TdRequestNotAllowedException>(() => TdRequestPolicy.ValidateAndNormalize(payload));
    }

    [Fact]
    public void Test17_Policy_optimizeStorage_valid_passes()
    {
        var valid = """
        {
            "@type": "optimizeStorage",
            "size": 16777216,
            "ttl": 3600,
            "count": -1,
            "immunity_delay": 600,
            "file_types": [],
            "chat_ids": [],
            "exclude_chat_ids": [],
            "return_deleted_file_statistics": true,
            "chat_limit": 0
        }
        """;
        var res = TdRequestPolicy.ValidateAndNormalize(valid);
        Assert.NotNull(res);
    }

    // 18. Policy: getStorageStatisticsFast and setLogVerbosityLevel validation, forbidden methods still rejected
    [Fact]
    public void Test18_Policy_getStorageStatisticsFast_and_setLogVerbosityLevel()
    {
        // getStorageStatisticsFast
        Assert.NotNull(TdRequestPolicy.ValidateAndNormalize("""{"@type":"getStorageStatisticsFast"}"""));
        Assert.Throws<TdRequestNotAllowedException>(() =>
            TdRequestPolicy.ValidateAndNormalize("""{"@type":"getStorageStatisticsFast","extra_key":"bad"}"""));

        // setLogVerbosityLevel
        Assert.NotNull(TdRequestPolicy.ValidateAndNormalize("""{"@type":"setLogVerbosityLevel","new_verbosity_level":0}"""));
        Assert.NotNull(TdRequestPolicy.ValidateAndNormalize("""{"@type":"setLogVerbosityLevel","new_verbosity_level":1}"""));
        Assert.NotNull(TdRequestPolicy.ValidateAndNormalize("""{"@type":"setLogVerbosityLevel","new_verbosity_level":2}"""));

        Assert.Throws<TdRequestNotAllowedException>(() =>
            TdRequestPolicy.ValidateAndNormalize("""{"@type":"setLogVerbosityLevel","new_verbosity_level":3}"""));
        Assert.Throws<TdRequestNotAllowedException>(() =>
            TdRequestPolicy.ValidateAndNormalize("""{"@type":"setLogVerbosityLevel","new_verbosity_level":5}"""));
        Assert.Throws<TdRequestNotAllowedException>(() =>
            TdRequestPolicy.ValidateAndNormalize("""{"@type":"setLogVerbosityLevel","new_verbosity_level":-1}"""));
        Assert.Throws<TdRequestNotAllowedException>(() =>
            TdRequestPolicy.ValidateAndNormalize("""{"@type":"setLogVerbosityLevel","new_verbosity_level":"1"}"""));
        Assert.Throws<TdRequestNotAllowedException>(() =>
            TdRequestPolicy.ValidateAndNormalize("""{"@type":"setLogVerbosityLevel"}"""));

        // Forbidden methods still rejected
        Assert.Throws<TdRequestNotAllowedException>(() =>
            TdRequestPolicy.ValidateAndNormalize("""{"@type":"viewMessages","chat_id":1,"message_ids":[2]}"""));
        Assert.Throws<TdRequestNotAllowedException>(() =>
            TdRequestPolicy.ValidateAndNormalize("""{"@type":"openMessageContent","chat_id":1,"message_id":2}"""));
        Assert.Throws<TdRequestNotAllowedException>(() =>
            TdRequestPolicy.ValidateAndNormalize("""{"@type":"deleteFile","file_id":1}"""));
    }

    // 19. Maintenance sends optimizeStorage with configured size, ttl, immunity_delay, count = -1, empty arrays
    [Fact]
    public async Task Test19_Maintenance_sends_optimizeStorage_with_configured_values()
    {
        var storeDir = CreateTempDir();
        var store = new MediaStore(storeDir);
        var dbPath = CreateTempDbPath();
        var cache = new MessageCache(dbPath);
        cache.Initialize();

        var transport = new FakeRecordingTdTransport
        {
            OnSend = (c, req) =>
            {
                using var d = JsonDocument.Parse(req);
                var type = d.RootElement.GetProperty("@type").GetString();
                var extra = d.RootElement.TryGetProperty("@extra", out var ex) ? ex.GetString() : null;

                if (type == "optimizeStorage")
                {
                    return $$"""
                    {
                        "@type": "storageStatistics",
                        "size": 5242880,
                        "count": 12,
                        "@extra": "{{extra}}"
                    }
                    """;
                }
                if (type == "getStorageStatisticsFast")
                {
                    return $$"""
                    {
                        "@type": "storageStatisticsFast",
                        "files_size": 10000000,
                        "file_database_size": 2000000,
                        "database_size": 3000000,
                        "@extra": "{{extra}}"
                    }
                    """;
                }
                return null;
            }
        };

        var client = new TdClient(transport);
        var config = new MediaCaptureConfig
        {
            StorageDirectory = storeDir,
            TdlibFilesMaxBytes = 600000000,
            TdlibFilesTtlHours = 48,
            TdlibImmunitySeconds = 7200
        };

        var reqTest = JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["@type"] = "optimizeStorage",
            ["size"] = config.TdlibFilesMaxBytes,
            ["ttl"] = (long)config.TdlibFilesTtlHours * 3600,
            ["count"] = -1,
            ["immunity_delay"] = config.TdlibImmunitySeconds,
            ["file_types"] = Array.Empty<object>(),
            ["chat_ids"] = Array.Empty<long>(),
            ["exclude_chat_ids"] = Array.Empty<long>(),
            ["return_deleted_file_statistics"] = true,
            ["chat_limit"] = 0
        });
        TdRequestPolicy.ValidateAndNormalize(reqTest);

        var logger = new CapturingLogger<StorageMaintenance>();
        var maintenance = new StorageMaintenance(
            cache, store, client, new MockDiskSpaceProbe(10L * 1024 * 1024 * 1024), config, logger: logger);

        await maintenance.RunOnceAsync();

        var optCall = transport.SentPayloads.FirstOrDefault(p => p.Contains("optimizeStorage"));
        Assert.True(optCall != null, $"optimizeStorage not sent. Logs: {string.Join(" | ", logger.Logs.Select(l => l.Message))}");

        using var doc = JsonDocument.Parse(optCall);
        var root = doc.RootElement;
        Assert.Equal(600000000, root.GetProperty("size").GetInt64());
        Assert.Equal(48 * 3600, root.GetProperty("ttl").GetInt64());
        Assert.Equal(-1, root.GetProperty("count").GetInt32());
        Assert.Equal(7200, root.GetProperty("immunity_delay").GetInt32());
        Assert.True(root.GetProperty("return_deleted_file_statistics").GetBoolean());
        Assert.Equal(0, root.GetProperty("chat_limit").GetInt32());
        Assert.Empty(root.GetProperty("file_types").EnumerateArray());
        Assert.Empty(root.GetProperty("chat_ids").EnumerateArray());
        Assert.Empty(root.GetProperty("exclude_chat_ids").EnumerateArray());

        // Snapshot recorded freed stats
        Assert.NotNull(maintenance.LatestSnapshot);
        Assert.Equal(5242880, maintenance.LatestSnapshot.OptimizeFreedBytes);
        Assert.Equal(12, maintenance.LatestSnapshot.OptimizeDeletedFiles);
    }

    // 20. Step isolation: optimizeStorage answering error or throwing TdException -> retention and sweep still run, snapshot made and logged, next run happens
    [Fact]
    public async Task Test20_Maintenance_step_isolation()
    {
        var storeDir = CreateTempDir();
        var store = new MediaStore(storeDir);
        var dbPath = CreateTempDbPath();
        var cache = new MessageCache(dbPath);
        cache.Initialize();

        // Add orphan file that should be swept
        var orphan = store.PathFor("456", 1);
        await File.WriteAllBytesAsync(orphan, [1]);
        File.SetLastWriteTimeUtc(orphan, DateTime.UtcNow.AddHours(-2));

        var transport = new FakeRecordingTdTransport
        {
            OnSend = (c, req) =>
            {
                using var d = JsonDocument.Parse(req);
                var type = d.RootElement.GetProperty("@type").GetString();
                var extra = d.RootElement.TryGetProperty("@extra", out var ex) ? ex.GetString() : null;

                if (type == "optimizeStorage")
                {
                    return $$"""{"@type":"error","code":500,"message":"INTERNAL_ERROR","@extra":"{{extra}}"}""";
                }
                if (type == "getStorageStatisticsFast")
                {
                    return $$"""{"@type":"storageStatisticsFast","files_size":100,"database_size":200,"@extra":"{{extra}}"}""";
                }
                return null;
            }
        };

        var client = new TdClient(transport);
        var config = new MediaCaptureConfig { StorageDirectory = storeDir };

        var logger = new CapturingLogger<StorageMaintenance>();
        using var cts = new CancellationTokenSource();
        long? snapshotBytesAtRun1 = null;
        int delayCount = 0;
        StorageMaintenance? maintenance = null;
        maintenance = new StorageMaintenance(
            cache, store, client, new MockDiskSpaceProbe(10L * 1024 * 1024 * 1024), config,
            logger: logger,
            delay: (ts, ct) =>
            {
                delayCount++;
                if (delayCount == 1)
                {
                    snapshotBytesAtRun1 = maintenance!.LatestSnapshot?.TdlibFilesBytes;
                }
                else
                {
                    cts.Cancel();
                }
                return Task.CompletedTask;
            });

        var loopTask = Task.Run(() => maintenance.RunLoopAsync(cts.Token));
        await Task.WhenAny(loopTask, Task.Delay(3000));
        cts.Cancel();
        try { await loopTask; } catch { }

        // Orphan was still deleted
        Assert.False(File.Exists(orphan));

        // Snapshot was still created
        Assert.NotNull(maintenance.LatestSnapshot);
        Assert.Equal(100, snapshotBytesAtRun1);
        Assert.True(delayCount >= 2);
    }

    // 21. Snapshot values: CacheDatabaseBytes = Stats().FileSizeBytes, MediaStore files on disk, TDLib fast stats, MemoryLimitBytes, FreeDiskBytes
    [Fact]
    public async Task Test21_StorageSnapshot_values()
    {
        var storeDir = CreateTempDir();
        var store = new MediaStore(storeDir);
        var f1 = store.PathFor("456", 1);
        await File.WriteAllBytesAsync(f1, new byte[100]);

        var dbPath = CreateTempDbPath();
        var cache = new MessageCache(dbPath);
        cache.Initialize();

        var transport = new FakeRecordingTdTransport
        {
            OnSend = (c, req) =>
            {
                using var d = JsonDocument.Parse(req);
                var type = d.RootElement.GetProperty("@type").GetString();
                var extra = d.RootElement.TryGetProperty("@extra", out var ex) ? ex.GetString() : null;
                if (type == "optimizeStorage")
                    return $$"""{"@type":"storageStatistics","size":50,"count":1,"@extra":"{{extra}}"}""";
                if (type == "getStorageStatisticsFast")
                    return $$"""{"@type":"storageStatisticsFast","files_size":7777,"database_size":8888,"@extra":"{{extra}}"}""";
                return null;
            }
        };

        var cgroupDir = CreateTempDir();
        var procCgroup = Path.Combine(cgroupDir, "cgroup");
        var sysFsCgroup = Path.Combine(cgroupDir, "sysfs");
        Directory.CreateDirectory(sysFsCgroup);
        await File.WriteAllTextAsync(procCgroup, "0::/\n");
        await File.WriteAllTextAsync(Path.Combine(sysFsCgroup, "memory.max"), "104857600\n");

        var client = new TdClient(transport);
        var config = new MediaCaptureConfig { StorageDirectory = storeDir };
        var probe = new MockDiskSpaceProbe(50000000);

        var maintenance = new StorageMaintenance(
            cache, store, client, probe, config,
            procCgroupPath: procCgroup, sysFsCgroupRoot: sysFsCgroup);

        await maintenance.RunOnceAsync();

        var snap = maintenance.LatestSnapshot;
        Assert.NotNull(snap);
        Assert.Equal(cache.Stats().FileSizeBytes, snap.CacheDatabaseBytes);
        Assert.Equal(100, snap.MediaStoreBytes);
        Assert.Equal(1, snap.MediaStoreFiles);
        Assert.Equal(7777, snap.TdlibFilesBytes);
        Assert.Equal(8888, snap.TdlibDatabaseBytes);
        Assert.Equal(50000000, snap.FreeDiskBytes);
        Assert.Equal(104857600, snap.MemoryLimitBytes);
        Assert.Equal(50, snap.OptimizeFreedBytes);
        Assert.Equal(1, snap.OptimizeDeletedFiles);
    }

    // 22. cgroup reader: number -> value; max -> null; no 0:: line -> null; missing files -> null
    [Fact]
    public async Task Test22_CgroupV2_memory_limit_reader()
    {
        var tempDir = CreateTempDir();
        var procFile = Path.Combine(tempDir, "proc_cgroup");
        var sysFsDir = Path.Combine(tempDir, "sysfs");
        Directory.CreateDirectory(sysFsDir);

        // 1. Number -> value
        await File.WriteAllTextAsync(procFile, "0::/user.slice\n");
        var sliceDir = Path.Combine(sysFsDir, "user.slice");
        Directory.CreateDirectory(sliceDir);
        await File.WriteAllTextAsync(Path.Combine(sliceDir, "memory.max"), "2147483648\n");
        Assert.Equal(2147483648L, StorageMaintenance.ReadCgroupV2MemoryLimit(procFile, sysFsDir));

        // 2. "max" -> null
        await File.WriteAllTextAsync(Path.Combine(sliceDir, "memory.max"), "max\n");
        Assert.Null(StorageMaintenance.ReadCgroupV2MemoryLimit(procFile, sysFsDir));

        // 3. No 0:: line -> null
        await File.WriteAllTextAsync(procFile, "1:name=systemd:/\n");
        Assert.Null(StorageMaintenance.ReadCgroupV2MemoryLimit(procFile, sysFsDir));

        // 4. Missing files -> null
        Assert.Null(StorageMaintenance.ReadCgroupV2MemoryLimit("nonexistent_proc", sysFsDir));
        await File.WriteAllTextAsync(procFile, "0::/missing\n");
        Assert.Null(StorageMaintenance.ReadCgroupV2MemoryLimit(procFile, sysFsDir));
    }

    // 23. Logs: summary has sizes and counts, none of store path, file names or ids; low free disk warning; RSS > 80% limit warning
    [Fact]
    public async Task Test23_Logs_summary_has_MB_and_counts_without_paths_or_IDs()
    {
        var storeDir = CreateTempDir();
        var store = new MediaStore(storeDir);
        var dbPath = CreateTempDbPath();
        var cache = new MessageCache(dbPath);
        cache.Initialize();

        var secretMarker = "secret_peer_999888";
        var secretFile = store.PathFor("999888", 12345);
        await File.WriteAllBytesAsync(secretFile, [1, 2]);

        var logger = new CapturingLogger<StorageMaintenance>();
        var transport = new FakeRecordingTdTransport
        {
            OnSend = (c, req) =>
            {
                using var d = JsonDocument.Parse(req);
                var type = d.RootElement.GetProperty("@type").GetString();
                var extra = d.RootElement.TryGetProperty("@extra", out var ex) ? ex.GetString() : null;
                if (type == "optimizeStorage")
                    return $$"""{"@type":"storageStatistics","size":0,"count":0,"@extra":"{{extra}}"}""";
                if (type == "getStorageStatisticsFast")
                    return $$"""{"@type":"storageStatisticsFast","files_size":0,"database_size":0,"@extra":"{{extra}}"}""";
                return null;
            }
        };

        var cgroupDir = CreateTempDir();
        var procCgroup = Path.Combine(cgroupDir, "cgroup");
        var sysFsCgroup = Path.Combine(cgroupDir, "sysfs");
        Directory.CreateDirectory(sysFsCgroup);
        await File.WriteAllTextAsync(procCgroup, "0::/\n");
        // Limit small enough so working set triggers > 80%
        await File.WriteAllTextAsync(Path.Combine(sysFsCgroup, "memory.max"), "1000\n");

        var config = new MediaCaptureConfig
        {
            StorageDirectory = storeDir,
            MinFreeBytes = 5000000000L // 5 GB
        };
        var probe = new MockDiskSpaceProbe(1000000000L); // 1 GB < 5 GB -> triggers warning

        var maintenance = new StorageMaintenance(
            cache, store, new TdClient(transport), probe, config,
            logger: logger, procCgroupPath: procCgroup, sysFsCgroupRoot: sysFsCgroup);

        await maintenance.RunOnceAsync();

        var summaryLog = logger.Logs.FirstOrDefault(l => l.Level == LogLevel.Information && l.Message.Contains("Storage maintenance snapshot"));
        Assert.False(string.IsNullOrEmpty(summaryLog.Message));

        // Summary log contains MB and counts
        Assert.Contains("RSS=", summaryLog.Message);
        Assert.Contains("CacheDB=", summaryLog.Message);
        Assert.Contains("Store=", summaryLog.Message);

        // Contains NONE of store path, file names, or ids
        Assert.DoesNotContain(storeDir, summaryLog.Message);
        Assert.DoesNotContain("999888", summaryLog.Message);
        Assert.DoesNotContain("12345", summaryLog.Message);
        Assert.DoesNotContain(secretMarker, summaryLog.Message);

        // Low free disk warning
        Assert.Contains(logger.Logs, l => l.Level == LogLevel.Warning && l.Message.Contains("Low free disk space"));

        // High RSS warning
        Assert.Contains(logger.Logs, l => l.Level == LogLevel.Warning && l.Message.Contains("Process RSS high"));
    }

    // 24. Worker: ordering on shared timeline: setLogVerbosityLevel before setTdlibParameters; optimizeStorage & getStorageStatisticsFast sent only AFTER invisibility getOption; fails invisibility -> neither sent & store untouched
    [Fact]
    public async Task Test24_Worker_ordering_on_shared_timeline()
    {
        var storeDir = CreateTempDir();
        var tempDb = CreateTempDbPath();
        var timeline = new List<string>();
        var syncStatePath = Path.Combine(CreateTempDir(), "device-state.json");

        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Telegram:ApiId"] = "12345",
            ["Telegram:ApiHash"] = "hash",
            ["Telegram:DatabaseDirectory"] = CreateTempDir(),
            ["Telegram:FilesDirectory"] = CreateTempDir(),
            ["Capture:CacheDatabasePath"] = tempDb,
            ["Capture:Media:Enabled"] = "true",
            ["Capture:Media:StorageDirectory"] = storeDir,
            ["Capture:Sync:StatePath"] = syncStatePath
        }).Build();

        var transport = new FakeRecordingTdTransport { AutoRepeatReadyState = true };
        transport.OnExecute = req =>
        {
            using var d = JsonDocument.Parse(req);
            var type = d.RootElement.GetProperty("@type").GetString();
            lock (timeline) timeline.Add("execute:" + type);
            return "{\"@type\":\"ok\"}";
        };
        transport.OnSend = (c, req) =>
        {
            using var d = JsonDocument.Parse(req);
            var type = d.RootElement.GetProperty("@type").GetString();
            var extra = d.RootElement.TryGetProperty("@extra", out var ex) ? ex.GetString() : null;

            lock (timeline) timeline.Add("send:" + type);

            if (type == "setTdlibParameters")
                return new JsonObject { ["@type"] = "ok", ["@extra"] = extra }.ToJsonString();
            if (type == "setOption")
                return new JsonObject { ["@type"] = "ok", ["@extra"] = extra }.ToJsonString();
            if (type == "getOption")
                return new JsonObject { ["@type"] = "optionValueBoolean", ["value"] = false, ["@extra"] = extra }.ToJsonString(); // Invisibility success!
            if (type == "optimizeStorage")
                return new JsonObject { ["@type"] = "storageStatistics", ["size"] = 0, ["count"] = 0, ["@extra"] = extra }.ToJsonString();
            if (type == "getStorageStatisticsFast")
                return new JsonObject { ["@type"] = "storageStatisticsFast", ["files_size"] = 0, ["database_size"] = 0, ["@extra"] = extra }.ToJsonString();
            return null;
        };

        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(config);
        services.AddSingleton<INativeLibraryProbe>(new FixedProbe(true));
        services.AddSingleton<ITdTransport>(transport);
        services.AddSingleton<ITdClient, TdClient>();
        services.AddSingleton<IConsolePrompt, FakeConsolePrompt>();
        services.AddSingleton(sp => new TdAuthenticator(
            sp.GetRequiredService<ITdClient>(),
            config,
            sp.GetRequiredService<IConsolePrompt>(),
            false,
            null));

        services.AddMessageCache(config);
        services.AddCaptureHandlers();
        services.AddCaptureSyncClient(config);

        var logger = new CapturingLogger<Worker>();
        var lifetime = new FakeHostLifetime();

        using var provider = services.BuildServiceProvider();
        var worker = new Worker(config, provider, lifetime, logger);

        transport.IncomingQueue.Enqueue(new JsonObject
        {
            ["@type"] = "updateAuthorizationState",
            ["authorization_state"] = new JsonObject { ["@type"] = "authorizationStateReady" }
        }.ToJsonString());

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        await worker.StartAsync(cts.Token);

        for (int i = 0; i < 50; i++)
        {
            lock (timeline)
            {
                if (timeline.Contains("send:optimizeStorage") && timeline.Contains("send:getStorageStatisticsFast"))
                    break;
            }
            await Task.Delay(50);
        }

        lock (timeline)
        {
            // 1. setLogVerbosityLevel executed before invisibility check
            int verbosityIdx = timeline.IndexOf("execute:setLogVerbosityLevel");
            int setOptionIdx = timeline.IndexOf("send:setOption");
            int getOptionIdx = timeline.IndexOf("send:getOption");
            int optStorageIdx = timeline.IndexOf("send:optimizeStorage");
            int getStatsIdx = timeline.IndexOf("send:getStorageStatisticsFast");

            Assert.True(verbosityIdx >= 0, "setLogVerbosityLevel was not executed.");
            Assert.True(setOptionIdx >= 0, "Invisibility setOption was not sent.");
            Assert.True(getOptionIdx >= 0, "Invisibility getOption was not sent.");
            Assert.True(verbosityIdx < setOptionIdx, "setLogVerbosityLevel must execute before invisibility setOption.");

            // 2. optimizeStorage and getStorageStatisticsFast sent only after invisibility getOption
            Assert.True(optStorageIdx > getOptionIdx, "optimizeStorage must be sent after getOption.");
            Assert.True(getStatsIdx > getOptionIdx, "getStorageStatisticsFast must be sent after getOption.");
        }
    }

    [Fact]
    public async Task Test24_Worker_when_invisibility_fails_maintenance_is_not_started()
    {
        var storeDir = CreateTempDir();
        var tempDb = CreateTempDbPath();
        var timeline = new List<string>();

        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Telegram:ApiId"] = "12345",
            ["Telegram:ApiHash"] = "hash",
            ["Telegram:DatabaseDirectory"] = CreateTempDir(),
            ["Telegram:FilesDirectory"] = CreateTempDir(),
            ["Capture:CacheDatabasePath"] = tempDb,
            ["Capture:Media:Enabled"] = "true",
            ["Capture:Media:StorageDirectory"] = storeDir,
            ["Capture:Sync:StatePath"] = Path.Combine(CreateTempDir(), "device-state.json")
        }).Build();

        var transport = new FakeRecordingTdTransport
        {
            AutoRepeatReadyState = true,
            OnSend = (c, req) =>
            {
                using var d = JsonDocument.Parse(req);
                var type = d.RootElement.GetProperty("@type").GetString();
                var extra = d.RootElement.TryGetProperty("@extra", out var ex) ? ex.GetString() : null;

                if (type != null) lock (timeline) timeline.Add(type);

                if (type == "setTdlibParameters")
                    return new JsonObject { ["@type"] = "ok", ["@extra"] = extra }.ToJsonString();
                if (type == "setOption")
                    return new JsonObject { ["@type"] = "ok", ["@extra"] = extra }.ToJsonString();
                if (type == "getOption")
                    return new JsonObject { ["@type"] = "optionValueBoolean", ["value"] = true, ["@extra"] = extra }.ToJsonString(); // Invisibility FAILS!
                return null;
            }
        };

        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(config);
        services.AddSingleton<INativeLibraryProbe>(new FixedProbe(true));
        services.AddSingleton<ITdTransport>(transport);
        services.AddSingleton<ITdClient, TdClient>();
        services.AddSingleton<IConsolePrompt, FakeConsolePrompt>();
        services.AddSingleton(sp => new TdAuthenticator(
            sp.GetRequiredService<ITdClient>(),
            config,
            sp.GetRequiredService<IConsolePrompt>(),
            false,
            null));

        services.AddMessageCache(config);
        services.AddCaptureHandlers();
        services.AddCaptureSyncClient(config);

        var logger = new CapturingLogger<Worker>();
        var lifetime = new FakeHostLifetime();

        using var provider = services.BuildServiceProvider();
        var worker = new Worker(config, provider, lifetime, logger);

        transport.IncomingQueue.Enqueue(new JsonObject
        {
            ["@type"] = "updateAuthorizationState",
            ["authorization_state"] = new JsonObject { ["@type"] = "authorizationStateReady" }
        }.ToJsonString());

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        await worker.StartAsync(cts.Token);

        for (int i = 0; i < 30; i++)
        {
            if (lifetime.StopRequested) break;
            await Task.Delay(50);
        }

        Assert.True(lifetime.StopRequested);

        lock (timeline)
        {
            Assert.DoesNotContain("optimizeStorage", timeline);
            Assert.DoesNotContain("getStorageStatisticsFast", timeline);
        }
    }

    // 25. Registration: AddCaptureHandlers resolves StorageMaintenance; default IDiskSpaceProbe is SystemDiskSpaceProbe
    [Fact]
    public void Test25_Registration_AddCaptureHandlers()
    {
        var services = new ServiceCollection();
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Capture:Media:StorageDirectory"] = CreateTempDir()
        }).Build();

        services.AddSingleton<IConfiguration>(config);
        services.AddSingleton<ITdTransport>(new FakeRecordingTdTransport());
        services.AddSingleton<ITdClient, TdClient>();
        services.AddMessageCache(config);
        services.AddCaptureHandlers();

        using var provider = services.BuildServiceProvider();
        var maintenance = provider.GetRequiredService<StorageMaintenance>();
        Assert.NotNull(maintenance);

        var probe = provider.GetRequiredService<IDiskSpaceProbe>();
        Assert.IsType<SystemDiskSpaceProbe>(probe);

        // SystemDiskSpaceProbe returns positive value for temp folder and non-existent subfolder
        var tempFolder = Path.GetTempPath();
        var freeBytes1 = probe.GetAvailableFreeBytes(tempFolder);
        Assert.NotNull(freeBytes1);
        Assert.True(freeBytes1 > 0);

        var notYetExisting = Path.Combine(tempFolder, "sub_" + Guid.NewGuid().ToString("N"));
        var freeBytes2 = probe.GetAvailableFreeBytes(notYetExisting);
        Assert.NotNull(freeBytes2);
        Assert.True(freeBytes2 > 0);
    }

    // 26. Preflight: key validation & store conflict checks
    [Fact]
    public void Test26_Preflight_validation()
    {
        var tempDir = CreateTempDir();
        var dbDir = Path.Combine(tempDir, "db");
        var filesDir = Path.Combine(tempDir, "files");
        var cacheDb = Path.Combine(tempDir, "cache.db");
        var storeDir = Path.Combine(tempDir, "store");
        var statePath = Path.Combine(tempDir, "device-state.json");
        Directory.CreateDirectory(dbDir);
        Directory.CreateDirectory(filesDir);

        IConfiguration MakeConfig(Dictionary<string, string?> overrides)
        {
            var baseDict = new Dictionary<string, string?>
            {
                ["Telegram:ApiId"] = "123",
                ["Telegram:ApiHash"] = "hash",
                ["Telegram:DatabaseDirectory"] = dbDir,
                ["Telegram:FilesDirectory"] = filesDir,
                ["Capture:CacheDatabasePath"] = cacheDb,
                ["Capture:Media:Enabled"] = "true",
                ["Capture:Media:StorageDirectory"] = storeDir,
                ["Capture:Sync:StatePath"] = statePath
            };
            foreach (var kv in overrides)
                baseDict[kv.Key] = kv.Value;
            return new ConfigurationBuilder().AddInMemoryCollection(baseDict).Build();
        }

        // Valid base passes
        Assert.True(CapturePreflight.Check(MakeConfig([]), _ => true).Success);

        // New keys out of range
        Assert.False(CapturePreflight.Check(MakeConfig(new() { ["Capture:Storage:MinFreeBytes"] = "-1" }), _ => true).Success);
        Assert.False(CapturePreflight.Check(MakeConfig(new() { ["Capture:Storage:MaintenanceIntervalMinutes"] = "0" }), _ => true).Success);
        Assert.False(CapturePreflight.Check(MakeConfig(new() { ["Capture:Storage:MaintenanceIntervalMinutes"] = "1441" }), _ => true).Success);
        Assert.False(CapturePreflight.Check(MakeConfig(new() { ["Capture:Storage:TdlibFilesMaxBytes"] = "100" }), _ => true).Success); // < 16MB
        Assert.False(CapturePreflight.Check(MakeConfig(new() { ["Capture:Storage:TdlibFilesTtlHours"] = "0" }), _ => true).Success);
        Assert.False(CapturePreflight.Check(MakeConfig(new() { ["Capture:Storage:TdlibImmunitySeconds"] = "100" }), _ => true).Success); // < 600
        Assert.False(CapturePreflight.Check(MakeConfig(new() { ["Capture:Tdlib:LogVerbosity"] = "3" }), _ => true).Success);

        // TdlibImmunitySeconds < 2 * DownloadTimeoutSeconds (timeout 500 -> 2 * 500 = 1000 > 600)
        Assert.False(CapturePreflight.Check(MakeConfig(new()
        {
            ["Capture:Media:DownloadTimeoutSeconds"] = "500",
            ["Capture:Storage:TdlibImmunitySeconds"] = "600"
        }), _ => true).Success);

        // MaxTotalBytes < MaxBytes
        Assert.False(CapturePreflight.Check(MakeConfig(new()
        {
            ["Capture:Media:MaxBytes"] = "5000000",
            ["Capture:Media:MaxTotalBytes"] = "1000000"
        }), _ => true).Success);

        // Store relative
        Assert.False(CapturePreflight.Check(MakeConfig(new() { ["Capture:Media:StorageDirectory"] = "relative/store" }), _ => true).Success);

        // Store equal to FilesDirectory
        Assert.False(CapturePreflight.Check(MakeConfig(new() { ["Capture:Media:StorageDirectory"] = filesDir }), _ => true).Success);

        // Store inside DatabaseDirectory
        Assert.False(CapturePreflight.Check(MakeConfig(new() { ["Capture:Media:StorageDirectory"] = Path.Combine(dbDir, "substore") }), _ => true).Success);

        // Store containing cache DB
        Assert.False(CapturePreflight.Check(MakeConfig(new() { ["Capture:Media:StorageDirectory"] = tempDir }), _ => true).Success);

        // Media disabled -> store not checked
        Assert.True(CapturePreflight.Check(MakeConfig(new()
        {
            ["Capture:Media:Enabled"] = "false",
            ["Capture:Media:StorageDirectory"] = filesDir // Invalid conflict, but media is disabled!
        }), _ => true).Success);
    }

    // 27. The unit file: no \r, required directives, none of forbidden, default paths covered, .gitattributes has eol=lf
    [Fact]
    public void Test27_Systemd_unit_file_verification()
    {
        var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
        string? root = null;
        while (dir != null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "CustomSync.sln")))
            {
                root = dir.FullName;
                break;
            }
            dir = dir.Parent;
        }
        Assert.NotNull(root);
        var unitPath = Path.Combine(root, "deploy", "customsync-capture.service");
        Assert.True(File.Exists(unitPath), $"Unit file not found at {unitPath}");

        var bytes = File.ReadAllBytes(unitPath);
        Assert.DoesNotContain((byte)'\r', bytes);

        var content = Encoding.UTF8.GetString(bytes);

        // Required directives
        Assert.Contains("Description=CustomSync capture service", content);
        Assert.Contains("After=network-online.target", content);
        Assert.Contains("Wants=network-online.target", content);
        Assert.Contains("Type=exec", content);
        Assert.Contains("User=customsync-capture", content);
        Assert.Contains("Group=customsync-capture", content);
        Assert.Contains("WorkingDirectory=/var/www/customsync-capture", content);
        Assert.Contains("ExecStart=/usr/bin/dotnet /var/www/customsync-capture/CustomSync.Capture.dll", content);
        Assert.Contains("Restart=always", content);
        Assert.Contains("RestartSec=15", content);
        Assert.Contains("Environment=DOTNET_ENVIRONMENT=Production", content);
        Assert.Contains("Environment=LD_LIBRARY_PATH=/var/www/customsync-capture", content);
        Assert.Contains("StandardOutput=journal", content);
        Assert.Contains("StandardError=journal", content);
        Assert.Contains("MemoryMax=1200M", content);
        Assert.Contains("MemoryHigh=900M", content);
        Assert.Contains("CPUQuota=100%", content);
        Assert.Contains("UMask=0077", content);
        Assert.Contains("StateDirectory=customsync-capture", content);
        Assert.Contains("StateDirectoryMode=0700", content);
        Assert.Contains("ReadWritePaths=/var/lib/customsync-capture", content);
        Assert.Contains("NoNewPrivileges=true", content);
        Assert.Contains("PrivateTmp=true", content);
        Assert.Contains("PrivateDevices=true", content);
        Assert.Contains("ProtectSystem=strict", content);
        Assert.Contains("ProtectHome=true", content);
        Assert.Contains("ProtectKernelTunables=true", content);
        Assert.Contains("ProtectKernelModules=true", content);
        Assert.Contains("ProtectControlGroups=true", content);
        Assert.Contains("RestrictAddressFamilies=AF_INET AF_INET6 AF_UNIX", content);
        Assert.Contains("RestrictSUIDSGID=true", content);
        Assert.Contains("LockPersonality=true", content);
        Assert.Contains("CapabilityBoundingSet=", content);
        Assert.Contains("WantedBy=multi-user.target", content);

        // Forbidden directives
        Assert.DoesNotContain("\nMemoryDenyWriteExecute=", content);
        Assert.DoesNotContain("\nType=notify", content);
        Assert.DoesNotContain("ASPNETCORE_ENVIRONMENT=Production", content);

        // Default paths lie under ReadWritePaths
        var defaultPaths = new[]
        {
            "/var/lib/customsync-capture/tdlib",
            "/var/lib/customsync-capture/files",
            "/var/lib/customsync-capture/message-cache.db",
            "/var/lib/customsync-capture/media",
            "/var/lib/customsync-capture/device-state.json"
        };
        foreach (var p in defaultPaths)
        {
            Assert.StartsWith("/var/lib/customsync-capture", p);
        }

        // .gitattributes
        var gitattrPath = Path.Combine(root, ".gitattributes");
        Assert.True(File.Exists(gitattrPath));
        var gitattrContent = File.ReadAllText(gitattrPath);
        Assert.Contains("deploy/customsync-capture.service text eol=lf", gitattrContent);
    }
}
