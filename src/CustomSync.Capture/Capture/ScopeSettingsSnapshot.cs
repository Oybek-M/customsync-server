namespace CustomSync.Capture.Capture;

public enum PeerType
{
    User = 0,
    Group = 1,
    Channel = 2,
    Unknown = 3
}

public record ScopeCategories(bool User = false, bool Group = false, bool Channel = false);

/// <summary>
/// Immutable snapshot of synced scope settings originating from owner preferences.
/// Filled by Task 6 once setting records arrive through sync pull.
/// Safe to read concurrently while another thread replaces the snapshot instance.
/// </summary>
public record ScopeSettingsSnapshot
{
    public IReadOnlySet<string> Whitelist { get; init; }
    public IReadOnlySet<string> Blocklist { get; init; }
    public ScopeCategories WhitelistCategories { get; init; }
    public ScopeCategories BlocklistCategories { get; init; }
    public IReadOnlyDictionary<string, bool> AntiDeletePerPeer { get; init; }
    public IReadOnlyDictionary<string, bool> AntiEditPerPeer { get; init; }
    public bool GlobalAntiDelete { get; init; }
    public bool GlobalAntiEdit { get; init; }

    public ScopeSettingsSnapshot(
        IReadOnlySet<string>? whitelist = null,
        IReadOnlySet<string>? blocklist = null,
        ScopeCategories? whitelistCategories = null,
        ScopeCategories? blocklistCategories = null,
        IReadOnlyDictionary<string, bool>? antiDeletePerPeer = null,
        IReadOnlyDictionary<string, bool>? antiEditPerPeer = null,
        bool globalAntiDelete = false,
        bool globalAntiEdit = false)
    {
        Whitelist = whitelist ?? new HashSet<string>();
        Blocklist = blocklist ?? new HashSet<string>();
        WhitelistCategories = whitelistCategories ?? new ScopeCategories();
        BlocklistCategories = blocklistCategories ?? new ScopeCategories();
        AntiDeletePerPeer = antiDeletePerPeer ?? new Dictionary<string, bool>();
        AntiEditPerPeer = antiEditPerPeer ?? new Dictionary<string, bool>();
        GlobalAntiDelete = globalAntiDelete;
        GlobalAntiEdit = globalAntiEdit;
    }
}

/// <summary>
/// Source interface providing the current synced scope settings snapshot.
/// </summary>
public interface ISyncedScopeSettingsSource
{
    ScopeSettingsSnapshot? CurrentSnapshot { get; }
}

/// <summary>
/// Default source returning null snapshot.
/// Filled by Task 6 once the sync protocol defines the setting keys and pull logic.
/// Returning null causes the scope evaluator to fall back to the server DefaultEnabled setting.
/// </summary>
public class NullSyncedScopeSettingsSource : ISyncedScopeSettingsSource
{
    public ScopeSettingsSnapshot? CurrentSnapshot => null;
}
