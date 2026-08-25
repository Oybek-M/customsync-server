using System.Buffers;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace CustomSync.Core;

/// <summary>
/// Yozuvning deterministik identifikatori — referens implementatsiya.
///
/// Formula:
///   record_id = hex(SHA256(kind ‖ 0x00 ‖ peerHash ‖ 0x00 ‖
///                          msgId ‖ 0x00 ‖ occurredAt))
///
/// Barcha satrlar UTF-8, sonlar InvariantCulture o'nlik ko'rinishda
/// (manfiy son oldidagi '-' ham kiradi — spec §0.6).
/// 0x00 ajratuvchi maydon chegarasi noaniqligini yo'q qiladi.
///
/// MUHIM: bu funksiya beshala platformada bayt-ma-bayt bir xil natija
/// berishi shart. O'zgartirish oldingi barcha yozuvlarni yaroqsiz qiladi.
/// </summary>
public static class RecordId
{
    public static string Compute(
        string kind, string peerHash, long msgId, long occurredAt)
    {
        var buffer = new ArrayBufferWriter<byte>(128);
        Append(buffer, kind);
        Append(buffer, "\0");
        Append(buffer, peerHash);
        Append(buffer, "\0");
        Append(buffer, msgId.ToString(CultureInfo.InvariantCulture));
        Append(buffer, "\0");
        Append(buffer, occurredAt.ToString(CultureInfo.InvariantCulture));

        var hash = SHA256.HashData(buffer.WrittenSpan);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static void Append(ArrayBufferWriter<byte> writer, string value)
    {
        var count = Encoding.UTF8.GetByteCount(value);
        var span = writer.GetSpan(count);
        Encoding.UTF8.GetBytes(value, span);
        writer.Advance(count);
    }
}
