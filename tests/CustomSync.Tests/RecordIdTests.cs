using CustomSync.Core;
using Xunit;

namespace CustomSync.Tests;

public class RecordIdTests
{
    [Fact]
    public void Compute_is_deterministic()
    {
        var a = RecordId.Compute("deleted", "a3f9c2", 12345, 1753800000);
        var b = RecordId.Compute("deleted", "a3f9c2", 12345, 1753800000);

        Assert.Equal(a, b);
    }

    [Fact]
    public void Compute_returns_lowercase_hex_of_64_chars()
    {
        var id = RecordId.Compute("deleted", "a3f9c2", 12345, 1753800000);

        Assert.Equal(64, id.Length);
        Assert.Matches("^[0-9a-f]{64}$", id);
    }

    [Theory]
    [InlineData("edited",  "a3f9c2", 12345, 1753800000)]
    [InlineData("deleted", "b7c210", 12345, 1753800000)]
    [InlineData("deleted", "a3f9c2", 12346, 1753800000)]
    [InlineData("deleted", "a3f9c2", 12345, 1753800001)]
    public void Compute_changes_when_any_field_changes(
        string kind, string peerHash, long msgId, long occurredAt)
    {
        var baseline = RecordId.Compute("deleted", "a3f9c2", 12345, 1753800000);

        var other = RecordId.Compute(kind, peerHash, msgId, occurredAt);

        Assert.NotEqual(baseline, other);
    }

    [Fact]
    public void Separator_prevents_field_boundary_ambiguity()
    {
        // Ajratuvchi bo'lmasa "ab"+"c" va "a"+"bc" bir xil natija berardi.
        var first  = RecordId.Compute("ab", "c", 1, 1);
        var second = RecordId.Compute("a", "bc", 1, 1);

        Assert.NotEqual(first, second);
    }

    /// <summary>
    /// Spec §0.6: avatar `-photo_id`, story `-story_id` ishlatadi.
    /// Ishora yo'qolsa avatar va oddiy xabar bitta yozuvga qo'shilib
    /// ketardi.
    /// </summary>
    [Fact]
    public void Sign_of_msg_id_is_preserved()
    {
        var negative = RecordId.Compute("media_index", "a3f9c2", -42, 1753800000);
        var positive = RecordId.Compute("media_index", "a3f9c2", 42, 1753800000);

        Assert.NotEqual(negative, positive);
    }

    /// <summary>
    /// Kontrakt testi: `test-vectors.json` dagi har bir holat aynan
    /// qayta hosil bo'lishi shart. Bu yiqilsa — protokol buzilgan.
    /// </summary>
    [Fact]
    public void Matches_cross_platform_test_vectors()
    {
        var cases = TestVectors.Get("record_id").GetProperty("cases");

        Assert.NotEmpty(cases.EnumerateArray());

        foreach (var vector in cases.EnumerateArray())
        {
            var kind       = vector.GetProperty("kind").GetString()!;
            var peerHash   = vector.GetProperty("peer_hash").GetString()!;
            var msgId      = vector.GetProperty("msg_id").GetInt64();
            var occurredAt = vector.GetProperty("occurred_at").GetInt64();
            var expected   = vector.GetProperty("record_id").GetString()!;

            var actual = RecordId.Compute(kind, peerHash, msgId, occurredAt);

            Assert.Equal(expected, actual);
        }
    }
}
