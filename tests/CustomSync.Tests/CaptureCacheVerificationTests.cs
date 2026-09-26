using CustomSync.Capture;
using CustomSync.Capture.Capture;
using Microsoft.Extensions.Configuration;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CustomSync.Tests;

/// <summary>
/// Tekshiruv bosqichida qo'shilgan testlar (2026-09-26). Har biri delegate
/// to'plami ushlamagan buzilishni yopadi.
/// </summary>
public class CaptureCacheVerificationTests : IDisposable
{
    private readonly List<string> _paths = new();

    private string CreateTempDbPath()
    {
        var path = Path.Combine(Path.GetTempPath(), $"cs-cache-verify-{Guid.NewGuid():N}.db");
        _paths.Add(path);
        return path;
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        foreach (var p in _paths)
        {
            foreach (var f in new[] { p, p + "-wal", p + "-shm" })
            {
                try { if (File.Exists(f)) File.Delete(f); } catch { }
            }
        }
    }

    private sealed class CapturingLogger : ILogger
    {
        public readonly List<string> Lines = new();
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            var text = formatter(state, exception);
            if (exception is not null) text += " | " + exception.Message;
            lock (Lines) Lines.Add(text);
        }
        public string AllText { get { lock (Lines) return string.Join("\n", Lines); } }
    }

    // 16. Bir xil message_id ikki xil chatda. Bu ushlanmasa, `Get` dagi
    // `WHERE message_id = ...` (chat_id siz) boshqa chatning matnini
    // qaytaradi va Task 4 uni o'chirilgan xabar matni deb yozib qo'yadi.
    [Fact]
    public void Test16_Same_message_id_in_two_chats_stays_separate()
    {
        var cache = new MessageCache(CreateTempDbPath());
        cache.Initialize();

        cache.Put(new CachedMessage(111L, 7L, "chat_111_matni", "u1", false, false, null, 1000L));
        cache.Put(new CachedMessage(222L, 7L, "chat_222_matni", "u2", false, false, null, 1000L));

        Assert.Equal("chat_111_matni", cache.Get(111L, 7L)!.Text);
        Assert.Equal("chat_222_matni", cache.Get(222L, 7L)!.Text);

        // Uchinchi chatdagi o'sha id — hech narsa almashtirilmasligi kerak.
        Assert.Null(cache.Put(new CachedMessage(333L, 7L, "chat_333_matni", "u3", false, false, null, 1000L)));

        // Va boshqa chat qatorlari joyida.
        Assert.Equal("chat_111_matni", cache.Get(111L, 7L)!.Text);
        Assert.Equal(3, cache.Stats().RowCount);
    }

    // 17. WAL yoqilgan, ya'ni yangi yozilgan ma'lumot `-wal` faylida turadi.
    // Faqat asosiy faylni sanash diskdagi haqiqiy hajmni kam ko'rsatadi —
    // Task 9 dagi kvota shu raqamga tayanadi.
    [Fact]
    public void Test17_Stats_size_includes_the_wal_file()
    {
        var dbPath = CreateTempDbPath();
        var cache = new MessageCache(dbPath);
        cache.Initialize();

        // Ochiq ulanish WAL ni checkpoint qilib yuborishiga yo'l qo'ymaydi.
        using var holdOpen = new SqliteConnection($"Data Source={dbPath}");
        holdOpen.Open();

        for (var i = 0; i < 300; i++)
        {
            cache.Put(new CachedMessage(1L, i, new string('x', 200), "u1", false, false, null, 1000L));
        }

        var walPath = dbPath + "-wal";
        Assert.True(File.Exists(walPath), "-wal fayli yaratilmagan, test o'z shartini tekshirmayapti");
        var walLength = new FileInfo(walPath).Length;
        Assert.True(walLength > 0, "-wal bo'sh, test o'z shartini tekshirmayapti");

        var mainLength = new FileInfo(dbPath).Length;
        Assert.True(
            cache.Stats().FileSizeBytes >= mainLength + walLength,
            $"Stats {cache.Stats().FileSizeBytes} bayt deydi, lekin diskda kamida {mainLength + walLength} bayt");
    }

    // 18. Ishlab chiqarish ulanishi. Delegate testi o'zi yasagan
    // konteynerni sinardi, shuning uchun `Worker` keshni umuman ulamay
    // qo'ysa ham hamma test o'tib ketardi.
    [Fact]
    public async Task Test18_Cache_startup_fails_loudly_when_registrations_are_missing()
    {
        // Ro'yxatsiz provider -> jimgina o'tib ketmasligi kerak.
        using var empty = new ServiceCollection().BuildServiceProvider();
        Assert.Throws<InvalidOperationException>(() =>
        {
            _ = CaptureCacheStartup.Start(empty, null, CancellationToken.None);
        });

        // To'liq ro'yxat -> kesh yaratiladi va tozalash ishlaydi.
        var dbPath = CreateTempDbPath();
        var services = new ServiceCollection();
        services.AddSingleton(_ => new MessageCache(dbPath));
        services.AddSingleton(sp => new PeriodicCachePruner(
            sp.GetRequiredService<MessageCache>(),
            retentionDays: 30,
            interval: TimeSpan.FromHours(6),
            logger: null,
            delay: (_, ct) => Task.Delay(Timeout.Infinite, ct)));
        using var provider = services.BuildServiceProvider();

        using var cts = new CancellationTokenSource();
        var task = CaptureCacheStartup.Start(provider, null, cts.Token);

        Assert.True(File.Exists(dbPath), "Initialize chaqirilmagan");
        cts.Cancel();
        try { await task; } catch (OperationCanceledException) { }
    }

    // 19. Tozalash ishga tushishda ham bajariladi. Faqat intervaldan keyin
    // tozalasa, tez-tez qayta ishga tushadigan xizmatda kesh hech qachon
    // tozalanmasligi mumkin (standart interval 6 soat).
    [Fact]
    public async Task Test19_Prune_runs_at_startup_before_the_first_delay()
    {
        var order = new List<string>();
        var cache = new CountingCache(CreateTempDbPath(), () => order.Add("prune"));
        cache.Initialize();

        using var cts = new CancellationTokenSource();
        var pruner = new PeriodicCachePruner(cache, 30, TimeSpan.FromHours(6), logger: null,
            delay: (_, ct) =>
            {
                order.Add("delay");
                cts.Cancel();
                return Task.CompletedTask;
            });

        await pruner.RunLoopAsync(cts.Token);

        Assert.NotEmpty(order);
        Assert.Equal("prune", order[0]);
    }

    // 20. Kutish funksiyasi xato bersa: sikl cheksiz aylanmaydi va xato
    // jimgina yo'qolmaydi.
    [Fact]
    public async Task Test20_Delay_failure_is_logged_and_does_not_spin_forever()
    {
        var logger = new CapturingLogger();
        var cache = new CountingCache(CreateTempDbPath(), () => { });
        cache.Initialize();

        var delayCalls = 0;
        var pruner = new PeriodicCachePruner(cache, 30, TimeSpan.FromHours(6), logger,
            delay: (_, _) =>
            {
                delayCalls++;
                throw new InvalidOperationException("timer uzildi");
            });

        var loop = pruner.RunLoopAsync(CancellationToken.None);
        var finished = await Task.WhenAny(loop, Task.Delay(TimeSpan.FromSeconds(5)));

        Assert.Same(loop, finished);
        Assert.True(delayCalls <= 3, $"sikl aylanib ketdi: {delayCalls} marta");
        Assert.Contains("timer", logger.AllText, StringComparison.OrdinalIgnoreCase);
    }

    // 21. Haqiqiy ro'yxatga olish kodi (Program.cs ishlatadigan). Delegate
    // testi o'zining nusxa ro'yxatini yasagani uchun konfiguratsiya
    // kalitlari buzilsa ham hech narsa sezilmasdi.
    [Fact]
    public void Test21_Production_registration_reads_its_configuration()
    {
        var dbPath = CreateTempDbPath();
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Capture:CacheDatabasePath"] = dbPath,
            ["Capture:CacheRetentionDays"] = "1",
        }).Build();

        using var provider = new ServiceCollection().AddMessageCache(config).BuildServiceProvider();

        var cache = provider.GetRequiredService<MessageCache>();
        cache.Initialize();
        Assert.True(File.Exists(dbPath), "kesh sozlamadagi yo'lda yaratilmadi");

        // Retention = 1 kun: ikki kunlik qator ketadi, bugungisi qoladi.
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        cache.Put(new CachedMessage(1L, 1L, "eski", "u1", false, false, null, now, now - 2 * 86400));
        cache.Put(new CachedMessage(1L, 2L, "yangi", "u1", false, false, null, now, now));

        Assert.Equal(1, provider.GetRequiredService<PeriodicCachePruner>().PruneOnce());
        Assert.Null(cache.Get(1L, 1L));
        Assert.NotNull(cache.Get(1L, 2L));
    }

    // 22. Worker rostdan keshni ulaydi. Bu test bo'lmaganda ulanish
    // qatorlarini butunlay o'chirib tashlash mumkin edi va hamma test
    // o'tib ketardi.
    [Fact]
    public async Task Test22_Worker_starts_the_cache()
    {
        var dbPath = CreateTempDbPath();
        // Telegram sozlamalari ataylab yo'q: preflight yiqiladi, ya'ni
        // kesh preflight'dan oldin ulanishi shart.
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Capture:CacheDatabasePath"] = dbPath,
        }).Build();

        using var provider = new ServiceCollection().AddMessageCache(config).BuildServiceProvider();
        var lifetime = new FakeLifetime();
        var worker = new Worker(config, provider, lifetime, NullLogger<Worker>.Instance);

        await worker.StartAsync(CancellationToken.None);
        if (worker.ExecuteTask is not null)
        {
            await Task.WhenAny(worker.ExecuteTask, Task.Delay(TimeSpan.FromSeconds(10)));
        }

        Assert.True(File.Exists(dbPath), "Worker keshni ulamadi: ma'lumotlar bazasi yaratilmagan");
        Assert.True(lifetime.StopRequested, "preflight yiqilgan, lekin xizmat to'xtatilmadi");
        await worker.StopAsync(CancellationToken.None);
    }

    private sealed class FakeLifetime : IHostApplicationLifetime
    {
        private readonly CancellationTokenSource _stopping = new();
        public bool StopRequested { get; private set; }
        public CancellationToken ApplicationStarted => CancellationToken.None;
        public CancellationToken ApplicationStopping => _stopping.Token;
        public CancellationToken ApplicationStopped => CancellationToken.None;
        public void StopApplication()
        {
            StopRequested = true;
            _stopping.Cancel();
        }
    }

    private sealed class CountingCache(string dbPath, Action onPrune) : MessageCache(dbPath)
    {
        public override int Prune(int olderThanDays)
        {
            onPrune();
            return 0;
        }
    }
}
