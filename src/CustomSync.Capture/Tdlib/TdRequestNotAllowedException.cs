namespace CustomSync.Capture.Tdlib;

/// <summary>
/// Thrown when an outgoing TDLib request is rejected by <see cref="TdRequestPolicy"/>.
/// </summary>
public class TdRequestNotAllowedException : Exception
{
    public string? RequestType { get; }

    public TdRequestNotAllowedException(string message, string? requestType = null)
        : base(message)
    {
        RequestType = requestType;
    }

    public TdRequestNotAllowedException(string message, Exception innerException, string? requestType = null)
        : base(message, innerException)
    {
        RequestType = requestType;
    }
}
