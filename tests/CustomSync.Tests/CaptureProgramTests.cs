using CustomSync.Capture;
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
}
