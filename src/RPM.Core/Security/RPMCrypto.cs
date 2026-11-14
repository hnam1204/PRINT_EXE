using System.Security.Cryptography;
using System.Text;

namespace RPM.Core.Security;

public static class RPMCrypto
{
    private static readonly byte[] Salt = Encoding.UTF8.GetBytes("RPM_PRINT_MANAGER_SALT_2026");

    /// <summary>
    /// Derives a 256-bit (32 bytes) key from a secret string.
    /// </summary>
    public static byte[] DeriveKey(string secret)
    {
        using var pbkdf2 = new Rfc2898DeriveBytes(secret, Salt, 10000, HashAlgorithmName.SHA256);
        return pbkdf2.GetBytes(32);
    }

    /// <summary>
    /// Encrypts byte array using AES-256-GCM.
    /// Returns (Ciphertext, Nonce, Tag).
    /// </summary>
    public static (byte[] Ciphertext, byte[] Nonce, byte[] Tag) Encrypt(byte[] plaintext, byte[] key)
    {
        if (key.Length != 32)
            throw new ArgumentException("Key must be 256 bits (32 bytes).", nameof(key));

        byte[] nonce = new byte[AesGcm.NonceByteSizes.MaxSize]; // 12 bytes
        RandomNumberGenerator.Fill(nonce);

        byte[] tag = new byte[AesGcm.TagByteSizes.MaxSize]; // 16 bytes
        byte[] ciphertext = new byte[plaintext.Length];

        using var aesGcm = new AesGcm(key, AesGcm.TagByteSizes.MaxSize);
        aesGcm.Encrypt(nonce, plaintext, ciphertext, tag);

        return (ciphertext, nonce, tag);
    }

    /// <summary>
    /// Decrypts byte array using AES-256-GCM.
    /// </summary>
    public static byte[] Decrypt(byte[] ciphertext, byte[] nonce, byte[] tag, byte[] key)
    {
        if (key.Length != 32)
            throw new ArgumentException("Key must be 256 bits (32 bytes).", nameof(key));

        byte[] plaintext = new byte[ciphertext.Length];
        using var aesGcm = new AesGcm(key, AesGcm.TagByteSizes.MaxSize);
        aesGcm.Decrypt(nonce, ciphertext, tag, plaintext);

        return plaintext;
    }

    /// <summary>
    /// Computes SHA256 hex string for deduplication & integrity check.
    /// </summary>
    public static string ComputeSha256(byte[] data)
    {
        byte[] hash = SHA256.HashData(data);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    /// <summary>
    /// Generates a human-friendly pairing code like "RPM-8821" or "RPM-7345".
    /// </summary>
    public static string GeneratePairingCode()
    {
        int number = RandomNumberGenerator.GetInt32(1000, 9999);
        return $"RPM-{number}";
    }
}

