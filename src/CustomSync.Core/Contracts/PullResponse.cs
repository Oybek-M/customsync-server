namespace CustomSync.Core.Contracts;

public sealed record StoredRecord
{
    public required long   Seq         { get; init; }
    public required string RecordId    { get; init; }
    public required string Kind        { get; init; }
    public required string AccountHash { get; init; }
    public required string PeerHash    { get; init; }
    public required long   MsgId       { get; init; }
    public required long   OccurredAt  { get; init; }
    public required long   ObservedAt  { get; init; }
    public required string DeviceId    { get; init; }
    public required byte[] Nonce       { get; init; }
    public required byte[] Payload     { get; init; }
    public IReadOnlyList<string> MediaHashes { get; init; } = Array.Empty<string>();
}

public sealed record PullResponse
{
    public required IReadOnlyList<StoredRecord> Records { get; init; }
    public required long NextSince { get; init; }
    public required bool HasMore   { get; init; }
}
