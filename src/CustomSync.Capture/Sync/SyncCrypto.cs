using System.Buffers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CustomSync.Capture.Capture;
using CustomSync.Core;
using CustomSync.Core.Contracts;

namespace CustomSync.Capture.Sync;

public static class SyncCrypto
{
    public const string ContentKeyInfo = "customsync-content-v1";
    public const string PeerKeyInfo = "customsync-peer-v1";
    public const string AccountKeyInfo = "customsync-account-v1";

    public static byte[] DeriveContentKey(byte[] masterKey)
        => CryptoPrimitives.DeriveKey(masterKey, ContentKeyInfo);

    public static byte[] DerivePeerKey(byte[] masterKey)
        => CryptoPrimitives.DeriveKey(masterKey, PeerKeyInfo);

    public static byte[] DeriveAccountKey(byte[] masterKey)
        => CryptoPrimitives.DeriveKey(masterKey, AccountKeyInfo);

    public static (byte[] Payload, byte[] Nonce) EncryptPayload(
        byte[] contentKey,
        byte[] plaintext,
        byte[]? fixedNonce = null)
    {
        var nonce = fixedNonce ?? RandomNumberGenerator.GetBytes(12);
        if (nonce.Length != 12)
            throw new ArgumentException("Nonce must be exactly 12 bytes.", nameof(fixedNonce));

        var ciphertext = new byte[plaintext.Length];
        var tag = new byte[16];

        using var aes = new AesGcm(contentKey, tagSizeInBytes: 16);
        aes.Encrypt(nonce, plaintext, ciphertext, tag, associatedData: ReadOnlySpan<byte>.Empty);

        // Wire payload = ciphertext followed by tag (concatenation)
        var wirePayload = new byte[ciphertext.Length + tag.Length];
        Buffer.BlockCopy(ciphertext, 0, wirePayload, 0, ciphertext.Length);
        Buffer.BlockCopy(tag, 0, wirePayload, ciphertext.Length, tag.Length);

        return (wirePayload, nonce);
    }

    public static byte[] DecryptPayload(byte[] contentKey, byte[] nonce, byte[] wirePayload)
    {
        if (wirePayload.Length < 16)
            throw new CryptographicException("Wire payload is too short to contain a 16-byte authentication tag.");
        if (nonce.Length != 12)
            throw new ArgumentException("Nonce must be exactly 12 bytes.", nameof(nonce));

        int ciphertextLen = wirePayload.Length - 16;
        var ciphertext = wirePayload.AsSpan(0, ciphertextLen);
        var tag = wirePayload.AsSpan(ciphertextLen, 16);
        var plaintext = new byte[ciphertextLen];

        using var aes = new AesGcm(contentKey, tagSizeInBytes: 16);
        aes.Decrypt(nonce, ciphertext, tag, plaintext, associatedData: ReadOnlySpan<byte>.Empty);

        return plaintext;
    }

    public static bool ValidateSection014(string payloadJson, string rowAccountId, string rowPeerId)
    {
        try
        {
            using var doc = JsonDocument.Parse(payloadJson);
            var root = doc.RootElement;

            if (!root.TryGetProperty("account_id", out var accElem) ||
                !root.TryGetProperty("peer_id", out var peerElem))
            {
                return false;
            }

            string? accStr = accElem.ValueKind == JsonValueKind.String ? accElem.GetString() : accElem.GetRawText();
            string? peerStr = peerElem.ValueKind == JsonValueKind.String ? peerElem.GetString() : peerElem.GetRawText();

            return accStr == rowAccountId && peerStr == rowPeerId;
        }
        catch
        {
            return false;
        }
    }

    public static SyncRecord BuildRecord(
        OutboxRow row,
        byte[] masterKey,
        string deviceId,
        byte[]? fixedNonce = null)
    {
        if (!ValidateSection014(row.PayloadJson, row.AccountId, row.PeerId))
        {
            throw new InvalidOperationException($"Row {row.Id} failed §0.14 validation: payload_json account_id or peer_id mismatch.");
        }

        var contentKey = DeriveContentKey(masterKey);
        var peerKey = DerivePeerKey(masterKey);
        var accountKey = DeriveAccountKey(masterKey);

        // spec §0.12/§0.13: account_hash is "" for activity, otherwise HMAC-SHA256(account_key, account_id)
        string accountHash = row.Kind == "activity"
            ? ""
            : CryptoPrimitives.ComputeAccountHash(accountKey, row.AccountId);

        // spec §0.12: peer_hash uses account-less formula for every kind
        string peerHash = CryptoPrimitives.ComputePeerHash(peerKey, row.PeerId);

        string recordId = RecordId.Compute(row.Kind, accountHash, peerHash, row.MsgId, row.OccurredAt);

        var plaintext = Encoding.UTF8.GetBytes(row.PayloadJson);
        var (payload, nonce) = EncryptPayload(contentKey, plaintext, fixedNonce);

        return new SyncRecord
        {
            RecordId = recordId,
            Kind = row.Kind,
            AccountHash = accountHash,
            PeerHash = peerHash,
            MsgId = row.MsgId,
            OccurredAt = row.OccurredAt,
            ObservedAt = row.ObservedAt,
            DeviceId = deviceId,
            Nonce = nonce,
            Payload = payload
        };
    }
}
