using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using CustomSync.Capture;
using CustomSync.Capture.Preflight;
using CustomSync.Capture.Tdlib;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace CustomSync.Tests;

/// <summary>
/// Plan 05 Task 11 tekshiruvi (TeamLead). Delegate kodida Linux'da egasi
/// yoki rejimi o'qilmasa tekshiruv jim o'tib ketardi (statx xatosida ega
/// o'rniga jarayonning o'z id'si qo'yilardi, `false` esa "tekshiradigan
/// narsa yo'q" deb qabul qilinardi), umask xatosi yutilardi, kalit
/// `$CREDENTIALS_DIRECTORY` ichidami — oddiy prefiks bilan tekshirilardi.
/// Fake "o'qib bo'lmadi" holatini Windows bilan bir xil qaytargani uchun
/// buni hech bir test ko'rmasdi.
/// </summary>
[Collection(ProcessExitCodeCollection.Name)]
public class CaptureSessionProtectionVerificationTests : IDisposable
{
    private static readonly UnixFileMode Mode0700 = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
    private static readonly UnixFileMode Mode0600 = UnixFileMode.UserRead | UnixFileMode.UserWrite;

    private readonly IPosixFileSystem _original = PosixSandbox.Current;
    private readonly string? _originalCredentials = Environment.GetEnvironmentVariable("CREDENTIALS_DIRECTORY");
    private readonly TestPosixFileSystem _fs = new() { CurrentUserId = 1000 };
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "cs-11-verify-" + Guid.NewGuid().ToString("N"));

    public CaptureSessionProtectionVerificationTests()
    {
        Directory.CreateDirectory(_dir);
        Environment.SetEnvironmentVariable("CREDENTIALS_DIRECTORY", null);
        PosixSandbox.Current = _fs;
    }

    public void Dispose()
    {
        PosixSandbox.Current = _original;
        Environment.SetEnvironmentVariable("CREDENTIALS_DIRECTORY", _originalCredentials);
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private static string KeyFile(string directory)
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "tdlib-db-key");
        File.WriteAllText(path, Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)));
        return path;
    }

    private string[] Args(string keyPath) =>
    [
        $"--Telegram:TdJsonPath={Path.Combine(_dir, "missing-tdjson.so")}",
        "--Telegram:ApiId=12345",
        "--Telegram:ApiHash=test",
        $"--Telegram:DatabaseDirectory={Path.Combine(_dir, "tdlib")}",
        $"--Telegram:FilesDirectory={Path.Combine(_dir, "files")}",
        $"--Telegram:DatabaseEncryptionKeyFile={keyPath}",
        $"--Capture:CacheDatabasePath={Path.Combine(_dir, "cache", "message-cache.db")}",
        $"--Capture:Media:StorageDirectory={Path.Combine(_dir, "media")}",
        "--Capture:Media:Enabled=false",
        "--Capture:Sync:Enabled=false",
        $"--Capture:Sync:StatePath={Path.Combine(_dir, "sync", "device-state.json")}",
        $"--Capture:Sync:MasterKeyPath={Path.Combine(_dir, "sync", "master.key")}",
    ];

    private IConfiguration Config(string keyPath) =>
        new ConfigurationBuilder().AddCommandLine(Args(keyPath)).Build();

    // Preflight tekshiradigan hamma papka "o'qildi, 0700, o'ziniki" —
    // shunda xato faqat test atayin o'qib bo'lmaydigan qilgan narsadan chiqadi.
    private void AllDirectoriesReadable(string keyPath)
    {
        foreach (var d in new[] { "tdlib", "files", "cache", "media", "sync" })
        {
            var full = Path.Combine(_dir, d);
            Directory.CreateDirectory(full);
            _fs.PathPermissions[Path.GetFullPath(full)] = (1000, Mode0700);
        }
        _fs.PathPermissions[Path.GetFullPath(keyPath)] = (1000, Mode0600);
    }

    [Fact]
    public void V01_Directory_whose_owner_or_mode_cannot_be_read_fails_preflight()
    {
        var keyPath = KeyFile(Path.Combine(_dir, "key"));
        AllDirectoriesReadable(keyPath);
        Assert.True(CapturePreflight.Check(Config(keyPath), _ => true, checkDatabaseKey: true).Success);

        _fs.PathPermissions.Remove(Path.GetFullPath(Path.Combine(_dir, "tdlib")));
        _fs.UnknownPathResult = PathPermissionProbe.Unreadable;
        var report = CapturePreflight.Check(Config(keyPath), _ => true, checkDatabaseKey: true);

        Assert.False(report.Success);
        Assert.Contains(report.Errors, e => e.Contains("Telegram:DatabaseDirectory") && e.Contains("cannot be verified"));
    }

    [Fact]
    public void V02_Current_user_id_that_cannot_be_read_fails_preflight()
    {
        var keyPath = KeyFile(Path.Combine(_dir, "key"));
        AllDirectoriesReadable(keyPath);
        _fs.GetCurrentUserIdFailure = new EntryPointNotFoundException("geteuid");

        var report = CapturePreflight.Check(Config(keyPath), _ => true, checkDatabaseKey: true);

        Assert.False(report.Success);
        Assert.Contains(report.Errors, e => e.Contains("Telegram:DatabaseDirectory") && e.Contains("cannot be verified"));
    }

    [Fact]
    public void V03_Config_file_whose_mode_cannot_be_read_fails_preflight()
    {
        var keyPath = KeyFile(Path.Combine(_dir, "key"));
        AllDirectoriesReadable(keyPath);
        var originalEnv = Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT");
        var cfgFile = Path.Combine(Directory.GetCurrentDirectory(), "appsettings.Verify11.json");
        Environment.SetEnvironmentVariable("DOTNET_ENVIRONMENT", "Verify11");
        File.WriteAllText(cfgFile, "{}");
        try
        {
            _fs.UnknownPathResult = PathPermissionProbe.Unreadable;
            var report = CapturePreflight.Check(Config(keyPath), _ => true, checkDatabaseKey: true);

            Assert.False(report.Success);
            Assert.Contains(report.Errors, e => e.Contains("appsettings.Verify11.json") && e.Contains("cannot be verified"));
        }
        finally
        {
            Environment.SetEnvironmentVariable("DOTNET_ENVIRONMENT", originalEnv);
            File.Delete(cfgFile);
        }
    }

    [Fact]
    public void V04_Key_file_whose_mode_cannot_be_read_is_refused()
    {
        var keyPath = KeyFile(Path.Combine(_dir, "key"));
        _fs.UnknownPathResult = PathPermissionProbe.Unreadable;

        var result = DatabaseEncryptionKey.LoadKey(Config(keyPath));

        Assert.False(result.Success);
        Assert.Null(result.KeyBase64);
        Assert.Contains("cannot be verified", result.Error);
    }

    [Theory]
    [InlineData("creds-evil")]
    [InlineData("creds/../outside")]
    public void V05_Key_outside_the_credentials_directory_is_checked_even_if_its_path_starts_with_it(string keyDir)
    {
        Directory.CreateDirectory(Path.Combine(_dir, "creds"));
        Environment.SetEnvironmentVariable("CREDENTIALS_DIRECTORY", Path.Combine(_dir, "creds"));
        var keyPath = KeyFile(Path.Combine(_dir, keyDir));
        _fs.PathPermissions[Path.GetFullPath(keyPath)] = (1000, Mode0600 | UnixFileMode.GroupRead);

        var result = DatabaseEncryptionKey.LoadKey(Config(keyPath));

        Assert.False(result.Success);
        Assert.Contains("Group and other bits", result.Error);
    }

    [Fact]
    public void V06_Key_inside_the_credentials_directory_keeps_its_exemption()
    {
        var credDir = Path.Combine(_dir, "creds");
        var keyPath = KeyFile(credDir);
        Environment.SetEnvironmentVariable("CREDENTIALS_DIRECTORY", credDir + Path.DirectorySeparatorChar);
        _fs.PathPermissions[Path.GetFullPath(keyPath)] = (1000, Mode0600 | UnixFileMode.GroupRead);

        var result = DatabaseEncryptionKey.LoadKey(Config(keyPath));

        Assert.True(result.Success, result.Error);
    }

    [Fact]
    public async Task V07_Umask_failure_exits_78_before_the_host_is_built()
    {
        var originalExitCode = Environment.ExitCode;
        var keyPath = KeyFile(Path.Combine(_dir, "key"));
        _fs.SetUmaskFailure = new EntryPointNotFoundException("umask");
        var hostConfigured = false;
        try
        {
            var exitCode = await CaptureProgram.RunAsync(Args(keyPath), _ => hostConfigured = true)
                .WaitAsync(TimeSpan.FromSeconds(60));

            Assert.Equal(78, exitCode);
            Assert.False(hostConfigured);
        }
        finally
        {
            Environment.ExitCode = originalExitCode;
        }
    }

    private sealed class LogSink : ILoggerProvider
    {
        public readonly List<string> Lines = new();
        public ILogger CreateLogger(string categoryName) => new Sink(this);
        public void Dispose() { }

        private sealed class Sink(LogSink owner) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                var text = formatter(state, exception) + (exception is null ? "" : " | " + exception);
                lock (owner.Lines) owner.Lines.Add(text);
            }
        }
    }

    // Delegate testi faqat 78 ni tekshirardi — AuthorizationGate har qanday
    // istisnoga 78 beradi, shuning uchun noto'g'ri kalitni alohida ushlash
    // olib tashlansa ham o'tardi. O'shanda egasi "--login qiling" degan
    // xabarni ko'radi va kalit o'rniga sessiyani "tuzatishga" urinadi.
    [Fact]
    public async Task V08_Wrong_key_answer_tells_the_owner_to_check_the_key_file()
    {
        var originalExitCode = Environment.ExitCode;
        var keyPath = KeyFile(Path.Combine(_dir, "key"));
        var key = File.ReadAllText(keyPath).Trim();
        var sink = new LogSink();
        var transport = new FakeRecordingTdTransport { AutoRepeatReadyState = true };
        transport.OnSend = (_, reqJson) =>
        {
            using var doc = JsonDocument.Parse(reqJson);
            var type = doc.RootElement.GetProperty("@type").GetString();
            var extra = doc.RootElement.TryGetProperty("@extra", out var ex) ? ex.GetString() : null;
            return type == "setTdlibParameters"
                ? new JsonObject { ["@type"] = "error", ["code"] = 401, ["message"] = "Wrong database encryption key", ["@extra"] = extra }.ToJsonString()
                : new JsonObject { ["@type"] = "ok", ["@extra"] = extra }.ToJsonString();
        };
        transport.IncomingQueue.Enqueue(new JsonObject
        {
            ["@type"] = "updateAuthorizationState",
            ["authorization_state"] = new JsonObject { ["@type"] = "authorizationStateWaitTdlibParameters" }
        }.ToJsonString());
        try
        {
            var exitCode = await CaptureProgram.RunAsync(Args(keyPath), services =>
            {
                services.AddSingleton<INativeLibraryProbe>(new FixedProbe(true));
                services.AddSingleton<ITdTransport>(transport);
                services.AddSingleton<ILoggerProvider>(sink);
            }).WaitAsync(TimeSpan.FromSeconds(30));

            Assert.Equal(78, exitCode);
            List<string> lines;
            lock (sink.Lines) lines = sink.Lines.ToList();
            Assert.Contains(lines, l => l.Contains("Check") && l.Contains("key file", StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(lines, l => l.Contains(key));
        }
        finally
        {
            Environment.ExitCode = originalExitCode;
        }
    }
}
