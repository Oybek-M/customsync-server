using Microsoft.Extensions.Configuration;

using Microsoft.Extensions.DependencyInjection;

namespace CustomSync.Capture.Capture;

public class ActivityScopeEvaluator : IActivityScope
{
    private readonly HashSet<string> _serverExclude;
    private readonly HashSet<string> _serverInclude;
    private readonly bool _serverTrackAllContacts;
    private readonly ISyncedActivityScopeSettingsSource _settingsSource;

    [ActivatorUtilitiesConstructor]
    public ActivityScopeEvaluator(
        IConfiguration config,
        ISyncedActivityScopeSettingsSource? settingsSource = null)
        : this(
            ScopeConfigReader.ReadPeerList(config, "Capture:Activity:Exclude"),
            ScopeConfigReader.ReadPeerList(config, "Capture:Activity:Include"),
            bool.TryParse(config["Capture:Activity:TrackAllContacts"], out bool val) && val,
            settingsSource)
    {
    }

    public ActivityScopeEvaluator(
        IEnumerable<string>? serverExclude,
        IEnumerable<string>? serverInclude,
        bool serverTrackAllContacts = false,
        ISyncedActivityScopeSettingsSource? settingsSource = null)
    {
        _serverExclude = serverExclude != null
            ? new HashSet<string>(serverExclude, StringComparer.Ordinal)
            : new HashSet<string>(StringComparer.Ordinal);
        _serverInclude = serverInclude != null
            ? new HashSet<string>(serverInclude, StringComparer.Ordinal)
            : new HashSet<string>(StringComparer.Ordinal);
        _serverTrackAllContacts = serverTrackAllContacts;
        _settingsSource = settingsSource ?? new NullSyncedActivityScopeSettingsSource();
    }

    public bool ShouldTrackActivity(string peerId, bool isContact)
    {
        if (string.IsNullOrEmpty(peerId))
            return false;

        // 1. Server Exclude -> false
        if (_serverExclude.Contains(peerId))
            return false;

        // 2. Server Include -> true
        if (_serverInclude.Contains(peerId))
            return true;

        // 3. Synced activity settings snapshot, if present
        var snapshot = _settingsSource.CurrentSnapshot;
        if (snapshot != null)
        {
            if (snapshot.Exclude.Contains(peerId))
                return false;
            if (snapshot.Include.Contains(peerId))
                return true;
            return snapshot.TrackAllContacts && isContact;
        }

        // 4. Otherwise Capture:Activity:TrackAllContacts && isContact (default false)
        return _serverTrackAllContacts && isContact;
    }
}
