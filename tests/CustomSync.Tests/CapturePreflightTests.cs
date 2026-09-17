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
}
