using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace RPM.Core.Cloud;

public static class CloudDocumentCrypto
{
    private static readonly byte[] Magic = Encoding.ASCII.GetBytes("RPM2");
    private const int NonceSize = 12;
    private const int TagSize = 16;
    private const int KeySize = 32;

    public static string ComputePublicKeyFingerprint(string publicKeyBase64)
    {
        if (string.IsNullOrWhiteSpace(publicKeyBase64)) throw new ArgumentException("Public key is required.", nameof(publicKeyBase64));
        byte[] keyBytes = Convert.FromBase64String(publicKeyBase64);
        return Convert.ToHexString(SHA256.HashData(keyBytes)).ToLowerInvariant();
    }

    public static byte[] EncryptForHost(ReadOnlySpan<byte> plaintext, string hostPublicKeyBase64)
    {
        using var hostKey = RSA.Create();
        hostKey.ImportSubjectPublicKeyInfo(Convert.FromBase64String(hostPublicKeyBase64), out _);
        var documentKey = RandomNumberGenerator.GetBytes(KeySize);
        var nonce = RandomNumberGenerator.GetBytes(NonceSize);
        var ciphertext = new byte[plaintext.Length];
        var tag = new byte[TagSize];
        using var aes = new AesGcm(documentKey, TagSize);
        aes.Encrypt(nonce, plaintext, ciphertext, tag);
        var wrappedKey = hostKey.Encrypt(documentKey, RSAEncryptionPadding.OaepSHA256);

        var output = new byte[Magic.Length + 1 + NonceSize + TagSize + sizeof(int) + wrappedKey.Length + ciphertext.Length];
        Magic.CopyTo(output, 0);
        output[Magic.Length] = 1;
        nonce.CopyTo(output, Magic.Length + 1);
        tag.CopyTo(output, Magic.Length + 1 + NonceSize);
        BinaryPrimitives.WriteInt32LittleEndian(output.AsSpan(Magic.Length + 1 + NonceSize + TagSize), wrappedKey.Length);
        wrappedKey.CopyTo(output, Magic.Length + 1 + NonceSize + TagSize + sizeof(int));
        ciphertext.CopyTo(output, Magic.Length + 1 + NonceSize + TagSize + sizeof(int) + wrappedKey.Length);
        CryptographicOperations.ZeroMemory(documentKey);
        return output;
    }

    public static byte[] DecryptForThisDevice(ReadOnlySpan<byte> envelope, CloudDeviceKeyStore keyStore)
    {
        const int prefixSize = 4 + 1 + NonceSize + TagSize + sizeof(int);
        if (envelope.Length < prefixSize || !envelope[..Magic.Length].SequenceEqual(Magic) || envelope[Magic.Length] != 1)
            throw new CryptographicException("Invalid document envelope.");
        var wrappedLength = BinaryPrimitives.ReadInt32LittleEndian(envelope.Slice(Magic.Length + 1 + NonceSize + TagSize, sizeof(int)));
        if (wrappedLength <= 0 || envelope.Length < prefixSize + wrappedLength)
            throw new CryptographicException("Invalid wrapped key.");
        using var hostKey = keyStore.LoadOrCreate();
        var wrappedKey = envelope.Slice(prefixSize, wrappedLength).ToArray();
        var documentKey = hostKey.Decrypt(wrappedKey, RSAEncryptionPadding.OaepSHA256);
        var ciphertext = envelope[(prefixSize + wrappedLength)..];
        var plaintext = new byte[ciphertext.Length];
        using var aes = new AesGcm(documentKey, TagSize);
        aes.Decrypt(envelope.Slice(Magic.Length + 1, NonceSize), ciphertext, envelope.Slice(Magic.Length + 1 + NonceSize, TagSize), plaintext);
        CryptographicOperations.ZeroMemory(documentKey);
        return plaintext;
    }
}
