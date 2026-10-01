using System.Reflection;
using CustomSync.Capture;
using CustomSync.Capture.Capture;
using CustomSync.Capture.Preflight;
using CustomSync.Capture.Sync;
using CustomSync.Capture.Tdlib;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Xunit;

namespace CustomSync.Tests;

/// <summary>
/// Plan 05 Task 7 tekshiruvida qo'shilgan testlar (2026-10-01). Har biri
/// delegate to'plami ushlamagan bo'shliqni yopadi.
/// </summary>
[Collection(ProcessExitCodeCollection.Name)]
public class CaptureInvisibilityVerificationTests : IDisposable
{
    private readonly List<string> _dirs = new();

    private string CreateTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"cs-t7-verify-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        _dirs.Add(dir);
        return dir;
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        foreach (var d in _dirs)
        {
            try { Directory.Delete(d, recursive: true); } catch { }
        }
    }

    private static Dictionary<string, string?> ValidSettings(string dir) => new()
    {
        ["Telegram:ApiId"] = "12345",
        ["Telegram:ApiHash"] = "test_hash",
        ["Telegram:DatabaseDirectory"] = dir,
        ["Telegram:FilesDirectory"] = dir,
        ["Capture:CacheDatabasePath"] = Path.Combine(dir, "cache.db"),
        ["Capture:Scope:DefaultEnabled"] = "true",
    };

    private sealed class FakeLifetime : IHostApplicationLifetime
    {
        private readonly CancellationTokenSource _stopping = new();
        public bool StopRequested { get; private set; }
        public CancellationToken ApplicationStarted => CancellationToken.None;
        public CancellationToken ApplicationStopping => _stopping.Token;
        public CancellationToken ApplicationStopped => CancellationToken.None;
        public void StopApplication() { StopRequested = true; _stopping.Cancel(); }
    }

    private sealed class CapturingLogger<T> : ILogger<T>
    {
        private readonly List<string> _lines = new();
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            lock (_lines) _lines.Add(formatter(state, exception));
        }
        public string AllText { get { lock (_lines) return string.Join("\n", _lines); } }
    }

    // 15. Darvoza native chegarada ham turadi. Faqat TdClient da tursa,
    // Capture ichidagi istalgan kod `new NativeTdTransport().Send(...)`
    // deb uni chetlab o'tardi — Test11 faqat konstruktor parametrlarini
    // tekshiradi va buni ko'rmaydi.
    [Fact]
    public void Test15_Native_transport_rejects_forbidden_requests_before_any_native_call()
    {
        var transport = new NativeTdTransport();
        const string forbidden = "{\"@type\":\"viewMessages\",\"chat_id\":1,\"message_ids\":[1]}";

        // DllNotFoundException emas: rad etish P/Invoke'gacha bo'lishi shart.
        Assert.Throws<TdRequestNotAllowedException>(() => transport.Send(0, forbidden));
        Assert.Throws<TdRequestNotAllowedException>(() => transport.Execute(forbidden));
    }

    // 16. P/Invoke'ning o'zi yopiq: td_send / td_execute ga faqat darvoza
    // orqali yetib boriladi. Kimdir ularni qayta ochsa, test yiqiladi.
    [Fact]
    public void Test16_Native_send_and_execute_are_reachable_only_through_the_gate()
    {
        var interop = typeof(TdClient).Assembly.GetType("CustomSync.Capture.Tdlib.TdJsonInterop", throwOnError: true)!;
        foreach (var name in new[] { "td_send", "td_execute" })
        {
            var method = interop.GetMethod(name, BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            Assert.NotNull(method);
            Assert.True(method!.IsPrivate, $"{name} darvozasiz chaqirilishi mumkin");
        }
    }

    // 17. Haqiqiy probe xulqi. Test14 faqat turini tekshirardi, ya'ni
    // `CanLoad` doim true qaytarsa ham hamma test o'tardi.
    [Fact]
    public void Test17_Real_probe_rejects_missing_and_non_library_files()
    {
        var probe = new SystemNativeLibraryProbe();
        var dir = CreateTempDir();

        Assert.False(probe.CanLoad(Path.Combine(dir, "yoq-libtdjson.so")));

        var notALibrary = Path.Combine(dir, "matn.so");
        File.WriteAllText(notALibrary, "bu kutubxona emas");
        Assert.False(probe.CanLoad(notALibrary));

        if (OperatingSystem.IsWindows())
        {
            Assert.True(probe.CanLoad(Path.Combine(Environment.SystemDirectory, "kernel32.dll")));
        }
    }

    // 18. Sozlangan, lekin mavjud bo'lmagan TdJsonPath preflight'da
    // aniq xato berishi shart. Tekshiruvchi (probe) berilganda bu yo'l
    // xatosiz o'tib ketardi va Task 7 dan beri Worker aynan shu yo'ldan
    // yuradi.
    [Fact]
    public void Test18_Missing_configured_library_fails_preflight_with_a_checker()
    {
        var dir = CreateTempDir();
        var settings = ValidSettings(dir);
        settings["Telegram:TdJsonPath"] = Path.Combine(dir, "yoq-libtdjson.so");
        var config = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();

        var report = CapturePreflight.Check(config, nativeLibChecker: _ => false);

        Assert.False(report.Success);
        Assert.Contains(report.Errors, e => e.Contains("does not exist"));
    }

    // 19. Xuddi shu holat ishlab chiqarish yo'lida: haqiqiy ro'yxat, Worker
    // exit 1 bilan to'xtaydi va ITdClient (native P/Invoke) hech qachon
    // yaratilmaydi.
    [Fact]
    public async Task Test19_Worker_stops_cleanly_when_the_configured_library_is_missing()
    {
        var original = Environment.ExitCode;
        Environment.ExitCode = 0;
        try
        {
            var dir = CreateTempDir();
            var settings = ValidSettings(dir);
            settings["Telegram:TdJsonPath"] = Path.Combine(dir, "yoq-libtdjson.so");
            var config = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();

            var services = new ServiceCollection();
            services.AddSingleton<IConfiguration>(config);
            services.AddTdlibClient(config);
            services.AddMessageCache(config);
            services.AddCaptureHandlers();
            services.AddCaptureSyncClient(config);
            using var provider = services.BuildServiceProvider();

            var lifetime = new FakeLifetime();
            var logger = new CapturingLogger<Worker>();
            var worker = new Worker(config, provider, lifetime, logger);

            await worker.StartAsync(CancellationToken.None);
            if (worker.ExecuteTask is not null)
            {
                await Task.WhenAny(worker.ExecuteTask, Task.Delay(TimeSpan.FromSeconds(10)));
            }
            await worker.StopAsync(CancellationToken.None);

            Assert.True(worker.ExecuteTask is null || !worker.ExecuteTask.IsFaulted,
                "Worker native xato bilan yiqildi — preflight uni ushlamadi");
            Assert.True(lifetime.StopRequested, "xizmat to'xtatilmadi");
            Assert.Equal(1, Environment.ExitCode);
            Assert.Contains("does not exist", logger.AllText);
        }
        finally
        {
            Environment.ExitCode = original;
        }
    }
}
