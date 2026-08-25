namespace CustomSync.Data.Entities;

public class AuditLogEntity
{
    public long     Id       { get; set; }
    public DateTime At       { get; set; }
    public string?  DeviceId { get; set; }
    public string   Action   { get; set; } = null!;
    public string?  Detail   { get; set; }
}
