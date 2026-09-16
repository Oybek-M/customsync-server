using System.Reflection;
using System.Runtime.InteropServices;

namespace CustomSync.Capture.Tdlib;

/// <summary>
/// Direct P/Invoke to tdjson C-API.
/// All string passing is UTF-8 explicitly.
/// Native library loading can be customized via NativeLibrary.SetDllImportResolver.
/// </summary>
internal static class TdJsonInterop
{
    public const string LibraryName = "tdjson";
    private static bool _resolverRegistered;
    private static readonly object _resolverLock = new();
    private static string? _customLibraryPath;

    public static void ConfigureResolver(string? customPath = null)
    {
        lock (_resolverLock)
        {
            _customLibraryPath = customPath;
            if (!_resolverRegistered)
            {
                NativeLibrary.SetDllImportResolver(typeof(TdJsonInterop).Assembly, DllImportResolver);
                _resolverRegistered = true;
            }
        }
    }

    private static IntPtr DllImportResolver(string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
    {
        if (libraryName == LibraryName)
        {
            if (!string.IsNullOrWhiteSpace(_customLibraryPath) && File.Exists(_customLibraryPath))
            {
                if (NativeLibrary.TryLoad(_customLibraryPath, out var handle))
                {
                    return handle;
                }
            }
        }

        return IntPtr.Zero;
    }

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int td_create_client_id();

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    public static extern void td_send(int clientId, IntPtr request);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr td_receive(double timeout);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr td_execute(IntPtr request);

    public static IntPtr StringToUtf8Ptr(string str)
    {
        return Marshal.StringToCoTaskMemUTF8(str);
    }

    public static void FreeUtf8Ptr(IntPtr ptr)
    {
        if (ptr != IntPtr.Zero)
        {
            Marshal.FreeCoTaskMem(ptr);
        }
    }

    public static string? PtrToUtf8String(IntPtr ptr)
    {
        if (ptr == IntPtr.Zero) return null;
        return Marshal.PtrToStringUTF8(ptr);
    }
}
