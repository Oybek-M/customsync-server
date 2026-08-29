using System.Security.Cryptography;
using System.Text;
using CustomSync.Core;
using Xunit;

namespace CustomSync.Tests;

public class CryptoVectorTests
{
    [Fact]
    public void Hkdf_reproduces_all_four_derived_keys()
    {
        var hkdfSection = TestVectors.Get("hkdf");
        var masterKeyHex = hkdfSection.GetProperty("master_key_hex").GetString()!;
        var masterKey = Convert.FromHexString(masterKeyHex);
        var derived = hkdfSection.GetProperty("derived");

        foreach (var prop in derived.EnumerateObject())
        {
            var info = prop.Name;
            var expectedHex = prop.Value.GetString()!;

            var actualKey = CryptoPrimitives.DeriveKey(masterKey, info);
            var actualHex = Convert.ToHexString(actualKey).ToLowerInvariant();

            Assert.Equal(expectedHex, actualHex);
        }
    }

    [Fact]
    public void Account_hash_matches_all_three_cases()
    {
        var hkdfSection = TestVectors.Get("hkdf");
        var masterKey = Convert.FromHexString(hkdfSection.GetProperty("master_key_hex").GetString()!);
        var accountKey = CryptoPrimitives.DeriveKey(masterKey, "customsync-account-v1");

        var cases = TestVectors.Get("account_hash").GetProperty("cases");
        Assert.NotEmpty(cases.EnumerateArray());

        foreach (var c in cases.EnumerateArray())
        {
            var accountId = c.GetProperty("account_id").GetString()!;
            var expectedHash = c.GetProperty("account_hash").GetString()!;

            var actualHash = CryptoPrimitives.ComputeAccountHash(accountKey, accountId);

            Assert.Equal(expectedHash, actualHash);
        }
    }

    [Fact]
    public void Peer_hash_matches_all_three_cases()
    {
        var hkdfSection = TestVectors.Get("hkdf");
        var masterKey = Convert.FromHexString(hkdfSection.GetProperty("master_key_hex").GetString()!);
        var peerKey = CryptoPrimitives.DeriveKey(masterKey, "customsync-peer-v1");

        var cases = TestVectors.Get("peer_hash").GetProperty("cases");
        Assert.NotEmpty(cases.EnumerateArray());

        foreach (var c in cases.EnumerateArray())
        {
            var peerId = c.GetProperty("peer_id").GetString()!;
            var expectedHash = c.GetProperty("peer_hash").GetString()!;

            var actualHash = CryptoPrimitives.ComputePeerHash(peerKey, peerId);

            Assert.Equal(expectedHash, actualHash);
        }
    }

    [Fact]
    public void Aes_256_gcm_encryption_produces_expected_ciphertext_and_tag()
    {
        var cases = TestVectors.Get("aes_gcm").GetProperty("cases");
        Assert.NotEmpty(cases.EnumerateArray());

        foreach (var c in cases.EnumerateArray())
        {
            var key = Convert.FromHexString(c.GetProperty("key_hex").GetString()!);
            var nonce = Convert.FromHexString(c.GetProperty("nonce_hex").GetString()!);
            var plaintext = Convert.FromHexString(c.GetProperty("plaintext_hex").GetString()!);
            var expectedCiphertextHex = c.GetProperty("ciphertext_hex").GetString()!;
            var expectedTagHex = c.GetProperty("tag_hex").GetString()!;

            using var aes = new AesGcm(key, tagSizeInBytes: 16);
            var ciphertext = new byte[plaintext.Length];
            var tag = new byte[16];

            aes.Encrypt(nonce, plaintext, ciphertext, tag);

            Assert.Equal(expectedCiphertextHex, Convert.ToHexString(ciphertext).ToLowerInvariant());
            Assert.Equal(expectedTagHex, Convert.ToHexString(tag).ToLowerInvariant());
        }
    }

    [Fact]
    public void Aes_256_gcm_decryption_recovers_plaintext_from_ciphertext_and_tag()
    {
        var cases = TestVectors.Get("aes_gcm").GetProperty("cases");
        Assert.NotEmpty(cases.EnumerateArray());

        foreach (var c in cases.EnumerateArray())
        {
            var key = Convert.FromHexString(c.GetProperty("key_hex").GetString()!);
            var nonce = Convert.FromHexString(c.GetProperty("nonce_hex").GetString()!);
            var ciphertext = Convert.FromHexString(c.GetProperty("ciphertext_hex").GetString()!);
            var tag = Convert.FromHexString(c.GetProperty("tag_hex").GetString()!);
            var expectedPlaintextHex = c.GetProperty("plaintext_hex").GetString()!;

            using var aes = new AesGcm(key, tagSizeInBytes: 16);
            var decrypted = new byte[ciphertext.Length];

            aes.Decrypt(nonce, ciphertext, tag, decrypted);

            Assert.Equal(expectedPlaintextHex, Convert.ToHexString(decrypted).ToLowerInvariant());
        }
    }

    [Fact]
    public void Pbkdf2_matches_all_three_cases()
    {
        var cases = TestVectors.Get("pbkdf2").GetProperty("cases");
        Assert.NotEmpty(cases.EnumerateArray());

        foreach (var c in cases.EnumerateArray())
        {
            var password = c.GetProperty("password").GetString()!;
            var salt = Convert.FromHexString(c.GetProperty("salt_hex").GetString()!);
            var iterations = c.GetProperty("iterations").GetInt32();
            var expectedKekHex = c.GetProperty("kek_hex").GetString()!;

            var actualKek = Rfc2898DeriveBytes.Pbkdf2(
                Encoding.UTF8.GetBytes(password),
                salt,
                iterations,
                HashAlgorithmName.SHA256,
                outputLength: 32);

            Assert.Equal(expectedKekHex, Convert.ToHexString(actualKek).ToLowerInvariant());
        }
    }
}
