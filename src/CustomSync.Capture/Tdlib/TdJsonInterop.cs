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

    // td_send va td_execute PRIVATE: native kodga so'rov faqat pastdagi
    // Send/Execute orqali, ya'ni TdRequestPolicy darvozasidan o'tib yetadi.
    // Darvoza faqat TdClient da tursa, Capture ichidagi istalgan kod
    // `new NativeTdTransport().Send(...)` deb uni chetlab o'tardi.
    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    private static extern void td_send(int clientId, IntPtr request);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr td_receive(double timeout);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr td_execute(IntPtr request);

    /// <summary>So'rovni darvozadan o'tkazib, keyin native td_send ga beradi.</summary>
    public static void Send(int clientId, string requestJson)
    {
        // Rad etish P/Invoke'dan OLDIN: ruxsatsiz so'rov native kodga yetmaydi.
        TdRequestPolicy.ValidateAndNormalize(requestJson);
        var ptr = StringToUtf8Ptr(requestJson);
        try
        {
            td_send(clientId, ptr);
        }
        finally
        {
            FreeUtf8Ptr(ptr);
        }
    }

    /// <summary>So'rovni darvozadan o'tkazib, keyin native td_execute ga beradi.</summary>
    public static string? Execute(string requestJson)
    {
        TdRequestPolicy.ValidateAndNormalize(requestJson);
        var ptr = StringToUtf8Ptr(requestJson);
        try
        {
            return PtrToUtf8String(td_execute(ptr));
        }
        finally
        {
            FreeUtf8Ptr(ptr);
        }
    }

    // Marshalling TdMarshal'da: u P/Invoke'siz, ya'ni native kutubxonasiz
    // sinaladi. Bu yerda faqat qayta yo'naltirish qoladi.
    public static IntPtr StringToUtf8Ptr(string str) => TdMarshal.StringToUtf8Ptr(str);

    public static void FreeUtf8Ptr(IntPtr ptr) => TdMarshal.FreeUtf8Ptr(ptr);

    public static string? PtrToUtf8String(IntPtr ptr) => TdMarshal.PtrToUtf8String(ptr);
}
