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

    private TimeSpan Interval => TimeSpan.FromSeconds(
        int.TryParse(_config["Capture:Sync:IntervalSeconds"], out var val) && val > 0 ? val : 30);

    /// <summary>
    /// Keyingi siklgacha kutish: odatda interval, server/tarmoq xatosidan
    /// keyin esa runner'ning o'sib boruvchi backoff'i.
    /// </summary>
    public TimeSpan NextDelay(bool lastCycleSucceeded)
        => !lastCycleSucceeded && _runner.CycleBackoffSeconds > 0
            ? TimeSpan.FromSeconds(_runner.CycleBackoffSeconds)
            : Interval;

    public virtual async Task RunLoopAsync(CancellationToken ct)
    {
        var interval = Interval;

        while (!ct.IsCancellationRequested)
        {
            try
            {
                var success = await _runner.PushCycleAsync(ct);
                await Task.Delay(NextDelay(success), _timeProvider, ct);
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
