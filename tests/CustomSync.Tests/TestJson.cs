using System.Text.Json;

namespace CustomSync.Tests;

/// <summary>
/// Server HTTP JSON'i snake_case (spec, .cmx va tdesktop agenti bilan mos).
/// `ReadFromJsonAsync&lt;T&gt;()` esa buni bilmaydi — u o'zining standart
/// camelCase sozlamasini ishlatadi va maydonlarni jimgina `null` qoldiradi.
/// Ko'p so'zli maydonli javoblarni o'qiyotganda shu sozlamani bering.
/// </summary>
internal static class TestJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
    };
}
