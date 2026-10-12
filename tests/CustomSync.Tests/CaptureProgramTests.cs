using System.Text.Json;
using System.Text.Json.Nodes;
using CustomSync.Capture;
using CustomSync.Capture.Preflight;
using CustomSync.Capture.Tdlib;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace CustomSync.Tests;

/// <summary>
/// `Worker` xatoda `Environment.ExitCode` ni qo'yadi, lekin kirish nuqtasi
/// `int` qaytaradi — bunday `Main` uchun runtime `Environment.ExitCode` ni
/// e'tiborsiz qoldiradi (hujjatda shunday yozilgan). Ya'ni jarayon har
/// qanday xatoda ham systemd'ga 0 qaytarardi. Shu sabab chiqish kodi
/// `Worker` darajasida emas, kirish nuqtasi qaytargan qiymatda tekshiriladi.
/// </summary>
[Collection(ProcessExitCodeCollection.Name)]
public class CaptureProgramTests
{
    private class CrashingProbe : INativeLibraryProbe
    {
        public bool CanLoad(string? path) => throw new InvalidOperationException("Probe failed unexpectedly before first await.");
    }

    private static string[] BuildValidArgs(string tempDir, string keyPath) =>
    [
        $"--Telegram:ApiId=12345",
        $"--Telegram:ApiHash=test_api_hash_abc",
        $"--Telegram:TdJsonPath={Path.Combine(tempDir, "tdjson.so")}",
        $"--Telegram:DatabaseDirectory={Path.Combine(tempDir, "tdlib")}",
        $"--Telegram:FilesDirectory={Path.Combine(tempDir, "files")}",
        $"--Telegram:DatabaseEncryptionKeyFile={keyPath}",
        $"--Capture:CacheDatabasePath={Path.Combine(tempDir, "message-cache.db")}",
        $"--Capture:Media:StorageDirectory={Path.Combine(tempDir, "media")}",
        "--Capture:Media:Enabled=false",
        "--Capture:Sync:Enabled=false",
        $"--Capture:Sync:StatePath={Path.Combine(tempDir, "device-state.json")}",
        $"--Capture:Sync:MasterKeyPath={Path.Combine(tempDir, "master.key")}",
        "--Capture:SessionInvisibilityTimeoutSeconds=5",
        "--Capture:Scope:DefaultEnabled=true"
    ];

    [Fact]
    public async Task Test01_Failed_preflight_is_the_exit_code_the_process_returns()
    {
        var originalExitCode = Environment.ExitCode;
        Environment.ExitCode = 0;
        var tempDir = Path.Combine(Path.GetTempPath(), "cs-program-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            // Hamma yo'l temp ichida: preflight papkalarni yaratadi, kesh esa
            // preflight'dan oldin ochiladi. TDLib yo'li mavjud bo'lmagan fayl —
            // preflight shu yerda to'xtaydi, native kutubxona yuklanmaydi.
            string[] args =
            [
                $"--Telegram:TdJsonPath={Path.Combine(tempDir, "missing-tdjson.so")}",
                $"--Telegram:DatabaseDirectory={Path.Combine(tempDir, "tdlib")}",
                $"--Telegram:FilesDirectory={Path.Combine(tempDir, "files")}",
                $"--Capture:CacheDatabasePath={Path.Combine(tempDir, "message-cache.db")}",
                $"--Capture:Media:StorageDirectory={Path.Combine(tempDir, "media")}",
                "--Capture:Media:Enabled=false",
                "--Capture:Sync:Enabled=false",
                $"--Capture:Sync:StatePath={Path.Combine(tempDir, "device-state.json")}",
                $"--Capture:Sync:MasterKeyPath={Path.Combine(tempDir, "master.key")}",
            ];

            var exitCode = await CaptureProgram.RunAsync(args).WaitAsync(TimeSpan.FromSeconds(60));

            Assert.Equal(78, exitCode);
        }
        finally
        {
            Environment.ExitCode = originalExitCode;
            try { Directory.Delete(tempDir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    [Fact]
    public async Task Test02_Unexpected_exception_before_first_await_exits_with_1()
    {
        var originalExitCode = Environment.ExitCode;
        Environment.ExitCode = 0;
        var tempDir = Path.Combine(Path.GetTempPath(), "cs-program-pre-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            var keyPath = Path.Combine(tempDir, "key.txt");
            File.WriteAllText(keyPath, Convert.ToBase64String(new byte[32]));
            var args = BuildValidArgs(tempDir, keyPath);

            // .NET 10 da `Worker` dan chiqqan istisnoni xostning o'zi ushlaydi va
            // jarayon 0 bilan tugaydi; `Worker` uni 1 ga aylantiradi. Har qanday
            // istisnoni "o'tdi" deb qabul qilish test tayyorgarligidagi xatoni ham
            // yashirardi — shuning uchun aniq kod tekshiriladi.
            var exitCode = await CaptureProgram.RunAsync(args, services =>
            {
                services.AddSingleton<INativeLibraryProbe>(new CrashingProbe());
            }).WaitAsync(TimeSpan.FromSeconds(10));

            Assert.Equal(1, exitCode);
        }
        finally
        {
            Environment.ExitCode = originalExitCode;
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    private class CrashingProfilePhotoLookup : CustomSync.Capture.Capture.ProfilePhotoLookup
    {
        public CrashingProfilePhotoLookup(CustomSync.Capture.Capture.MessageCache cache)
            : base(null, cache, null, null)
        {
        }

        public override void Start(CancellationToken ct = default)
        {
            throw new InvalidOperationException("Crashing after startup in ProfilePhotoLookup.");
        }
    }

    [Fact]
    public async Task Test03_Unexpected_exception_after_startup_exits_with_1()
    {
        var originalExitCode = Environment.ExitCode;
        Environment.ExitCode = 0;
        var tempDir = Path.Combine(Path.GetTempPath(), "cs-program-post-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            var keyPath = Path.Combine(tempDir, "key.txt");
            File.WriteAllText(keyPath, Convert.ToBase64String(new byte[32]));
            var args = BuildValidArgs(tempDir, keyPath);

            var transport = new FakeRecordingTdTransport { AutoRepeatReadyState = true };
            transport.OnSend = (clientId, reqJson) =>
            {
                using var doc = JsonDocument.Parse(reqJson);
                var root = doc.RootElement;
                var type = root.GetProperty("@type").GetString();
                var extra = root.TryGetProperty("@extra", out var ex) ? ex.GetString() : null;

                if (type == "setOption")
                    return new JsonObject { ["@type"] = "ok", ["@extra"] = extra }.ToJsonString();
                if (type == "getOption")
                    return new JsonObject { ["@type"] = "optionValueBoolean", ["value"] = false, ["@extra"] = extra }.ToJsonString();

                return new JsonObject { ["@type"] = "ok", ["@extra"] = extra }.ToJsonString();
            };

            transport.IncomingQueue.Enqueue(new JsonObject
            {
                ["@type"] = "updateAuthorizationState",
                ["authorization_state"] = new JsonObject { ["@type"] = "authorizationStateReady" }
            }.ToJsonString());

            var exitCode = await CaptureProgram.RunAsync(args, services =>
            {
                services.AddSingleton<INativeLibraryProbe>(new FixedProbe(true));
                services.AddSingleton<ITdTransport>(transport);
                services.AddSingleton<CustomSync.Capture.Capture.ProfilePhotoLookup>(sp =>
                    new CrashingProfilePhotoLookup(sp.GetRequiredService<CustomSync.Capture.Capture.MessageCache>()));
            }).WaitAsync(TimeSpan.FromSeconds(10));

            Assert.Equal(1, exitCode);
        }
        finally
        {
            Environment.ExitCode = originalExitCode;
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task Test04_Normal_shutdown_returns_zero()
    {
        var originalExitCode = Environment.ExitCode;
        Environment.ExitCode = 0;
        var tempDir = Path.Combine(Path.GetTempPath(), "cs-program-normal-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            var keyPath = Path.Combine(tempDir, "key.txt");
            File.WriteAllText(keyPath, Convert.ToBase64String(new byte[32]));
            var args = BuildValidArgs(tempDir, keyPath);

            var transport = new FakeRecordingTdTransport { AutoRepeatReadyState = true };
            transport.OnSend = (clientId, reqJson) =>
            {
                using var doc = JsonDocument.Parse(reqJson);
                var root = doc.RootElement;
                var type = root.GetProperty("@type").GetString();
                var extra = root.TryGetProperty("@extra", out var ex) ? ex.GetString() : null;

                if (type == "setOption")
                    return new JsonObject { ["@type"] = "ok", ["@extra"] = extra }.ToJsonString();
                if (type == "getOption")
                    return new JsonObject { ["@type"] = "optionValueBoolean", ["value"] = false, ["@extra"] = extra }.ToJsonString();

                return new JsonObject { ["@type"] = "ok", ["@extra"] = extra }.ToJsonString();
            };

            transport.IncomingQueue.Enqueue(new JsonObject
            {
                ["@type"] = "updateAuthorizationState",
                ["authorization_state"] = new JsonObject { ["@type"] = "authorizationStateReady" }
            }.ToJsonString());

            var exitCode = await CaptureProgram.RunAsync(args, services =>
            {
                services.AddSingleton<INativeLibraryProbe>(new FixedProbe(true));
                services.AddSingleton<ITdTransport>(transport);
                services.AddHostedService<StoppingService>();
            }).WaitAsync(TimeSpan.FromSeconds(10));

            Assert.Equal(0, exitCode);
        }
        finally
        {
            Environment.ExitCode = originalExitCode;
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    private class StoppingService(IHostApplicationLifetime lifetime) : BackgroundService
    {
        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            await Task.Delay(500, stoppingToken);
            lifetime.StopApplication();
        }
    }
}
