using System.ComponentModel.DataAnnotations.Schema;

namespace CustomSync.Data.Entities;

[Table("upload_sessions")]
public class UploadSessionEntity
{
    public string SessionId { get; set; } = null!;
    public string ReleaseId { get; set; } = null!;
    public long TotalSize { get; set; }
    public long Received { get; set; }
    public string TempPath { get; set; } = null!;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime ExpiresAt { get; set; }
}
