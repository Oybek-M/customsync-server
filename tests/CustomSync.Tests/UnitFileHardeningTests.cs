using Xunit;

namespace CustomSync.Tests;

public class UnitFileHardeningTests
{
    /// <summary>
    /// Ikkala xizmat xotirasida sir bor (JWT kaliti; TDLib sessiyasi va sync
    /// master key). Qulaganda core dump ularni diskka yozardi -- 2026-10-15 da
    /// capture SIGBUS bilan qulaganda aynan shunday bo'ldi.
    /// </summary>
    [Theory]
    [InlineData("customsync.service")]
    [InlineData("customsync-capture.service")]
    public void Unit_disables_core_dumps(string unit)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "deploy", unit);
        Assert.True(File.Exists(path), $"Unit file not found at {path}");

        var lines = File.ReadAllLines(path).Select(l => l.Trim());
        Assert.Contains("LimitCORE=0", lines);
    }
}
