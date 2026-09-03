using System.ComponentModel.DataAnnotations.Schema;

namespace CustomSync.Data.Entities;

[Table("releases")]
public class ReleaseEntity
{
    public string ReleaseId { get; set; } = null!;
    public string Platform { get; set; } = null!;
    public long Version { get; set; }
    public string Channel { get; set; } = null!;
    public string FileName { get; set; } = null!;
    public long Size { get; set; }
    public string Sha256 { get; set; } = null!;
    public string StoragePath { get; set; } = null!;
    public string UploadedBy { get; set; } = null!;
    public DateTime UploadedAt { get; set; } = DateTime.UtcNow;
    public DateTime? PublishedAt { get; set; }

    public List<ReleaseMirrorEntity> Mirrors { get; set; } = [];
}
