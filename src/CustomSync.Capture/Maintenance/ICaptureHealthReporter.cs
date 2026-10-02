namespace CustomSync.Capture.Maintenance;

public interface ICaptureHealthReporter
{
    Task<bool> ReportAsync(StorageSnapshot snapshot, CancellationToken ct = default);
}

public sealed class NullCaptureHealthReporter : ICaptureHealthReporter
{
    public Task<bool> ReportAsync(StorageSnapshot snapshot, CancellationToken ct = default)
        => Task.FromResult(false);
}
