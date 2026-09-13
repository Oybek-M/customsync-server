namespace CustomSync.Data.Entities;

public static class ArchiveRunStatus
{
    public const string Completed            = "completed";
    public const string FailedUpload         = "failed_upload";
    public const string FailedVerification   = "failed_verification";
    public const string AwaitingConfirmation = "awaiting_confirmation";
    public const string NothingToDo          = "nothing_to_do";
}

public class ArchiveRunEntity
{
    public long      RunId           { get; set; }
    public string    PolicyId        { get; set; } = null!;
    public string    TargetId        { get; set; } = null!;
    public string    Status          { get; set; } = null!;
    public DateTime  StartedAt       { get; set; }
    public DateTime? FinishedAt      { get; set; }
    public int       MatchedCount    { get; set; }
    public int       DeletedCount    { get; set; }
    public long      FreedBytes      { get; set; }
    public int       MissingMedia    { get; set; }
    public string?   ArchiveLocation { get; set; }
    public string?   Sha256          { get; set; }
    public string?   Error           { get; set; }
}
