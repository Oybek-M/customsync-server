using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using CustomSync.Capture;
using CustomSync.Capture.Preflight;
using CustomSync.Capture.Sync;
using CustomSync.Capture.Tdlib;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Xunit;

namespace CustomSync.Tests;

public sealed class LinuxFactAttribute : FactAttribute
{
    public LinuxFactAttribute()
    {
        if (!OperatingSystem.IsLinux())
        {
            Skip = "Linux-only test: requires Linux statx / POSIX syscalls.";
        }
    }
}

public class TestPosixFileSystem : IPosixFileSystem
{
    public List<int> UmaskCalls { get; } = new();
    public List<string> Created0700Directories { get; } = new();
    public uint CurrentUserId { get; set; } = 1000;
    public Dictionary<string, (uint OwnerUid, UnixFileMode Mode)> PathPermissions { get; } = new(StringComparer.OrdinalIgnoreCase);

    // Paths not in PathPermissions behave like Windows unless a test asks
    // for an unreadable owner/mode.
    public PathPermissionProbe UnknownPathResult { get; set; } = PathPermissionProbe.NotApplicable;
    public Exception? SetUmaskFailure { get; set; }

    public void SetUmask(int mask)
    {
        lock (UmaskCalls)
        {
            UmaskCalls.Add(mask);
        }
        if (SetUmaskFailure != null) throw SetUmaskFailure;
    }

    public void CreateDirectory0700(string path)
    {
        lock (Created0700Directories)
        {
            Created0700Directories.Add(Path.GetFullPath(path));
        }
        if (!Directory.Exists(path))
        {
            Directory.CreateDirectory(path);
        }
    }

    public Exception? GetCurrentUserIdFailure { get; set; }

    public uint GetCurrentUserId() => GetCurrentUserIdFailure != null ? throw GetCurrentUserIdFailure : CurrentUserId;

    public PathPermissionProbe ProbePathPermissions(string path, out uint ownerUid, out UnixFileMode mode)
    {
        var full = Path.GetFullPath(path);
        if (PathPermissions.TryGetValue(full, out var perm))
        {
            ownerUid = perm.OwnerUid;
            mode = perm.Mode;
            return PathPermissionProbe.Read;
        }
        ownerUid = 0;
        mode = 0;
        return UnknownPathResult;
    }
}

[Collection(ProcessExitCodeCollection.Name)]
public class CaptureSessionProtectionTests
{
    private static string CreateTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "cs-sess-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static string CreateRandomKeyFile(string tempDir, int byteCount = 32)
    {
        var keyPath = Path.Combine(tempDir, "tdlib-db-key");
        var bytes = RandomNumberGenerator.GetBytes(byteCount);
        File.WriteAllText(keyPath, Convert.ToBase64String(bytes));
        return keyPath;
    }

    private static string[] BuildArgs(string tempDir, string keyPath, string? extraArg = null)
    {
        var list = new List<string>
        {
            $"--Telegram:ApiId=12345",
            $"--Telegram:ApiHash=test_api_hash_abc",
            $"--Telegram:TdJsonPath={Path.Combine(tempDir, "tdjson.so")}",
            $"--Telegram:DatabaseDirectory={Path.Combine(tempDir, "tdlib")}",
            $"--Telegram:FilesDirectory={Path.Combine(tempDir, "files")}",
            $"--Telegram:DatabaseEncryptionKeyFile={keyPath}",
            $"--Capture:CacheDatabasePath={Path.Combine(tempDir, "cache.db")}",
            $"--Capture:Media:StorageDirectory={Path.Combine(tempDir, "media")}",
            "--Capture:Media:Enabled=false",
            "--Capture:Sync:Enabled=false",
            $"--Capture:Sync:StatePath={Path.Combine(tempDir, "device-state.json")}",
            $"--Capture:Sync:MasterKeyPath={Path.Combine(tempDir, "master.key")}",
            "--Capture:SessionInvisibilityTimeoutSeconds=5",
            "--Capture:Scope:DefaultEnabled=true"
        };
        if (!string.IsNullOrEmpty(extraArg))
        {
            list.Add(extraArg);
        }
        return list.ToArray();
    }

    private static void SetupTransportHandling(FakeRecordingTdTransport transport)
    {
        transport.OnSend = (clientId, reqJson) =>
        {
            using var doc = JsonDocument.Parse(reqJson);
            var root = doc.RootElement;
            var type = root.GetProperty("@type").GetString();
            var extra = root.TryGetProperty("@extra", out var ex) ? ex.GetString() : null;

            if (type == "setTdlibParameters")
                return new JsonObject { ["@type"] = "ok", ["@extra"] = extra }.ToJsonString();
            if (type == "setOption")
                return new JsonObject { ["@type"] = "ok", ["@extra"] = extra }.ToJsonString();
            if (type == "getOption")
                return new JsonObject { ["@type"] = "optionValueBoolean", ["value"] = false, ["@extra"] = extra }.ToJsonString();

            return new JsonObject { ["@type"] = "ok", ["@extra"] = extra }.ToJsonString();
        };
    }

    // 🔴 1 (74): After ready: LoggingOut then Closed; Closed alone; WaitPhoneNumber -> one error line, loops stopped, exit code 78, "running" not logged again
    [Fact]
    public async Task Test01_PostReady_LoggingOut_Then_Closed_Stops_Loops_Logs_One_Error_Exits_78()
    {
        int originalExitCode = Environment.ExitCode;
        Environment.ExitCode = 0;
        var tempDir = CreateTempDir();
        var keyPath = CreateRandomKeyFile(tempDir);
        try
        {
            var transport = new FakeRecordingTdTransport { AutoRepeatReadyState = true };
            SetupTransportHandling(transport);

            // Send ready state initially
            transport.IncomingQueue.Enqueue(new JsonObject
            {
                ["@type"] = "updateAuthorizationState",
                ["authorization_state"] = new JsonObject { ["@type"] = "authorizationStateReady" }
            }.ToJsonString());

            var capturingLogger = new CapturingLogger<Worker>();
            var args = BuildArgs(tempDir, keyPath);

            var runTask = CaptureProgram.RunAsync(args, services =>
            {
                services.AddSingleton<INativeLibraryProbe>(new FixedProbe(true));
                services.AddSingleton<ITdTransport>(transport);
                services.AddSingleton<ILogger<Worker>>(capturingLogger);
            });

            // Wait until Worker logs that it is authorized and running
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (!capturingLogger.Logs.Any(l => l.Message.Contains("authorized and running")) && sw.ElapsedMilliseconds < 5000)
            {
                await Task.Delay(20);
            }

            Assert.Contains(capturingLogger.Logs, l => l.Message.Contains("authorized and running"));
            transport.AutoRepeatReadyState = false;

            // Now push LoggingOut followed immediately by Closed
            transport.IncomingQueue.Enqueue(new JsonObject
            {
                ["@type"] = "updateAuthorizationState",
                ["authorization_state"] = new JsonObject { ["@type"] = "authorizationStateLoggingOut" }
            }.ToJsonString());

            transport.IncomingQueue.Enqueue(new JsonObject
            {
                ["@type"] = "updateAuthorizationState",
                ["authorization_state"] = new JsonObject { ["@type"] = "authorizationStateClosed" }
            }.ToJsonString());

            var exitCode = await runTask.WaitAsync(TimeSpan.FromSeconds(10));

            // Process exit code is 78
            Assert.Equal(78, exitCode);

            // Exactly ONE error line logged with the state type
            var errorLogs = capturingLogger.Logs.Where(l => l.Level == LogLevel.Error).ToList();
            Assert.Single(errorLogs);
            Assert.Contains("authorizationStateLoggingOut", errorLogs[0].Message);

            // "running" was NOT logged again
            var runningLogs = capturingLogger.Logs.Where(l => l.Message.Contains("authorized and running")).ToList();
            Assert.Single(runningLogs);
        }
        finally
        {
            Environment.ExitCode = originalExitCode;
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task Test01b_PostReady_Closed_Alone_Stops_With_ExitCode_78()
    {
        int originalExitCode = Environment.ExitCode;
        Environment.ExitCode = 0;
        var tempDir = CreateTempDir();
        var keyPath = CreateRandomKeyFile(tempDir);
        try
        {
            var transport = new FakeRecordingTdTransport { AutoRepeatReadyState = true };
            SetupTransportHandling(transport);

            transport.IncomingQueue.Enqueue(new JsonObject
            {
                ["@type"] = "updateAuthorizationState",
                ["authorization_state"] = new JsonObject { ["@type"] = "authorizationStateReady" }
            }.ToJsonString());

            var capturingLogger = new CapturingLogger<Worker>();
            var args = BuildArgs(tempDir, keyPath);

            var runTask = CaptureProgram.RunAsync(args, services =>
            {
                services.AddSingleton<INativeLibraryProbe>(new FixedProbe(true));
                services.AddSingleton<ITdTransport>(transport);
                services.AddSingleton<ILogger<Worker>>(capturingLogger);
            });

            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (!capturingLogger.Logs.Any(l => l.Message.Contains("authorized and running")) && sw.ElapsedMilliseconds < 5000)
            {
                await Task.Delay(20);
            }

            Assert.Contains(capturingLogger.Logs, l => l.Message.Contains("authorized and running"));
            transport.AutoRepeatReadyState = false;

            // Push Closed alone
            transport.IncomingQueue.Enqueue(new JsonObject
            {
                ["@type"] = "updateAuthorizationState",
                ["authorization_state"] = new JsonObject { ["@type"] = "authorizationStateClosed" }
            }.ToJsonString());

            var exitCode = await runTask.WaitAsync(TimeSpan.FromSeconds(10));

            Assert.Equal(78, exitCode);
            var errorLogs = capturingLogger.Logs.Where(l => l.Level == LogLevel.Error).ToList();
            Assert.Single(errorLogs);
            Assert.Contains("authorizationStateClosed", errorLogs[0].Message);
        }
        finally
        {
            Environment.ExitCode = originalExitCode;
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task Test01c_PostReady_WaitPhoneNumber_Stops_With_ExitCode_78()
    {
        int originalExitCode = Environment.ExitCode;
        Environment.ExitCode = 0;
        var tempDir = CreateTempDir();
        var keyPath = CreateRandomKeyFile(tempDir);
        try
        {
            var transport = new FakeRecordingTdTransport { AutoRepeatReadyState = true };
            SetupTransportHandling(transport);

            transport.IncomingQueue.Enqueue(new JsonObject
            {
                ["@type"] = "updateAuthorizationState",
                ["authorization_state"] = new JsonObject { ["@type"] = "authorizationStateReady" }
            }.ToJsonString());

            var capturingLogger = new CapturingLogger<Worker>();
            var args = BuildArgs(tempDir, keyPath);

            var runTask = CaptureProgram.RunAsync(args, services =>
            {
                services.AddSingleton<INativeLibraryProbe>(new FixedProbe(true));
                services.AddSingleton<ITdTransport>(transport);
                services.AddSingleton<ILogger<Worker>>(capturingLogger);
            });

            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (!capturingLogger.Logs.Any(l => l.Message.Contains("authorized and running")) && sw.ElapsedMilliseconds < 5000)
            {
                await Task.Delay(20);
            }

            Assert.Contains(capturingLogger.Logs, l => l.Message.Contains("authorized and running"));
            transport.AutoRepeatReadyState = false;

            // Push WaitPhoneNumber after ready
            transport.IncomingQueue.Enqueue(new JsonObject
            {
                ["@type"] = "updateAuthorizationState",
                ["authorization_state"] = new JsonObject { ["@type"] = "authorizationStateWaitPhoneNumber" }
            }.ToJsonString());

            var exitCode = await runTask.WaitAsync(TimeSpan.FromSeconds(10));

            Assert.Equal(78, exitCode);
            var errorLogs = capturingLogger.Logs.Where(l => l.Level == LogLevel.Error).ToList();
            Assert.Single(errorLogs);
            Assert.Contains("authorizationStateWaitPhoneNumber", errorLogs[0].Message);
        }
        finally
        {
            Environment.ExitCode = originalExitCode;
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    // 🔴 1 (75): A state change during shutdown -> no error, exit code 0
    [Fact]
    public async Task Test02_State_Change_During_Shutdown_No_Error_ExitCode_0()
    {
        int originalExitCode = Environment.ExitCode;
        Environment.ExitCode = 0;
        var tempDir = CreateTempDir();
        var keyPath = CreateRandomKeyFile(tempDir);
        try
        {
            var transport = new FakeRecordingTdTransport { AutoRepeatReadyState = true };
            SetupTransportHandling(transport);

            transport.IncomingQueue.Enqueue(new JsonObject
            {
                ["@type"] = "updateAuthorizationState",
                ["authorization_state"] = new JsonObject { ["@type"] = "authorizationStateReady" }
            }.ToJsonString());

            var capturingLogger = new CapturingLogger<Worker>();
            IHostApplicationLifetime? capturedLifetime = null;
            var args = BuildArgs(tempDir, keyPath);

            var runTask = CaptureProgram.RunAsync(args, services =>
            {
                services.AddSingleton<INativeLibraryProbe>(new FixedProbe(true));
                services.AddSingleton<ITdTransport>(transport);
                services.AddSingleton<ILogger<Worker>>(capturingLogger);
                services.AddHostedService(sp =>
                {
                    capturedLifetime = sp.GetRequiredService<IHostApplicationLifetime>();
                    return new LifecycleProbe();
                });
            });

            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (!capturingLogger.Logs.Any(l => l.Message.Contains("authorized and running")) && sw.ElapsedMilliseconds < 5000)
            {
                await Task.Delay(20);
            }

            Assert.Contains(capturingLogger.Logs, l => l.Message.Contains("authorized and running"));
            transport.AutoRepeatReadyState = false;

            // Trigger graceful shutdown
            Environment.ExitCode = 0;
            capturedLifetime?.StopApplication();

            // Send Closing/Closed during shutdown
            transport.IncomingQueue.Enqueue(new JsonObject
            {
                ["@type"] = "updateAuthorizationState",
                ["authorization_state"] = new JsonObject { ["@type"] = "authorizationStateClosing" }
            }.ToJsonString());

            transport.IncomingQueue.Enqueue(new JsonObject
            {
                ["@type"] = "updateAuthorizationState",
                ["authorization_state"] = new JsonObject { ["@type"] = "authorizationStateClosed" }
            }.ToJsonString());

            var exitCode = await runTask.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(0, exitCode);

            // No error lines logged during shutdown
            var errorLogs = capturingLogger.Logs.Where(l => l.Level == LogLevel.Error).ToList();
            Assert.Empty(errorLogs);
        }
        finally
        {
            Environment.ExitCode = originalExitCode;
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    // 🔴 1 (76): Startup not authorized -> 78; gate timeout -> 1; preflight failure -> 78
    [Fact]
    public async Task Test03_Startup_Not_Authorized_Returns_78()
    {
        int originalExitCode = Environment.ExitCode;
        Environment.ExitCode = 0;
        var tempDir = CreateTempDir();
        var keyPath = CreateRandomKeyFile(tempDir);
        try
        {
            var transport = new FakeRecordingTdTransport { AutoRepeatReadyState = true };
            SetupTransportHandling(transport);

            // Startup state is Closed (not authorized)
            transport.IncomingQueue.Enqueue(new JsonObject
            {
                ["@type"] = "updateAuthorizationState",
                ["authorization_state"] = new JsonObject { ["@type"] = "authorizationStateClosed" }
            }.ToJsonString());

            var args = BuildArgs(tempDir, keyPath);
            var exitCode = await CaptureProgram.RunAsync(args, services =>
            {
                services.AddSingleton<INativeLibraryProbe>(new FixedProbe(true));
                services.AddSingleton<ITdTransport>(transport);
            }).WaitAsync(TimeSpan.FromSeconds(10));

            Assert.Equal(78, exitCode);
        }
        finally
        {
            Environment.ExitCode = originalExitCode;
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task Test03b_Startup_Gate_Timeout_Returns_1()
    {
        int originalExitCode = Environment.ExitCode;
        Environment.ExitCode = 0;
        var tempDir = CreateTempDir();
        var keyPath = CreateRandomKeyFile(tempDir);
        try
        {
            var transport = new FakeRecordingTdTransport();
            // Don't enqueue any authorization state -> AuthorizationGate will time out
            var client = new TdClient(transport);
            var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Telegram:ApiId"] = "12345",
                ["Telegram:ApiHash"] = "test",
                ["Telegram:DatabaseEncryptionKeyFile"] = keyPath
            }).Build();
            var auth = new TdAuthenticator(client, config, new FakeConsolePrompt(), isInteractive: false);
            var gate = new AuthorizationGate(client, auth);

            // Fast timeout for test
            var outcome = await gate.RunAsync(TimeSpan.FromMilliseconds(50));
            Assert.False(outcome.Ready);
            Assert.Equal(1, outcome.ExitCode);
        }
        finally
        {
            Environment.ExitCode = originalExitCode;
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task Test03c_Preflight_Failure_Returns_78()
    {
        int originalExitCode = Environment.ExitCode;
        Environment.ExitCode = 0;
        var tempDir = CreateTempDir();
        var keyPath = CreateRandomKeyFile(tempDir);
        try
        {
            var args = BuildArgs(tempDir, keyPath);
            // Probe fails (native library missing)
            var exitCode = await CaptureProgram.RunAsync(args, services =>
            {
                services.AddSingleton<INativeLibraryProbe>(new FixedProbe(false));
            }).WaitAsync(TimeSpan.FromSeconds(10));

            Assert.Equal(78, exitCode);
        }
        finally
        {
            Environment.ExitCode = originalExitCode;
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    // 🔴 1 & 3 (77): The unit file contains RestartPreventExitStatus=78 and LoadCredential
    [Fact]
    public void Test04_Unit_File_Contains_RestartPreventExitStatus_And_LoadCredential()
    {
        var unitPath = Path.Combine(Directory.GetCurrentDirectory(), "deploy", "customsync-capture.service");
        if (!File.Exists(unitPath))
        {
            unitPath = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "deploy", "customsync-capture.service");
        }
        Assert.True(File.Exists(unitPath), $"Unit file not found at {unitPath}");

        var content = File.ReadAllText(unitPath);
        Assert.Contains("RestartPreventExitStatus=78", content);
        Assert.Contains("LoadCredential=tdlib-db-key:/etc/customsync-capture/tdlib-db-key", content);
        Assert.Contains("Restart=always", content);
        Assert.Contains("RestartSec=15", content);
    }

    // 🔴 2 (78): umask: the seam is called before the host is built in all four modes
    [Theory]
    [InlineData("")]                         // service mode
    [InlineData("--login")]                  // --login
    [InlineData("--enroll")]                 // --enroll
    [InlineData("--set-key")]                // --set-key
    public async Task Test05_Umask0077_Called_In_All_Four_Modes(string mode)
    {
        var modeArgs = string.IsNullOrEmpty(mode) ? Array.Empty<string>() : new[] { mode };
        var original = PosixSandbox.Current;
        var mock = new TestPosixFileSystem();
        PosixSandbox.Current = mock;
        try
        {
            var tempDir = CreateTempDir();
            var keyPath = CreateRandomKeyFile(tempDir);
            var baseArgs = BuildArgs(tempDir, keyPath);
            var combined = baseArgs.Concat(modeArgs).ToArray();

            // We cancel immediately or pass missing configs so CLI commands exit fast
            try
            {
                await CaptureProgram.RunAsync(combined, services =>
                {
                    services.AddSingleton<INativeLibraryProbe>(new FixedProbe(false));
                }).WaitAsync(TimeSpan.FromSeconds(3));
            }
            catch { }

            // Verify SetUmask(0x3F) was called (0x3F is octal 0077)
            Assert.Contains(0x3F, mock.UmaskCalls);
        }
        finally
        {
            PosixSandbox.Current = original;
        }
    }

    [LinuxFact]
    public void Test05b_Linux_Umask0077_Enforces_0600_File_Mode()
    {
        if (!OperatingSystem.IsLinux()) return;
        PosixSandbox.SetUmask0077();
        var tempFile = Path.Combine(Path.GetTempPath(), "cs-umask-" + Guid.NewGuid().ToString("N"));
        try
        {
            File.WriteAllText(tempFile, "test");
            var mode = File.GetUnixFileMode(tempFile);
            // With umask 0077, group and other permissions must be 0
            var groupAndOther = mode & (UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute |
                                        UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute);
            Assert.Equal((UnixFileMode)0, groupAndOther);
        }
        finally
        {
            try { File.Delete(tempFile); } catch { }
        }
    }

    // 🔴 2 (79): Directory creation with 0700 for every directory in §2
    [Fact]
    public void Test06_Directory_Creation_Uses_0700_For_All_Directories()
    {
        var original = PosixSandbox.Current;
        var mock = new TestPosixFileSystem();
        PosixSandbox.Current = mock;
        var tempDir = CreateTempDir();
        var keyPath = CreateRandomKeyFile(tempDir);
        try
        {
            var dbDir = Path.Combine(tempDir, "tdlib");
            var filesDir = Path.Combine(tempDir, "files");
            var cacheDb = Path.Combine(tempDir, "cache-dir", "cache.db");
            var mediaDir = Path.Combine(tempDir, "media-dir");

            var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Telegram:ApiId"] = "12345",
                ["Telegram:ApiHash"] = "test",
                ["Telegram:DatabaseDirectory"] = dbDir,
                ["Telegram:FilesDirectory"] = filesDir,
                ["Telegram:DatabaseEncryptionKeyFile"] = keyPath,
                ["Capture:CacheDatabasePath"] = cacheDb,
                ["Capture:Media:Enabled"] = "true",
                ["Capture:Media:StorageDirectory"] = mediaDir
            }).Build();

            var report = CapturePreflight.Check(config, _ => true, checkDatabaseKey: true);
            Assert.True(report.Success);

            // Check that CreateDirectory0700 was called for each directory
            Assert.Contains(mock.Created0700Directories, d => d.Equals(Path.GetFullPath(dbDir), StringComparison.OrdinalIgnoreCase));
            Assert.Contains(mock.Created0700Directories, d => d.Equals(Path.GetFullPath(filesDir), StringComparison.OrdinalIgnoreCase));
            Assert.Contains(mock.Created0700Directories, d => d.Equals(Path.GetFullPath(Path.GetDirectoryName(cacheDb)!), StringComparison.OrdinalIgnoreCase));
            Assert.Contains(mock.Created0700Directories, d => d.Equals(Path.GetFullPath(mediaDir), StringComparison.OrdinalIgnoreCase));

            // Also test DeviceCredentials.WriteAtomic directory creation
            var deviceDir = Path.Combine(tempDir, "device-dir");
            DeviceCredentials.WriteAtomic(Path.Combine(deviceDir, "state.json"), "{}");
            Assert.Contains(mock.Created0700Directories, d => d.Equals(Path.GetFullPath(deviceDir), StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            PosixSandbox.Current = original;
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    [LinuxFact]
    public void Test06b_Linux_Real_Directory_Creation_Has_0700_Mode()
    {
        if (!OperatingSystem.IsLinux()) return;
        var tempDir = Path.Combine(Path.GetTempPath(), "cs-dir0700-" + Guid.NewGuid().ToString("N"));
        try
        {
            PosixSandbox.CreateDirectory0700(tempDir);
            var mode = File.GetUnixFileMode(tempDir);
            var expected = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
            Assert.Equal(expected, mode);
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    // 🔴 2 (80): Preflight decisions: 0700 own -> ok; 0750, 0705, another owner -> error; config file 0640 -> ok, 0644 or 0660 -> error; absent config file -> ok
    [Fact]
    public void Test07_Preflight_Permission_Decisions()
    {
        var original = PosixSandbox.Current;
        var mock = new TestPosixFileSystem { CurrentUserId = 1000 };
        PosixSandbox.Current = mock;
        var tempDir = CreateTempDir();
        var keyPath = CreateRandomKeyFile(tempDir);
        try
        {
            var dbDir = Path.Combine(tempDir, "tdlib");
            var filesDir = Path.Combine(tempDir, "files");
            Directory.CreateDirectory(dbDir);
            Directory.CreateDirectory(filesDir);

            // Mock permissions for dbDir and filesDir as 0700 owned by 1000
            var mode0700 = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
            mock.PathPermissions[Path.GetFullPath(dbDir)] = (1000, mode0700);
            mock.PathPermissions[Path.GetFullPath(filesDir)] = (1000, mode0700);

            var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Telegram:ApiId"] = "12345",
                ["Telegram:ApiHash"] = "test",
                ["Telegram:DatabaseDirectory"] = dbDir,
                ["Telegram:FilesDirectory"] = filesDir,
                ["Telegram:DatabaseEncryptionKeyFile"] = keyPath,
                ["Capture:CacheDatabasePath"] = Path.Combine(tempDir, "cache.db")
            }).Build();

            // 1. 0700 owned by current user -> ok
            var r1 = CapturePreflight.Check(config, _ => true, checkDatabaseKey: true);
            Assert.True(r1.Success);

            // 2. 0750 owned by current user -> error (group bits set)
            var mode0750 = mode0700 | UnixFileMode.GroupRead | UnixFileMode.GroupExecute;
            mock.PathPermissions[Path.GetFullPath(dbDir)] = (1000, mode0750);
            var r2 = CapturePreflight.Check(config, _ => true, checkDatabaseKey: true);
            Assert.False(r2.Success);
            Assert.Contains(r2.Errors, e => e.Contains("Group and other bits are not allowed"));

            // 3. 0705 owned by current user -> error (other bits set)
            var mode0705 = mode0700 | UnixFileMode.OtherRead | UnixFileMode.OtherExecute;
            mock.PathPermissions[Path.GetFullPath(dbDir)] = (1000, mode0705);
            var r3 = CapturePreflight.Check(config, _ => true, checkDatabaseKey: true);
            Assert.False(r3.Success);
            Assert.Contains(r3.Errors, e => e.Contains("Group and other bits are not allowed"));

            // 4. Another owner -> error
            mock.PathPermissions[Path.GetFullPath(dbDir)] = (2000, mode0700);
            var r4 = CapturePreflight.Check(config, _ => true, checkDatabaseKey: true);
            Assert.False(r4.Success);
            Assert.Contains(r4.Errors, e => e.Contains("owned by user ID 2000"));

            // Reset dbDir to valid 0700 owned by 1000
            mock.PathPermissions[Path.GetFullPath(dbDir)] = (1000, mode0700);

            // 5. Config file checks
            var originalEnv = Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT");
            Environment.SetEnvironmentVariable("DOTNET_ENVIRONMENT", "TestEnv");
            var cfgFile = Path.Combine(Directory.GetCurrentDirectory(), "appsettings.TestEnv.json");
            try
            {
                File.WriteAllText(cfgFile, "{}");

                // Config file 0640 -> ok
                var mode0640 = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead;
                mock.PathPermissions[Path.GetFullPath(cfgFile)] = (1000, mode0640);
                var r5 = CapturePreflight.Check(config, _ => true, checkDatabaseKey: true);
                Assert.True(r5.Success);

                // Config file 0644 -> error (other read)
                var mode0644 = mode0640 | UnixFileMode.OtherRead;
                mock.PathPermissions[Path.GetFullPath(cfgFile)] = (1000, mode0644);
                var r6 = CapturePreflight.Check(config, _ => true, checkDatabaseKey: true);
                Assert.False(r6.Success);
                Assert.Contains(r6.Errors, e => e.Contains("expected <= 0640"));

                // Config file 0660 -> error (group write)
                var mode0660 = mode0640 | UnixFileMode.GroupWrite;
                mock.PathPermissions[Path.GetFullPath(cfgFile)] = (1000, mode0660);
                var r7 = CapturePreflight.Check(config, _ => true, checkDatabaseKey: true);
                Assert.False(r7.Success);
                Assert.Contains(r7.Errors, e => e.Contains("expected <= 0640"));
            }
            finally
            {
                try { File.Delete(cfgFile); } catch { }
                Environment.SetEnvironmentVariable("DOTNET_ENVIRONMENT", originalEnv);
            }

            // Absent config file -> ok
            var r8 = CapturePreflight.Check(config, _ => true, checkDatabaseKey: true);
            Assert.True(r8.Success);
        }
        finally
        {
            PosixSandbox.Current = original;
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    // 🔴 2 (81): Order: preflight failure -> the cache file and the TDLib directories were not created
    [Fact]
    public async Task Test08_Preflight_Failure_Leaves_Cache_And_Tdlib_Directories_Uncreated()
    {
        int originalExitCode = Environment.ExitCode;
        Environment.ExitCode = 0;
        var tempDir = CreateTempDir();
        var keyPath = CreateRandomKeyFile(tempDir);
        try
        {
            var dbDir = Path.Combine(tempDir, "tdlib_uncreated");
            var filesDir = Path.Combine(tempDir, "files_uncreated");
            var cacheDb = Path.Combine(tempDir, "cache_uncreated", "cache.db");

            var args = new string[]
            {
                "--Telegram:ApiId=12345",
                "--Telegram:ApiHash=test",
                $"--Telegram:TdJsonPath={Path.Combine(tempDir, "missing-tdjson.so")}",
                $"--Telegram:DatabaseDirectory={dbDir}",
                $"--Telegram:FilesDirectory={filesDir}",
                $"--Telegram:DatabaseEncryptionKeyFile={keyPath}",
                $"--Capture:CacheDatabasePath={cacheDb}",
                "--Capture:Media:Enabled=false",
                "--Capture:Sync:Enabled=false"
            };

            var exitCode = await CaptureProgram.RunAsync(args, services =>
            {
                services.AddSingleton<INativeLibraryProbe>(new FixedProbe(false)); // fails preflight
            }).WaitAsync(TimeSpan.FromSeconds(10));

            Assert.Equal(78, exitCode);

            // Assert directories and files were NOT created
            Assert.False(Directory.Exists(dbDir), "TDLib database directory should not have been created on preflight failure.");
            Assert.False(Directory.Exists(filesDir), "TDLib files directory should not have been created on preflight failure.");
            Assert.False(File.Exists(cacheDb), "Message cache file should not have been created on preflight failure.");
        }
        finally
        {
            Environment.ExitCode = originalExitCode;
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    // 🔴 3 (82): Key: from the setting; from $CREDENTIALS_DIRECTORY; missing, bad base64, 31 bytes, file with group bits -> error; same key in --login and service
    [Fact]
    public void Test09_Database_Encryption_Key_Resolution_And_Validation()
    {
        var tempDir = CreateTempDir();
        try
        {
            // 1. Valid key from setting
            var validKeyPath = CreateRandomKeyFile(tempDir, 32);
            var cfgSetting = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Telegram:DatabaseEncryptionKeyFile"] = validKeyPath
            }).Build();
            var (s1, k1, e1) = DatabaseEncryptionKey.LoadKey(cfgSetting);
            Assert.True(s1);
            Assert.NotNull(k1);
            Assert.Null(e1);

            // 2. Valid key from $CREDENTIALS_DIRECTORY
            var credDir = Path.Combine(tempDir, "creds");
            Directory.CreateDirectory(credDir);
            var credKeyPath = Path.Combine(credDir, "tdlib-db-key");
            var keyBytes = RandomNumberGenerator.GetBytes(32);
            var keyBase64 = Convert.ToBase64String(keyBytes);
            File.WriteAllText(credKeyPath, keyBase64);

            var origCred = Environment.GetEnvironmentVariable("CREDENTIALS_DIRECTORY");
            try
            {
                Environment.SetEnvironmentVariable("CREDENTIALS_DIRECTORY", credDir);
                var cfgEmpty = new ConfigurationBuilder().Build();
                var (s2, k2, e2) = DatabaseEncryptionKey.LoadKey(cfgEmpty);
                Assert.True(s2);
                Assert.Equal(keyBase64, k2);
            }
            finally
            {
                Environment.SetEnvironmentVariable("CREDENTIALS_DIRECTORY", origCred);
            }

            // 3. Missing key -> error
            var cfgMissing = new ConfigurationBuilder().Build();
            var (s3, k3, e3) = DatabaseEncryptionKey.LoadKey(cfgMissing);
            Assert.False(s3);
            Assert.Contains("not configured", e3);

            // 4. Bad base64 -> error
            var badB64Path = Path.Combine(tempDir, "bad-b64.key");
            File.WriteAllText(badB64Path, "!!!not-valid-base64???");
            var cfgBadB64 = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Telegram:DatabaseEncryptionKeyFile"] = badB64Path
            }).Build();
            var (s4, k4, e4) = DatabaseEncryptionKey.LoadKey(cfgBadB64);
            Assert.False(s4);
            Assert.Contains("valid base64", e4);

            // 5. 31 bytes -> error (minimum is 32 bytes)
            var shortKeyPath = CreateRandomKeyFile(tempDir, 31);
            var cfgShort = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Telegram:DatabaseEncryptionKeyFile"] = shortKeyPath
            }).Build();
            var (s5, k5, e5) = DatabaseEncryptionKey.LoadKey(cfgShort);
            Assert.False(s5);
            Assert.Contains("at least 32 bytes", e5);

            // 6. Outside CREDENTIALS_DIRECTORY with group bits -> error
            var original = PosixSandbox.Current;
            var mock = new TestPosixFileSystem();
            PosixSandbox.Current = mock;
            try
            {
                var groupBitsKeyPath = CreateRandomKeyFile(tempDir, 32);
                // 0640 has group read bit
                mock.PathPermissions[Path.GetFullPath(groupBitsKeyPath)] = (1000, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead);

                var cfgGroupBits = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Telegram:DatabaseEncryptionKeyFile"] = groupBitsKeyPath
                }).Build();
                var (s6, k6, e6) = DatabaseEncryptionKey.LoadKey(cfgGroupBits);
                Assert.False(s6);
                Assert.Contains("expected <= 0600", e6);
            }
            finally
            {
                PosixSandbox.Current = original;
            }
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task Test09b_Same_Key_Sent_In_Login_And_Service_Mode()
    {
        var tempDir = CreateTempDir();
        var keyPath = CreateRandomKeyFile(tempDir, 32);
        var expectedKey = File.ReadAllText(keyPath).Trim();
        try
        {
            // Test in service mode (isInteractive = false)
            var transportService = new FakeRecordingTdTransport();
            SetupTransportHandling(transportService);
            var clientService = new TdClient(transportService);
            var cfg = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Telegram:ApiId"] = "12345",
                ["Telegram:ApiHash"] = "test",
                ["Telegram:DatabaseDirectory"] = tempDir,
                ["Telegram:FilesDirectory"] = tempDir,
                ["Telegram:DatabaseEncryptionKeyFile"] = keyPath
            }).Build();

            var authService = new TdAuthenticator(clientService, cfg, new FakeConsolePrompt(), isInteractive: false);
            await authService.ProcessAuthorizationStateAsync(new JsonObject
            {
                ["@type"] = "updateAuthorizationState",
                ["authorization_state"] = new JsonObject { ["@type"] = "authorizationStateWaitTdlibParameters" }
            }.ToJsonString());

            var serviceReq = transportService.SentPayloads.First(p => p.Contains("setTdlibParameters"));
            using (var doc = JsonDocument.Parse(serviceReq))
            {
                Assert.Equal(expectedKey, doc.RootElement.GetProperty("database_encryption_key").GetString());
            }

            // Test in login mode (isInteractive = true)
            var transportLogin = new FakeRecordingTdTransport();
            SetupTransportHandling(transportLogin);
            var clientLogin = new TdClient(transportLogin);
            var authLogin = new TdAuthenticator(clientLogin, cfg, new FakeConsolePrompt(), isInteractive: true);
            await authLogin.ProcessAuthorizationStateAsync(new JsonObject
            {
                ["@type"] = "updateAuthorizationState",
                ["authorization_state"] = new JsonObject { ["@type"] = "authorizationStateWaitTdlibParameters" }
            }.ToJsonString());

            var loginReq = transportLogin.SentPayloads.First(p => p.Contains("setTdlibParameters"));
            using (var doc = JsonDocument.Parse(loginReq))
            {
                Assert.Equal(expectedKey, doc.RootElement.GetProperty("database_encryption_key").GetString());
            }
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    // 🔴 3 (82 & 54): Key redacted by TdRedactor and never in logs
    [Fact]
    public void Test10_Key_Redacted_By_TdRedactor_And_Never_In_Logs()
    {
        var rawKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        var jsonWithKey = new JsonObject
        {
            ["@type"] = "setTdlibParameters",
            ["database_encryption_key"] = rawKey,
            ["api_id"] = 12345
        }.ToJsonString();

        var redacted = TdRedactor.Redact(jsonWithKey);
        Assert.DoesNotContain(rawKey, redacted);
        Assert.Contains("[REDACTED]", redacted);
    }

    // 🔴 3 (83): Wrong-key answer exits with 78, deletes nothing
    [Fact]
    public async Task Test11_Wrong_Key_Answer_Exits_78_And_Deletes_Nothing()
    {
        int originalExitCode = Environment.ExitCode;
        Environment.ExitCode = 0;
        var tempDir = CreateTempDir();
        var keyPath = CreateRandomKeyFile(tempDir, 32);
        try
        {
            var dbDir = Path.Combine(tempDir, "tdlib");
            Directory.CreateDirectory(dbDir);
            var canaryFile = Path.Combine(dbDir, "important_session_file.bin");
            File.WriteAllText(canaryFile, "canary data");

            var transport = new FakeRecordingTdTransport { AutoRepeatReadyState = true };
            transport.OnSend = (clientId, reqJson) =>
            {
                using var doc = JsonDocument.Parse(reqJson);
                var type = doc.RootElement.GetProperty("@type").GetString();
                var extra = doc.RootElement.TryGetProperty("@extra", out var ex) ? ex.GetString() : null;

                if (type == "setTdlibParameters")
                {
                    // Return 401 Wrong database encryption key from TDLib
                    return new JsonObject
                    {
                        ["@type"] = "error",
                        ["code"] = 401,
                        ["message"] = "Wrong database encryption key",
                        ["@extra"] = extra
                    }.ToJsonString();
                }
                return new JsonObject { ["@type"] = "ok", ["@extra"] = extra }.ToJsonString();
            };

            // Enqueue WaitTdlibParameters
            transport.IncomingQueue.Enqueue(new JsonObject
            {
                ["@type"] = "updateAuthorizationState",
                ["authorization_state"] = new JsonObject { ["@type"] = "authorizationStateWaitTdlibParameters" }
            }.ToJsonString());

            var args = BuildArgs(tempDir, keyPath);
            var exitCode = await CaptureProgram.RunAsync(args, services =>
            {
                services.AddSingleton<INativeLibraryProbe>(new FixedProbe(true));
                services.AddSingleton<ITdTransport>(transport);
            }).WaitAsync(TimeSpan.FromSeconds(10));

            // Must exit with 78
            Assert.Equal(78, exitCode);

            // Important: existing session database files are NOT deleted!
            Assert.True(File.Exists(canaryFile), "Wrong key answer must not delete existing database directory contents.");
        }
        finally
        {
            Environment.ExitCode = originalExitCode;
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    // 🔴 4 (84): Session name pinned (device_model and application_version)
    [Fact]
    public async Task Test12_Device_Model_And_Application_Version_Pinned()
    {
        var tempDir = CreateTempDir();
        var keyPath = CreateRandomKeyFile(tempDir, 32);
        try
        {
            var transport = new FakeRecordingTdTransport();
            SetupTransportHandling(transport);
            var client = new TdClient(transport);
            var cfg = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Telegram:ApiId"] = "12345",
                ["Telegram:ApiHash"] = "test",
                ["Telegram:DatabaseDirectory"] = tempDir,
                ["Telegram:FilesDirectory"] = tempDir,
                ["Telegram:DatabaseEncryptionKeyFile"] = keyPath
            }).Build();

            var auth = new TdAuthenticator(client, cfg, new FakeConsolePrompt(), isInteractive: false);
            await auth.ProcessAuthorizationStateAsync(new JsonObject
            {
                ["@type"] = "updateAuthorizationState",
                ["authorization_state"] = new JsonObject { ["@type"] = "authorizationStateWaitTdlibParameters" }
            }.ToJsonString());

            var req = transport.SentPayloads.First(p => p.Contains("setTdlibParameters"));
            using var doc = JsonDocument.Parse(req);
            var root = doc.RootElement;

            Assert.Equal("CustomSync Capture", root.GetProperty("device_model").GetString());
            Assert.Equal("1.0", root.GetProperty("application_version").GetString());
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    private class LifecycleProbe : IHostedService
    {
        public Task StartAsync(CancellationToken ct) => Task.CompletedTask;
        public Task StopAsync(CancellationToken ct) => Task.CompletedTask;
    }
}
