namespace CustomSync.Capture.Capture;

public record ActivityScopeSettingsSnapshot(
    IReadOnlySet<string> Exclude,
    IReadOnlySet<string> Include,
    bool TrackAllContacts);

public interface ISyncedActivityScopeSettingsSource
{
    ActivityScopeSettingsSnapshot? CurrentSnapshot { get; }
}

public class NullSyncedActivityScopeSettingsSource : ISyncedActivityScopeSettingsSource
{
    public ActivityScopeSettingsSnapshot? CurrentSnapshot => null;
}
