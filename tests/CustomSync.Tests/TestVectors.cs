using System.Text.Json;

namespace CustomSync.Tests;

/// <summary>
/// Platformalararo test vektorlarini yuklaydi.
///
/// Fayl ATAYLAB bu repoga nusxalanmagan — u tdesktop repo'sida yagona
/// nusxa bo'lib turadi (docs/sync-protocol/README.md). Nusxa olingan
/// kontrakt birinchi kundanoq eskira boshlaydi.
///
/// Topilmasa test JIMGINA o'tib ketmaydi, balki yiqiladi: vektorsiz
/// o'tgan kripto testi hech narsani isbotlamaydi.
/// </summary>
internal static class TestVectors
{
    private const string EnvVar = "CUSTOMSYNC_TEST_VECTORS";

    private static readonly Lazy<(string Path, JsonElement Root)> Loaded = new(Load);

    /// <summary>
    /// Bo'lim yo'q bo'lsa aniq xabar beradi. Oddiy `GetProperty` faqat
    /// `KeyNotFoundException` tashlardi — 2026-10-01 da laptop'dagi eski
    /// tdesktop nusxasi tufayli 17 test shu xabar bilan yiqildi va sabab
    /// (fayl eskirgan, kod emas) ko'rinmadi.
    /// </summary>
    public static JsonElement Get(string section)
    {
        var (path, root) = Loaded.Value;
        if (root.TryGetProperty(section, out var value))
            return value;

        throw new InvalidOperationException(
            $"test-vectors.json da '{section}' bo'limi yo'q: {path}\n" +
            "Ehtimol shu kompyuterdagi tdesktop nusxasi eskirgan. U repo'ga bu " +
            "sessiyadan tegilmaydi; uning o'z sessiyasida yangilang yoki " +
            $"{EnvVar} ga yangi nusxani bering:\n" +
            "  git -C <tdesktop> show origin/Oybek:docs/sync-protocol/test-vectors.json > <repo tashqarisidagi fayl>");
    }

    private static (string Path, JsonElement Root) Load()
    {
        var path = Resolve()
            ?? throw new InvalidOperationException(
                $"test-vectors.json topilmadi. {EnvVar} muhit o'zgaruvchisiga " +
                "to'liq yo'lni bering, masalan:\n" +
                @"  set CUSTOMSYNC_TEST_VECTORS=<tdesktop>\docs\sync-protocol\test-vectors.json " +
                @"(C:\TBuild\tdesktop yoki D:\TBuild\tdesktop)");

        using var stream = File.OpenRead(path);
        return (path, JsonDocument.Parse(stream).RootElement.Clone());
    }

    private static string? Resolve()
    {
        var fromEnv = Environment.GetEnvironmentVariable(EnvVar);
        if (!string.IsNullOrWhiteSpace(fromEnv) && File.Exists(fromEnv))
            return fromEnv;

        // tdesktop build daraxtining ma'lum joylari: laptopda C:, PC'da D:
        // (docs/MACHINES.md). Birinchi mavjudi olinadi.
        string[] documented =
        {
            @"C:\TBuild\tdesktop\docs\sync-protocol\test-vectors.json",
            @"D:\TBuild\tdesktop\docs\sync-protocol\test-vectors.json",
        };
        return documented.FirstOrDefault(File.Exists);
    }
}
