namespace CustomSync.Data.Entities;

public class DeviceEntity
{
    public string    DeviceId     { get; set; } = null!;
    public string    Name         { get; set; } = null!;
    public string    Platform     { get; set; } = null!;
    public DateTime  EnrolledAt   { get; set; }
    public DateTime? LastSeenAt   { get; set; }
    public long      LastCursor   { get; set; }
    public DateTime? RevokedAt    { get; set; }
    public string    RefreshHash  { get; set; } = null!;
    public string    Role         { get; set; } = "device";

    // Refresh imtiyoz oynasi: oldingi token hash'i, almashtirilgan vaqt va
    // yangi tokenning ESKI tokendan olingan kalit bilan shifrlangan nusxasi.
    public string?   PreviousRefreshHash { get; set; }
    public DateTime? RefreshRotatedAt    { get; set; }
    public byte[]?   RotationReplay      { get; set; }
}
