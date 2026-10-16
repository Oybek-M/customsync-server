using CustomSync.Capture.Tdlib;
using Microsoft.Extensions.Logging;
using Xunit;

namespace CustomSync.Tests;

/// <summary>
/// Dispose receive oqimini 2 soniya kutadi. Oqim shu paytda xato yo'lida
/// ushlanib qolsa (band mashina, sekin logger), Dispose CTS'ni yo'q qilardi va
/// oqim keyin `_stopCts.Token` ga tegib ObjectDisposedException olardi --
/// fon oqimidagi ushlanmagan istisno butun jarayonni (capture'ni yoki
/// testhost'ni) qulatadi.
/// </summary>
public class TdClientDisposeRaceTests
{
    private sealed class ThrowingTransport : ITdTransport
    {
        private int _calls;
        public int CreateClientId() => 1;
        public void Send(int clientId, string requestJson) { }
        public string? Receive(double timeoutSeconds)
        {
            if (Interlocked.Increment(ref _calls) == 1)
                throw new InvalidOperationException("transport failure");
            Thread.Sleep(20);
            return null;
        }
        public string? Execute(string requestJson) => null;
        public void Dispose() { }
    }

    private sealed class BlockingLogger : ILogger<TdClient>
    {
        public readonly ManualResetEventSlim Entered = new();
        public readonly ManualResetEventSlim Release = new();
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state,
            Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (logLevel < LogLevel.Error) return;
            Entered.Set();
            Release.Wait(TimeSpan.FromSeconds(10));
        }
    }

    [Fact]
    public void Dispose_while_receive_thread_is_stuck_in_error_path_does_not_crash()
    {
        var logger = new BlockingLogger();
        var client = new TdClient(new ThrowingTransport(), logger, errorBackoff: TimeSpan.FromMilliseconds(50));

        Assert.True(logger.Entered.Wait(TimeSpan.FromSeconds(5)), "receive loop never hit the error path");

        // Dispose 2 soniya kutadi va oqim hali logger ichida -- kutish tugaydi.
        client.Dispose();
        logger.Release.Set();

        // Tuzatishsiz oqim shu yerda ObjectDisposedException bilan qulab,
        // testhost jarayonini to'xtatardi.
        Thread.Sleep(500);
    }
}
