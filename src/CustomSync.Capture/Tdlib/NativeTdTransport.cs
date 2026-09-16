namespace CustomSync.Capture.Tdlib;

/// <summary>
/// ITdTransport implementation that calls tdjson native methods.
/// Safely copies UTF-8 strings and frees unmanaged memory in finally blocks.
/// </summary>
public class NativeTdTransport : ITdTransport
{
    public int CreateClientId()
    {
        return TdJsonInterop.td_create_client_id();
    }

    public void Send(int clientId, string requestJson)
    {
        var ptr = TdJsonInterop.StringToUtf8Ptr(requestJson);
        try
        {
            TdJsonInterop.td_send(clientId, ptr);
        }
        finally
        {
            TdJsonInterop.FreeUtf8Ptr(ptr);
        }
    }

    public string? Receive(double timeoutSeconds)
    {
        var ptr = TdJsonInterop.td_receive(timeoutSeconds);
        // Note: td_receive returns memory owned by TDLib, valid only until next td_receive on this thread.
        // We copy the string out immediately before anything else!
        return TdJsonInterop.PtrToUtf8String(ptr);
    }

    public string? Execute(string requestJson)
    {
        var ptr = TdJsonInterop.StringToUtf8Ptr(requestJson);
        try
        {
            var resPtr = TdJsonInterop.td_execute(ptr);
            return TdJsonInterop.PtrToUtf8String(resPtr);
        }
        finally
        {
            TdJsonInterop.FreeUtf8Ptr(ptr);
        }
    }

    public void Dispose()
    {
        GC.SuppressFinalize(this);
    }
}
