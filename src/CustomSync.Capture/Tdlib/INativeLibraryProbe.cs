using System.Runtime.InteropServices;
using CustomSync.Capture.Preflight;

namespace CustomSync.Capture.Tdlib;

public interface INativeLibraryProbe
{
    bool CanLoad(string? customPath);
}

public sealed class SystemNativeLibraryProbe : INativeLibraryProbe
{
    public bool CanLoad(string? customPath)
    {
        if (!string.IsNullOrWhiteSpace(customPath))
        {
            if (!File.Exists(customPath))
            {
                return false;
            }
            if (NativeLibrary.TryLoad(customPath, out var handle))
            {
                NativeLibrary.Free(handle);
                return true;
            }
            return false;
        }

        if (NativeLibrary.TryLoad("tdjson", typeof(CapturePreflight).Assembly, null, out var defaultHandle))
        {
            NativeLibrary.Free(defaultHandle);
            return true;
        }
        return false;
    }
}
