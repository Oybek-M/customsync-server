namespace CustomSync.Capture.Tdlib;

public class TdException(int code, string message) : Exception($"[{code}] {message}")
{
    public int Code { get; } = code;
    public string TdMessage { get; } = message;
}
