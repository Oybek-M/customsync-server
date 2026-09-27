using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace CustomSync.Capture.Capture;

/// <summary>
/// Maps TDLib status and user structures to tdesktop-compatible string representations,
/// implements the 60-second status noise filter, and computes field discriminators.
/// Pure and independently tested.
/// </summary>
public static class ActivityMapper
{
    // Spec §3.2.2 / tdesktop kLifeStartDate + 4 (2013-08-01): undan eski
    // (0 ham) online/offline vaqti tdesktop'da LastseenStatus::LongAgo().
    public const long LifeStartThreshold = 1375315204;

    // "empty" HECH QACHON yozilmaydi: tdesktop userStatusEmpty ni ham,
    // noma'lum holatni ham "long_ago" deb yozadi. Boshqa qiymat — bir xil
    // hodisa ikki xil new_value bilan keladi va yozuvlar birlashmaydi.
    public const string LongAgo = "long_ago";

    public static string MapStatus(JsonElement statusElement, long now)
    {
        if (!statusElement.TryGetProperty("@type", out var typeProp))
            return LongAgo;

        string type = typeProp.GetString() ?? "";
        return type switch
        {
            "userStatusOnline" => MapOnline(statusElement, now),
            "userStatusOffline" => MapOffline(statusElement),
            "userStatusRecently" => "recently",
            "userStatusLastWeek" => "within_week",
            "userStatusLastMonth" => "within_month",
            "userStatusEmpty" => LongAgo,
            _ => LongAgo
        };
    }

    private static string MapOnline(JsonElement elem, long now)
    {
        long expires = 0;
        if (elem.TryGetProperty("expires", out var expProp))
        {
            if (expProp.ValueKind == JsonValueKind.Number)
                expires = expProp.GetInt64();
            else if (expProp.ValueKind == JsonValueKind.String && long.TryParse(expProp.GetString(), out long val))
                expires = val;
        }

        if (expires < LifeStartThreshold)
        {
            return LongAgo;
        }

        return expires > now ? $"online:{expires}" : $"offline:{expires}";
    }

    private static string MapOffline(JsonElement elem)
    {
        long wasOnline = 0;
        if (elem.TryGetProperty("was_online", out var woProp))
        {
            if (woProp.ValueKind == JsonValueKind.Number)
                wasOnline = woProp.GetInt64();
            else if (woProp.ValueKind == JsonValueKind.String && long.TryParse(woProp.GetString(), out long val))
                wasOnline = val;
        }

        return wasOnline < LifeStartThreshold ? LongAgo : $"offline:{wasOnline}";
    }

    /// <summary>
    /// Matches tdesktop langFullName(firstName, lastName):
    /// if first is empty -> last
    /// else if last is empty -> first
    /// else first + " " + last
    /// </summary>
    public static string MapName(string? firstName, string? lastName)
    {
        string first = firstName ?? "";
        string last = lastName ?? "";

        if (string.IsNullOrEmpty(first)) return last;
        if (string.IsNullOrEmpty(last)) return first;
        return $"{first} {last}";
    }

    /// <summary>
    /// Matches tdesktop UserData::username():
    /// returns first active username, or editable_username, or "".
    /// </summary>
    public static string MapUsername(JsonElement usernamesElement)
    {
        if (usernamesElement.TryGetProperty("active_usernames", out var activeProp)
            && activeProp.ValueKind == JsonValueKind.Array
            && activeProp.GetArrayLength() > 0)
        {
            return activeProp[0].GetString() ?? "";
        }

        if (usernamesElement.TryGetProperty("editable_username", out var editProp)
            && editProp.ValueKind == JsonValueKind.String)
        {
            return editProp.GetString() ?? "";
        }

        return "";
    }

    public static string StatusState(string value)
    {
        int colon = value.IndexOf(':');
        return colon > 0 ? value[..colon] : value;
    }

    public static long StatusAge(string value, long observedAt)
    {
        int colon = value.IndexOf(':');
        if (colon <= 0) return -1;
        if (long.TryParse(value.AsSpan(colon + 1), out long ts))
        {
            return observedAt - ts;
        }
        return -1;
    }

    /// <summary>
    /// tdesktop custom_activity_history.cpp:444-452:
    /// If StatusState(oldValue) == StatusState(newValue) and
    /// both have timestamps and |StatusAge(newValue, now) - StatusAge(oldValue, now)| < 60,
    /// it is periodic offline bump noise and must be suppressed.
    /// </summary>
    public static bool IsStatusNoise(string oldValue, string newValue, long observedAt)
    {
        if (StatusState(oldValue) != StatusState(newValue))
            return false;

        long newAge = StatusAge(newValue, observedAt);
        long oldAge = StatusAge(oldValue, observedAt);
        if (newAge >= 0 && oldAge >= 0 && Math.Abs(newAge - oldAge) < 60)
        {
            return true;
        }

        return false;
    }

    /// <summary>
    /// Matches tdesktop custom_sync_record.cpp:66-76 DiscriminatorFor(field):
    /// SHA256(field.toUtf8())[0:8] as big-endian int64, cleared sign bit (& 0x7FFFFFFFFFFFFFFF).
    /// </summary>
    public static long DiscriminatorFor(string text)
    {
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(text));
        long result = 0;
        for (int i = 0; i < 8; ++i)
        {
            result = (result << 8) | digest[i];
        }
        return result & 0x7FFFFFFFFFFFFFFF;
    }
}
