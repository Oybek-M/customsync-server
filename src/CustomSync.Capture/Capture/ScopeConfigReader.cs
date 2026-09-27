using System.Globalization;
using Microsoft.Extensions.Configuration;

namespace CustomSync.Capture.Capture;

public static class ScopeConfigReader
{
    public static List<string> ReadPeerList(IConfiguration config, string key)
    {
        var section = config.GetSection(key);
        var children = section.GetChildren().Select(c => c.Value).Where(v => !string.IsNullOrWhiteSpace(v)).ToList();
        if (children.Count > 0)
        {
            return children!;
        }

        var raw = config[key];
        if (!string.IsNullOrWhiteSpace(raw))
        {
            return raw.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        }

        return new List<string>();
    }

    public static bool IsValidDecimalInt64(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return false;

        return long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out _);
    }
}
