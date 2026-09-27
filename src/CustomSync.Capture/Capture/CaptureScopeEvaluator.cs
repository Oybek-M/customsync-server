using System.Globalization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace CustomSync.Capture.Capture;

/// <summary>
/// Evaluates whether a peer should be cached or captured for deletions and edits.
/// Precedence order:
/// 1. Server Block (exact peer id) -> false for all three decisions
/// 2. Server Allow (exact peer id) -> true for all three decisions
/// 3. Synced settings snapshot, if present -> tdesktop chain
/// 4. Otherwise Server DefaultEnabled -> that value for all three
///
/// NOTE: Configuration (Server Block/Allow/DefaultEnabled) is read once at startup;
/// a change needs a service restart.
/// Synced settings snapshot is read via ISyncedScopeSettingsSource on every decision,
/// allowing runtime updates from sync pull without restart.
/// </summary>
public class CaptureScopeEvaluator : ICaptureScope
{
    private readonly HashSet<string> _serverBlock;
    private readonly HashSet<string> _serverAllow;
    private readonly bool _serverDefaultEnabled;
    private readonly ISyncedScopeSettingsSource _settingsSource;

    [ActivatorUtilitiesConstructor]
    public CaptureScopeEvaluator(
        IConfiguration configuration,
        ISyncedScopeSettingsSource? settingsSource = null)
        : this(
            ScopeConfigReader.ReadPeerList(configuration, "Capture:Scope:Block"),
            ScopeConfigReader.ReadPeerList(configuration, "Capture:Scope:Allow"),
            bool.TryParse(configuration["Capture:Scope:DefaultEnabled"], out bool def) && def,
            settingsSource)
    {
    }

    public CaptureScopeEvaluator(
        IEnumerable<string>? serverBlock,
        IEnumerable<string>? serverAllow,
        bool serverDefaultEnabled = false,
        ISyncedScopeSettingsSource? settingsSource = null)
    {
        _serverBlock = serverBlock != null ? new HashSet<string>(serverBlock) : new HashSet<string>();
        _serverAllow = serverAllow != null ? new HashSet<string>(serverAllow) : new HashSet<string>();
        _serverDefaultEnabled = serverDefaultEnabled;
        _settingsSource = settingsSource ?? new NullSyncedScopeSettingsSource();
    }

    public bool ShouldCache(string peerId)
    {
        if (string.IsNullOrEmpty(peerId)) return false;

        // 1. Server Block
        if (_serverBlock.Contains(peerId)) return false;

        // 2. Server Allow
        if (_serverAllow.Contains(peerId)) return true;

        // 3. Synced settings snapshot
        var snapshot = _settingsSource.CurrentSnapshot;
        if (snapshot != null)
        {
            return EvaluateChain(peerId, snapshot, Decision.Cache);
        }

        // 4. Server DefaultEnabled
        return _serverDefaultEnabled;
    }

    public bool ShouldAntiDelete(string peerId)
    {
        if (string.IsNullOrEmpty(peerId)) return false;

        if (_serverBlock.Contains(peerId)) return false;
        if (_serverAllow.Contains(peerId)) return true;

        var snapshot = _settingsSource.CurrentSnapshot;
        if (snapshot != null)
        {
            return EvaluateChain(peerId, snapshot, Decision.Delete);
        }

        return _serverDefaultEnabled;
    }

    public bool ShouldAntiEdit(string peerId)
    {
        if (string.IsNullOrEmpty(peerId)) return false;

        if (_serverBlock.Contains(peerId)) return false;
        if (_serverAllow.Contains(peerId)) return true;

        var snapshot = _settingsSource.CurrentSnapshot;
        if (snapshot != null)
        {
            return EvaluateChain(peerId, snapshot, Decision.Edit);
        }

        return _serverDefaultEnabled;
    }

    public static PeerType GetPeerType(string peerId)
    {
        if (string.IsNullOrEmpty(peerId)) return PeerType.Unknown;
        if (!ulong.TryParse(peerId, NumberStyles.None, CultureInfo.InvariantCulture, out ulong value))
            return PeerType.Unknown;

        int typeVal = (int)((value >> 48) & 0xFF);
        return typeVal switch
        {
            0 => PeerType.User,
            1 => PeerType.Group,
            2 => PeerType.Channel,
            _ => PeerType.Unknown
        };
    }

    private enum Decision
    {
        Cache,
        Delete,
        Edit
    }

    private static bool EvaluateChain(string peerId, ScopeSettingsSnapshot snapshot, Decision decision)
    {
        bool inExactBlocklist = snapshot.Blocklist.Contains(peerId);
        bool inExactWhitelist = snapshot.Whitelist.Contains(peerId);

        PeerType type = GetPeerType(peerId);

        bool blCat = type switch
        {
            PeerType.User => snapshot.BlocklistCategories.User,
            PeerType.Group => snapshot.BlocklistCategories.Group,
            PeerType.Channel => snapshot.BlocklistCategories.Channel,
            _ => false
        };

        bool wlCat = type switch
        {
            PeerType.User => snapshot.WhitelistCategories.User,
            PeerType.Group => snapshot.WhitelistCategories.Group,
            PeerType.Channel => snapshot.WhitelistCategories.Channel,
            _ => false
        };

        // InBlocklist(p) = p ∈ BL || (p ∉ WL && BLcat[type(p)])
        // InWhitelist(p) = p ∈ WL || (p ∉ BL && WLcat[type(p)])
        bool inBlocklist = inExactBlocklist || (!inExactWhitelist && blCat);
        bool inWhitelist = inExactWhitelist || (!inExactBlocklist && wlCat);

        if (inBlocklist) return false;
        if (inWhitelist) return true;

        bool deleteFallback = snapshot.AntiDeletePerPeer.TryGetValue(peerId, out bool perPeerDelete)
            ? perPeerDelete
            : snapshot.GlobalAntiDelete;

        bool editFallback = snapshot.AntiEditPerPeer.TryGetValue(peerId, out bool perPeerEdit)
            ? perPeerEdit
            : snapshot.GlobalAntiEdit;

        return decision switch
        {
            Decision.Delete => deleteFallback,
            Decision.Edit => editFallback,
            Decision.Cache => deleteFallback || editFallback,
            _ => false
        };
    }
}
