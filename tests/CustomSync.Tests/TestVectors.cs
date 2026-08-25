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

    private static readonly Lazy<JsonElement> Root = new(Load);

    public static JsonElement Get(string section) => Root.Value.GetProperty(section);

    private static JsonElement Load()
    {
        var path = Resolve()
            ?? throw new InvalidOperationException(
                $"test-vectors.json topilmadi. {EnvVar} muhit o'zgaruvchisiga " +
                "to'liq yo'lni bering, masalan:\n" +
                @"  set CUSTOMSYNC_TEST_VECTORS=C:\TBuild\tdesktop\docs\sync-protocol\test-vectors.json");

        using var stream = File.OpenRead(path);
        return JsonDocument.Parse(stream).RootElement.Clone();
    }

    private static string? Resolve()
    {
        var fromEnv = Environment.GetEnvironmentVariable(EnvVar);
        if (!string.IsNullOrWhiteSpace(fromEnv) && File.Exists(fromEnv))
            return fromEnv;

        // tdesktop build daraxtining hujjatlashtirilgan joyi (CLAUDE.md).
        const string documented = @"C:\TBuild\tdesktop\docs\sync-protocol\test-vectors.json";
        return File.Exists(documented) ? documented : null;
    }
}
