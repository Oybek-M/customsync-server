namespace CustomSync.Capture.Tdlib;

public interface ITdTransport : IDisposable
{
    int CreateClientId();
    void Send(int clientId, string requestJson);
    string? Receive(double timeoutSeconds);
    string? Execute(string requestJson);
}
