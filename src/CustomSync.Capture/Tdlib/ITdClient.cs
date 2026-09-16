namespace CustomSync.Capture.Tdlib;

public interface ITdClient : IDisposable
{
    int ClientId { get; }
    int PendingRequestCount { get; }
    event Action<string>? UpdateReceived;
    Task<string> SendAsync(string requestJson, TimeSpan? timeout = null, CancellationToken ct = default);
    string? Execute(string requestJson);
}
