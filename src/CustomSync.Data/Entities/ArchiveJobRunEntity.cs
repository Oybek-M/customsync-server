namespace CustomSync.Data.Entities;

public static class ArchiveJobRunStatus
{
    public const string Running   = "running";
    public const string Completed = "completed";
    public const string Skipped   = "skipped";
    public const string Failed    = "failed";
}

public class ArchiveJobRunEntity
{
    public DateOnly  RunDate    { get; set; }
    public DateTime  StartedAt  { get; set; }
    public DateTime? FinishedAt { get; set; }
    public string    Status     { get; set; } = null!;
    public string?   Summary    { get; set; }
}
