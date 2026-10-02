using CustomSync.Capture.Maintenance;

namespace CustomSync.Capture.Sync;

public sealed class CaptureHealthReporter(CaptureSyncRunner runner) : ICaptureHealthReporter
{
    public Task<bool> ReportAsync(StorageSnapshot snapshot, CancellationToken ct = default)
        => runner.ReportHealthAsync(snapshot, ct);
}
