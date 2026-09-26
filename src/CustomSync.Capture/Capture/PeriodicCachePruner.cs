using Microsoft.Extensions.Logging;

namespace CustomSync.Capture.Capture;

public class PeriodicCachePruner
{
    private readonly MessageCache _cache;
    private readonly int _retentionDays;
    private readonly TimeSpan _interval;
    private readonly ILogger? _logger;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;

    public PeriodicCachePruner(
        MessageCache cache,
        int retentionDays,
        TimeSpan interval,
        ILogger? logger = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        _cache = cache;
        _retentionDays = retentionDays;
        _interval = interval;
        _logger = logger;
        _delay = delay ?? Task.Delay;
    }

    public int PruneOnce()
    {
        try
        {
            var count = _cache.Prune(_retentionDays);
            _logger?.LogInformation("Pruned {Count} expired messages from cache (retention: {RetentionDays} days).", count, _retentionDays);
            return count;
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to prune message cache.");
            return 0;
        }
    }

    public async Task RunLoopAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            // Ishga tushishda darhol tozalaymiz. Avval intervalni kutardi,
            // ya'ni tez-tez qayta ishga tushadigan xizmatda (standart
            // interval 6 soat) kesh hech qachon tozalanmasligi mumkin edi.
            PruneOnce();

            try
            {
                await _delay(_interval, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // Kutish mexanizmi buzilgan. Jimgina davom etsak sikl
                // to'xtovsiz aylanib protsessorni yeydi, shuning uchun
                // xatoni yozib chiqib ketamiz.
                _logger?.LogError(ex, "Cache prune loop stopped: the delay failed.");
                break;
            }
        }
    }
}
