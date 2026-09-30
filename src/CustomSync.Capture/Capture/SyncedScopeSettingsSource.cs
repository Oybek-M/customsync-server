using System.Text.Json;
using CustomSync.Capture.Capture;
using Microsoft.Extensions.Logging;

namespace CustomSync.Capture.Capture;

public class SyncedScopeSettingsSource : ISyncedScopeSettingsSource, ISyncedActivityScopeSettingsSource
{
    public static readonly HashSet<string> AllScopeKeys = new(StringComparer.Ordinal)
    {
        "scope.whitelist",
        "scope.blacklist",
        "scope.wl_categories",
        "scope.bl_categories",
        "scope.antidelete_global",
        "scope.antiedit_global",
        "scope.antidelete_per_peer",
        "scope.antiedit_per_peer",
        "scope.activity_track_all_contacts",
        "scope.activity_include",
        "scope.activity_exclude"
    };

    private readonly MessageCache _cache;
    private readonly ILogger<SyncedScopeSettingsSource>? _logger;

    private ScopeSettingsSnapshot? _messageSnapshot;
    private ActivityScopeSettingsSnapshot? _activitySnapshot;

    public ScopeSettingsSnapshot? CurrentSnapshot => _messageSnapshot;
    ActivityScopeSettingsSnapshot? ISyncedActivityScopeSettingsSource.CurrentSnapshot => _activitySnapshot;

    public SyncedScopeSettingsSource(MessageCache cache, ILogger<SyncedScopeSettingsSource>? logger = null)
    {
        _cache = cache;
        _logger = logger;
        RebuildSnapshotsFromStore();
    }

    public void RebuildSnapshotsFromStore()
    {
        var rows = _cache.GetAllSyncedSettings();
        var dict = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var r in rows)
        {
            dict[r.Key] = r.Value;
        }

        var newMessage = BuildMessageSnapshot(dict);
        var newActivity = BuildActivitySnapshot(dict);

        Interlocked.Exchange(ref _messageSnapshot, newMessage);
        Interlocked.Exchange(ref _activitySnapshot, newActivity);
    }

    public static ScopeSettingsSnapshot? BuildMessageSnapshot(IReadOnlyDictionary<string, string> dict)
    {
        string[] requiredKeys =
        {
            "scope.whitelist",
            "scope.blacklist",
            "scope.wl_categories",
            "scope.bl_categories",
            "scope.antidelete_global",
            "scope.antiedit_global",
            "scope.antidelete_per_peer",
            "scope.antiedit_per_peer"
        };

        foreach (var k in requiredKeys)
        {
            if (!dict.ContainsKey(k))
                return null;
        }

        var wl = ParsePeerList(dict["scope.whitelist"]);
        if (wl == null) return null;

        var bl = ParsePeerList(dict["scope.blacklist"]);
        if (bl == null) return null;

        var wlCats = ParseCategories(dict["scope.wl_categories"]);
        if (wlCats == null) return null;

        var blCats = ParseCategories(dict["scope.bl_categories"]);
        if (blCats == null) return null;

        var adGlobal = ParseBool(dict["scope.antidelete_global"]);
        if (adGlobal == null) return null;

        var aeGlobal = ParseBool(dict["scope.antiedit_global"]);
        if (aeGlobal == null) return null;

        var adPerPeer = ParsePeerBoolMap(dict["scope.antidelete_per_peer"]);
        if (adPerPeer == null) return null;

        var aePerPeer = ParsePeerBoolMap(dict["scope.antiedit_per_peer"]);
        if (aePerPeer == null) return null;

        return new ScopeSettingsSnapshot(
            whitelist: wl,
            blocklist: bl,
            whitelistCategories: wlCats,
            blocklistCategories: blCats,
            antiDeletePerPeer: adPerPeer,
            antiEditPerPeer: aePerPeer,
            globalAntiDelete: adGlobal.Value,
            globalAntiEdit: aeGlobal.Value);
    }

    public static ActivityScopeSettingsSnapshot? BuildActivitySnapshot(IReadOnlyDictionary<string, string> dict)
    {
        string[] requiredKeys =
        {
            "scope.activity_track_all_contacts",
            "scope.activity_include",
            "scope.activity_exclude"
        };

        foreach (var k in requiredKeys)
        {
            if (!dict.ContainsKey(k))
                return null;
        }

        var trackAll = ParseBool(dict["scope.activity_track_all_contacts"]);
        if (trackAll == null) return null;

        var inc = ParsePeerList(dict["scope.activity_include"]);
        if (inc == null) return null;

        var exc = ParsePeerList(dict["scope.activity_exclude"]);
        if (exc == null) return null;

        return new ActivityScopeSettingsSnapshot(
            Exclude: exc,
            Include: inc,
            TrackAllContacts: trackAll.Value);
    }

    public static IReadOnlySet<string>? ParsePeerList(string rawJson)
    {
        if (string.IsNullOrWhiteSpace(rawJson))
            return null;

        try
        {
            using var doc = JsonDocument.Parse(rawJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
                return null;

            var set = new HashSet<string>(StringComparer.Ordinal);
            foreach (var element in doc.RootElement.EnumerateArray())
            {
                if (element.ValueKind != JsonValueKind.String)
                    return null;

                var val = element.GetString();
                if (string.IsNullOrEmpty(val) || !ScopeConfigReader.IsCanonicalPeerId(val))
                {
                    return null;
                }

                set.Add(val);
            }

            return set;
        }
        catch
        {
            return null;
        }
    }

    public static ScopeCategories? ParseCategories(string rawJson)
    {
        if (string.IsNullOrWhiteSpace(rawJson))
            return null;

        try
        {
            using var doc = JsonDocument.Parse(rawJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                return null;

            bool? user = null;
            bool? group = null;
            bool? channel = null;

            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                if (prop.Value.ValueKind != JsonValueKind.True && prop.Value.ValueKind != JsonValueKind.False)
                    return null;

                if (prop.NameEquals("user")) user = prop.Value.GetBoolean();
                else if (prop.NameEquals("group")) group = prop.Value.GetBoolean();
                else if (prop.NameEquals("channel")) channel = prop.Value.GetBoolean();
                else
                {
                    return null;
                }
            }

            if (!user.HasValue || !group.HasValue || !channel.HasValue)
                return null;

            return new ScopeCategories(User: user.Value, Group: group.Value, Channel: channel.Value);
        }
        catch
        {
            return null;
        }
    }

    public static IReadOnlyDictionary<string, bool>? ParsePeerBoolMap(string rawJson)
    {
        if (string.IsNullOrWhiteSpace(rawJson))
            return null;

        try
        {
            using var doc = JsonDocument.Parse(rawJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                return null;

            var dict = new Dictionary<string, bool>(StringComparer.Ordinal);
            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                if (!ScopeConfigReader.IsCanonicalPeerId(prop.Name))
                    return null;

                if (prop.Value.ValueKind != JsonValueKind.True && prop.Value.ValueKind != JsonValueKind.False)
                    return null;

                dict[prop.Name] = prop.Value.GetBoolean();
            }

            return dict;
        }
        catch
        {
            return null;
        }
    }

    public static bool? ParseBool(string rawValue)
    {
        if (string.Equals(rawValue, "true", StringComparison.OrdinalIgnoreCase))
            return true;
        if (string.Equals(rawValue, "false", StringComparison.OrdinalIgnoreCase))
            return false;
        return null;
    }
}
