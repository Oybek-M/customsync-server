namespace CustomSync.Services.Storage.Targets;

public sealed record ArchiveUploadResult(
    bool Success,
    string? Location,      // target ichidagi manzil (kalit, yo'l, message_id)
    string? Sha256,        // target o'qib tasdiqlagan checksum (kichik harflarda)
    string? Error);

/// <summary>
/// Arxiv manzili interfeysi. Har bir target yozish va qayta o'qishni bajara olishi shart.
/// Tekshirilmagan zaxiradan keyin hech narsa o'chirilmaydi.
/// </summary>
public interface IArchiveTarget
{
    string TargetId { get; }
    string DisplayName { get; }

    /// <summary>
    /// Checksum tekshiruvi YETARLI emasmi. Qo'lda yuklab olishda arxiv
    /// serverning o'z diskida qoladi, ya'ni uni qayta o'qish "zaxira
    /// xavfsiz joyda" degani EMAS -- buni faqat odam tasdiqlaydi.
    /// </summary>
    bool RequiresExplicitConfirmation { get; }

    /// <summary>Sozlamalar to'g'ri va manzil yetib bo'ladiganmi.</summary>
    Task<bool> HealthCheckAsync(CancellationToken ct = default);

    Task<ArchiveUploadResult> UploadAsync(
        string archiveName, Stream content, CancellationToken ct = default);

    /// <summary>
    /// Tekshiruv uchun qayta o'qish. null qaytarsa — tekshiruv
    /// muvaffaqiyatsiz va o'chirish BEKOR QILINADI.
    /// </summary>
    Task<Stream?> DownloadAsync(string location, CancellationToken ct = default);
}
