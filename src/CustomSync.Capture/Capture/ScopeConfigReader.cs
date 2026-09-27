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

    /// <summary>
    /// TdIdMapper chiqaradigan AYNAN shakl: musbat, belgisiz, bo'shliqsiz,
    /// boshida nolsiz o'nlik son. Evaluator satrlarni aniq solishtiradi —
    /// "0123", " 123" yoki TDLib chat_id (-100...) "son" bo'lib o'tsa ham
    /// hech qachon mos kelmaydi, ya'ni Block yozuvi jimgina ishlamay qoladi.
    /// </summary>
    public static bool IsCanonicalPeerId(string value)
    {
        if (string.IsNullOrEmpty(value))
            return false;

        return long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed)
            && parsed > 0
            && parsed.ToString(CultureInfo.InvariantCulture) == value;
    }
}
