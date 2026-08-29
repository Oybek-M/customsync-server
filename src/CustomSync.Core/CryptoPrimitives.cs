using System.Security.Cryptography;
using System.Text;

namespace CustomSync.Core;

/// <summary>
/// Platformalararo test vektorlari (.NET reference implementatsiyasi) uchun
/// kalit hosil qilish va HMAC hash primitivlari.
/// Serverning o'zi master key'ni saqlamaydi va bu metodlarni to'g'ridan-to'g'ri
/// chaqirmaydi, lekin ular test vektorlari mosligini kafolatlash uchun mavjud.
/// </summary>
public static class CryptoPrimitives
{
    /// <summary>
    /// HKDF-SHA256 orqali 32 baytli kalit hosil qilish.
    /// Salt = 32 bayt nol (bo'sh emas, aynan 32 ta 0x00 bayti).
    /// </summary>
    public static byte[] DeriveKey(byte[] masterKey, string info)
    {
        var salt = new byte[32];
        var infoBytes = Encoding.UTF8.GetBytes(info);
        return HKDF.DeriveKey(HashAlgorithmName.SHA256, masterKey, 32, salt, infoBytes);
    }

    /// <summary>
    /// spec §0.12: HMAC-SHA256(account_key, account_id)[0..16] -> lowercase hex.
    /// account_id — o'nlik satr (masalan "111222333").
    /// </summary>
    public static string ComputeAccountHash(byte[] accountKey, string accountId)
    {
        using var hmac = new HMACSHA256(accountKey);
        var hash = hmac.ComputeHash(Encoding.UTF8.GetBytes(accountId));
        return Convert.ToHexString(hash[..16]).ToLowerInvariant();
    }

    /// <summary>
    /// spec §0.12: HMAC-SHA256(peer_key, peer_id)[0..16] -> lowercase hex.
    /// peer_id — o'nlik satr (masalan "7053823996").
    /// </summary>
    public static string ComputePeerHash(byte[] peerKey, string peerId)
    {
        using var hmac = new HMACSHA256(peerKey);
        var hash = hmac.ComputeHash(Encoding.UTF8.GetBytes(peerId));
        return Convert.ToHexString(hash[..16]).ToLowerInvariant();
    }
}
