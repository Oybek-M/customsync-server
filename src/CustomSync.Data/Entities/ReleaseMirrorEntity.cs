using System.ComponentModel.DataAnnotations.Schema;

namespace CustomSync.Data.Entities;

[Table("release_mirrors")]
public class ReleaseMirrorEntity
{
    public string ReleaseId { get; set; } = null!;
    public string Mirror { get; set; } = null!;
    public string State { get; set; } = null!;
    public string? LastError { get; set; }
    public DateTime? VerifiedAt { get; set; }

    public ReleaseEntity? Release { get; set; }
}
