namespace CustomSync.Data.Entities;

/// <summary>
/// Retention siyosati — qanday ma'lumot qancha vaqtdan keyin nima
/// bo'lishi. Web app'dan to'liq tahrirlanadi (qoida K1): yangi siyosat
/// qo'shish uchun kodga tegilmaydi.
/// </summary>
public class RetentionPolicyEntity
{
    public const string ActionArchiveThenDelete = "archive_then_delete";
    public const string ActionDeleteOnly        = "delete_only";
    public const string ActionNeverDelete       = "never_delete";

    public string    PolicyId      { get; set; } = null!;
    public string    Name          { get; set; } = null!;
    public bool      Enabled       { get; set; }

    /// <summary>Qaysi yozuvlarga tegishli: kind, yoki bo'sh = hammasi.</summary>
    public string?   Kind          { get; set; }

    /// <summary>Faqat shu peer uchun; bo'sh = hammasi.</summary>
    public string?   PeerHash      { get; set; }

    /// <summary>Faqat media biriktirilgan yozuvlar uchunmi.</summary>
    public bool      MediaOnly     { get; set; }

    /// <summary>Necha kundan eski yozuvlarga qo'llanadi.</summary>
    public int       OlderThanDays { get; set; }

    /// <summary>"archive_then_delete" | "delete_only" | "never_delete"</summary>
    public string    Action        { get; set; } = null!;

    /// <summary>archive_then_delete uchun target id.</summary>
    public string?   TargetId      { get; set; }

    public int       Priority      { get; set; }
    public DateTime  UpdatedAt     { get; set; }

    /// <summary>
    /// Siyosat parametrlarini tekshiradi. Minimal kundan kam bo'lsa yoki
    /// noma'lum amal bo'lsa xatolik matnini qaytaradi, aks holda null.
    /// </summary>
    public string? GetValidationError(int minDays)
    {
        if (Action is not (ActionArchiveThenDelete or ActionDeleteOnly or ActionNeverDelete))
        {
            return $"Noma'lum amal: '{Action}'";
        }

        if (Action != ActionNeverDelete && OlderThanDays < minDays)
        {
            return $"OlderThanDays ({OlderThanDays}) minimal chegara ({minDays} kun) dan kam bo'lishi mumkin emas";
        }

        return null;
    }

    public bool IsValid(int minDays) => GetValidationError(minDays) == null;
}
