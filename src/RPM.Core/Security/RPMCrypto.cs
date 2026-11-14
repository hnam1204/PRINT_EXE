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
        using var pbkdf2 = new Rfc2898DeriveBytes(secret, Salt, 600_000, HashAlgorithmName.SHA512);
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
    /// Generates a short pairing code like "RPM-A7K2Q9".
    /// </summary>
    public static string GeneratePairingCode()
    {
        const string alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
        Span<byte> random = stackalloc byte[6];
        Span<char> code = stackalloc char[6];
        int maxAcceptable = byte.MaxValue - (byte.MaxValue % alphabet.Length);
        int index = 0;
        while (index < code.Length)
        {
            RandomNumberGenerator.Fill(random);
            foreach (byte value in random)
            {
                if (value >= maxAcceptable) continue;
                code[index++] = alphabet[value % alphabet.Length];
                if (index == code.Length) break;
            }
        }

        return $"RPM-{new string(code)}";
    }

    public static string GeneratePairingCode(string previousCode)
    {
        string nextCode;
        do
        {
            nextCode = GeneratePairingCode();
        }
        while (string.Equals(nextCode, previousCode, StringComparison.OrdinalIgnoreCase));

        return nextCode;
    }
}

