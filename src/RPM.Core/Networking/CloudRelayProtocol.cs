using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace RPM.Core.Networking;

/// <summary>
/// Messages exchanged with the public WSS data-plane gateway. The gateway
/// routes opaque packet JSON; it never receives the document plaintext.
/// </summary>
public sealed record CloudRelayBindMessage(
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("pairingId")] string PairingId,
    [property: JsonPropertyName("hostId")] string HostId,
    [property: JsonPropertyName("clientId")] string ClientId,
    [property: JsonPropertyName("deviceId")] string DeviceId,
    [property: JsonPropertyName("wrappedSessionKey")] string? WrappedSessionKey = null,
    [property: JsonPropertyName("role")] string Role = "");

public sealed record CloudRelayPacketMessage(
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("pairingId")] string PairingId,
    [property: JsonPropertyName("senderId")] string SenderId,
    [property: JsonPropertyName("targetId")] string? TargetId,
    [property: JsonPropertyName("packetJson")] string PacketJson,
    [property: JsonPropertyName("sessionMac")] string SessionMac);

public static class CloudRelayProtocol
{
    public const string AuthChannel = "relay";
    public const string BindType = "RELAY_BIND";
    public const string PacketType = "RELAY_PACKET";
    public const string KeyType = "RELAY_KEY";
    public const string KeyAckType = "RELAY_KEY_ACK";
    public const string ReadyType = "RELAY_READY";
    public const string AuthenticatedType = "RELAY_AUTHENTICATED";

    public static string ComputePacketMac(string sessionSecret, string pairingId, string senderId, string packetJson)
    {
        if (string.IsNullOrWhiteSpace(sessionSecret)) throw new ArgumentException("Session secret is required.", nameof(sessionSecret));
        byte[] key = Convert.FromBase64String(sessionSecret);
        string canonical = string.Join("\n", pairingId, senderId, packetJson);
        using var hmac = new HMACSHA256(key);
        return Convert.ToBase64String(hmac.ComputeHash(Encoding.UTF8.GetBytes(canonical)));
    }

    public static bool VerifyPacketMac(CloudRelayPacketMessage message, string sessionSecret)
    {
        try
        {
            byte[] actual = Convert.FromBase64String(message.SessionMac);
            byte[] expected = Convert.FromBase64String(ComputePacketMac(sessionSecret, message.PairingId, message.SenderId, message.PacketJson));
            return CryptographicOperations.FixedTimeEquals(actual, expected);
        }
        catch (FormatException)
        {
            return false;
        }
    }

    public static string WrapSessionKey(string hostPublicKeyBase64, byte[] sessionKey)
    {
        if (sessionKey.Length != 32) throw new ArgumentException("Relay session key must be 256 bits.", nameof(sessionKey));
        using var rsa = RSA.Create();
        rsa.ImportSubjectPublicKeyInfo(Convert.FromBase64String(hostPublicKeyBase64), out _);
        return Convert.ToBase64String(rsa.Encrypt(sessionKey, RSAEncryptionPadding.OaepSHA256));
    }

    public static byte[] UnwrapSessionKey(string wrappedSessionKey, RSA privateKey)
    {
        byte[] key = privateKey.Decrypt(Convert.FromBase64String(wrappedSessionKey), RSAEncryptionPadding.OaepSHA256);
        if (key.Length != 32) throw new CryptographicException("Invalid relay session key length.");
        return key;
    }

    public static string ToJson<T>(T message) => JsonSerializer.Serialize(message);
}
