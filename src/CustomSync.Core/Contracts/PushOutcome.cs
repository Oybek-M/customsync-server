namespace CustomSync.Core.Contracts;

public static class PushOutcome
{
    /// <summary>Yangi yozuv qo'shildi.</summary>
    public const string Created = "created";

    /// <summary>Allaqachon mavjud va mavjudi yaxshiroq — o'zgarish yo'q.</summary>
    public const string Duplicate = "duplicate";

    /// <summary>Mavjud yozuv yaxshiroq kuzatuv bilan almashtirildi.</summary>
    public const string Superseded = "superseded";

    /// <summary>Rad etildi — klient outbox'da ushlab qolishi va qayta urinishi kerak.</summary>
    public const string Error = "error";
}

public sealed record PushResult(
    string RecordId, string Status, long? Seq = null, string? Message = null);
