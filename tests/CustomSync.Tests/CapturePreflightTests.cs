using CustomSync.Capture.Preflight;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace CustomSync.Tests;

public class CapturePreflightTests
{
    [Fact]
    public void Test14_Missing_native_library_path_returns_specific_message_no_unhandled_exception()
    {
        var nonExistentPath = Path.Combine(Path.GetTempPath(), $"non_existent_tdlib_{Guid.NewGuid():N}.so");
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Telegram:TdJsonPath"] = nonExistentPath,
                ["Telegram:ApiId"] = "12345",
                ["Telegram:ApiHash"] = "some_fake_hash",
                ["Telegram:DatabaseDirectory"] = Path.GetTempPath(),
                ["Telegram:FilesDirectory"] = Path.GetTempPath()
            })
            .Build();

        var report = CapturePreflight.Check(config);

        Assert.False(report.Success);
        Assert.Contains(report.Errors, e => e.Contains("does not exist") && e.Contains(nonExistentPath));
    }

    [Fact]
    public void Test15_Missing_ApiId_or_ApiHash_reports_specific_message_without_leaking_values()
    {
        var fakeHash = "fake_secret_hash_not_to_leak";
        var configMissingId = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Telegram:ApiId"] = "0", // invalid / missing
                ["Telegram:ApiHash"] = fakeHash,
                ["Telegram:DatabaseDirectory"] = Path.GetTempPath(),
                ["Telegram:FilesDirectory"] = Path.GetTempPath()
            })
            .Build();

        var report1 = CapturePreflight.Check(configMissingId, nativeLibChecker: _ => true);
        Assert.False(report1.Success);
        Assert.Contains(report1.Errors, e => e.Contains("Telegram:ApiId"));
        // Assert fakeHash is NEVER leaked in error messages
        Assert.DoesNotContain(report1.Errors, e => e.Contains(fakeHash));

        var configMissingHash = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Telegram:ApiId"] = "12345",
                ["Telegram:ApiHash"] = "", // missing
                ["Telegram:DatabaseDirectory"] = Path.GetTempPath(),
                ["Telegram:FilesDirectory"] = Path.GetTempPath()
            })
            .Build();

        var report2 = CapturePreflight.Check(configMissingHash, nativeLibChecker: _ => true);
        Assert.False(report2.Success);
        Assert.Contains(report2.Errors, e => e.Contains("Telegram:ApiHash"));
    }

    [Fact]
    public void Test17_Media_configuration_validation()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"preflight-media-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            Dictionary<string, string?> BaseConfig() => new()
            {
                ["Telegram:ApiId"] = "12345",
                ["Telegram:ApiHash"] = "some_fake_hash_12345",
                ["Telegram:DatabaseDirectory"] = tempDir,
                ["Telegram:FilesDirectory"] = tempDir,
                ["Capture:CacheDatabasePath"] = Path.Combine(tempDir, "cache.db")
            };

            // 1. Valid defaults (no media keys set)
            var configDefaults = new ConfigurationBuilder().AddInMemoryCollection(BaseConfig()).Build();
            var reportDefaults = CapturePreflight.Check(configDefaults, nativeLibChecker: _ => true);
            Assert.True(reportDefaults.Success);
            Assert.Empty(reportDefaults.Errors);

            // 2. Invalid Capture:Media:Enabled
            var cfgBadEnabled = new ConfigurationBuilder().AddInMemoryCollection(BaseConfig()).AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Capture:Media:Enabled"] = "notabool"
            }).Build();
            var repBadEnabled = CapturePreflight.Check(cfgBadEnabled, nativeLibChecker: _ => true);
            Assert.False(repBadEnabled.Success);
            Assert.Contains(repBadEnabled.Errors, e => e.Contains("Capture:Media:Enabled"));

            // 3. Invalid Capture:Media:PeerIds (-100... or non-canonical)
            var cfgBadPeer1 = new ConfigurationBuilder().AddInMemoryCollection(BaseConfig()).AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Capture:Media:PeerIds"] = "-10012345678"
            }).Build();
            var repBadPeer1 = CapturePreflight.Check(cfgBadPeer1, nativeLibChecker: _ => true);
            Assert.False(repBadPeer1.Success);
            Assert.Contains(repBadPeer1.Errors, e => e.Contains("Capture:Media:PeerIds"));

            var cfgBadPeer2 = new ConfigurationBuilder().AddInMemoryCollection(BaseConfig()).AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Capture:Media:PeerIds"] = "0123"
            }).Build();
            var repBadPeer2 = CapturePreflight.Check(cfgBadPeer2, nativeLibChecker: _ => true);
            Assert.False(repBadPeer2.Success);
            Assert.Contains(repBadPeer2.Errors, e => e.Contains("Capture:Media:PeerIds"));

            // 4. Invalid Capture:Media:MaxBytes (0, negative, > 26214400)
            foreach (var badMb in new[] { "0", "-1", "26214401", "abc" })
            {
                var cfg = new ConfigurationBuilder().AddInMemoryCollection(BaseConfig()).AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Capture:Media:MaxBytes"] = badMb
                }).Build();
                var rep = CapturePreflight.Check(cfg, nativeLibChecker: _ => true);
                Assert.False(rep.Success);
                Assert.Contains(rep.Errors, e => e.Contains("Capture:Media:MaxBytes"));
            }

            // 5. Invalid Capture:Media:DownloadTimeoutSeconds (<= 0 or not int)
            foreach (var badTimeout in new[] { "0", "-5", "abc" })
            {
                var cfg = new ConfigurationBuilder().AddInMemoryCollection(BaseConfig()).AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Capture:Media:DownloadTimeoutSeconds"] = badTimeout
                }).Build();
                var rep = CapturePreflight.Check(cfg, nativeLibChecker: _ => true);
                Assert.False(rep.Success);
                Assert.Contains(rep.Errors, e => e.Contains("Capture:Media:DownloadTimeoutSeconds"));
            }

            // 6. Invalid Capture:Media:MaxAttempts (<= 0 or not int)
            foreach (var badAttempts in new[] { "0", "-1", "abc" })
            {
                var cfg = new ConfigurationBuilder().AddInMemoryCollection(BaseConfig()).AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Capture:Media:MaxAttempts"] = badAttempts
                }).Build();
                var rep = CapturePreflight.Check(cfg, nativeLibChecker: _ => true);
                Assert.False(rep.Success);
                Assert.Contains(rep.Errors, e => e.Contains("Capture:Media:MaxAttempts"));
            }

            // 7. Valid full configuration
            var cfgValid = new ConfigurationBuilder().AddInMemoryCollection(BaseConfig()).AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Capture:Media:Enabled"] = "true",
                ["Capture:Media:PeerIds"] = "12345,67890",
                ["Capture:Media:MaxBytes"] = "26214400",
                ["Capture:Media:DownloadTimeoutSeconds"] = "60",
                ["Capture:Media:MaxAttempts"] = "3"
            }).Build();
            var repValid = CapturePreflight.Check(cfgValid, nativeLibChecker: _ => true);
            Assert.True(repValid.Success);
            Assert.Empty(repValid.Errors);
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                try { Directory.Delete(tempDir, recursive: true); } catch { }
            }
        }
    }
}
