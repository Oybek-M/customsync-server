using System.Runtime.InteropServices;

namespace CustomSync.Capture.Tdlib;

/// <summary>
/// tdjson bilan matn almashish. P/Invoke'dan ALOHIDA turadi, shuning uchun
/// native kutubxonasiz ham sinash mumkin — bu qatlamning to'g'riligi
/// xizmatning butun qiymatiga ta'sir qiladi:
///
/// - UTF-8 majburiy. ANSI marshalling lotin bo'lmagan matnni buzadi, ya'ni
///   aynan shu xizmat ushlab qolish uchun yaratilgan xabarlarni.
/// - `td_receive` qaytargan xotira TDLib'niki va faqat o'sha oqimdagi
///   keyingi `td_receive` gacha yaroqli — satr darhol nusxalanadi.
/// </summary>
public static class TdMarshal
{
    public static IntPtr StringToUtf8Ptr(string str) => Marshal.StringToCoTaskMemUTF8(str);

    public static void FreeUtf8Ptr(IntPtr ptr)
    {
        if (ptr != IntPtr.Zero)
        {
            Marshal.FreeCoTaskMem(ptr);
        }
    }

    public static string? PtrToUtf8String(IntPtr ptr)
        => ptr == IntPtr.Zero ? null : Marshal.PtrToStringUTF8(ptr);
}
