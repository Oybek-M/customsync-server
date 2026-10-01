using CustomSync.Capture.Media;

namespace CustomSync.Tests;

/// <summary>
/// Testlar uchun bo'sh joy o'lchagichi. <c>MediaDownloader</c> ning har bir
/// konstruktori o'lchagichni talab qiladi: avval production kodida "joy
/// yetarli" deb javob beradigan soxta o'lchagichli konstruktor bor edi va u
/// disk himoyasini chetlab o'tadigan ikkinchi eshik edi.
/// </summary>
internal sealed class FixedDiskSpaceProbe(long? freeBytes) : IDiskSpaceProbe
{
    public static FixedDiskSpaceProbe Ample() => new(100L * 1024 * 1024 * 1024);

    public long? GetAvailableFreeBytes(string path) => freeBytes;
}
