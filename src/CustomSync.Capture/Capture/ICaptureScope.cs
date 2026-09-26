namespace CustomSync.Capture.Capture;

/// <summary>
/// Evaluates whether a peer is within the capture scope.
/// </summary>
public interface ICaptureScope
{
    bool ShouldCapture(string peerId);
}

/// <summary>
/// Fail-closed placeholder for ICaptureScope.
/// Captures nothing until ScopeEvaluator is implemented in Task 5.
/// Registering this prevents writing private chats to VPS disk
/// before capture rules are explicitly defined.
/// </summary>
public class NoneCaptureScope : ICaptureScope
{
    public bool ShouldCapture(string peerId) => false;
}
