namespace CustomSync.Capture.Capture;

public record OutboxRow(
    long Id,
    string Kind,
    string AccountId,
    string PeerId,
    long MsgId,
    long OccurredAt,
    long ObservedAt,
    string PayloadJson,
    long CreatedAt
);

public record DeleteResult(int DeletedCount, int UncachedCount);

public enum EditResult
{
    BaselineCreated,
    Unchanged,
    Edited
}
