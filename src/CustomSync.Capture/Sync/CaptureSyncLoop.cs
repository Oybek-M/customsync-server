using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace CustomSync.Capture.Sync;

public class CaptureSyncLoop
{
    private readonly CaptureSyncRunner _runner;
    private readonly TimeProvider _timeProvider;
    private readonly IConfiguration _config;
    private readonly ILogger<CaptureSyncLoop>? _logger;

    public CaptureSyncLoop(
        CaptureSyncRunner runner,
        TimeProvider timeProvider,
        IConfiguration config,
        ILogger<CaptureSyncLoop>? logger = null)
    {
        _runner = runner;
        _timeProvider = timeProvider;
        _config = config;
        _logger = logger;
    }

    public virtual async Task RunLoopAsync(CancellationToken ct)
    {
        var intervalSec = int.TryParse(_config["Capture:Sync:IntervalSeconds"], out var val) && val > 0 ? val : 30;
        var interval = TimeSpan.FromSeconds(intervalSec);

        while (!ct.IsCancellationRequested)
        {
            try
            {
                var success = await _runner.PushCycleAsync(ct);
                TimeSpan delay = interval;
                if (!success && _runner.CycleBackoffSeconds > 0)
                {
                    delay = TimeSpan.FromSeconds(_runner.CycleBackoffSeconds);
                }
                await Task.Delay(delay, _timeProvider, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Unexpected error in capture sync loop.");
                await Task.Delay(interval, _timeProvider, ct);
            }
        }
    }
}
