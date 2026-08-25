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
}
