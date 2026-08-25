namespace CustomSync.Data.Entities;

public class EnrollmentCodeEntity
{
    public string    CodeHash  { get; set; } = null!;
    public DateTime  CreatedAt { get; set; }
    public DateTime  ExpiresAt { get; set; }
    public DateTime? UsedAt    { get; set; }
    public string?   UsedBy    { get; set; }
}
