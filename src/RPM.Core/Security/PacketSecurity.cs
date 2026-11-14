using System.Security.Cryptography;
using System.Text;
using RPM.Core.Protocol;

namespace RPM.Core.Security;

public static class PacketSecurity
{
    public const int MaxNetworkPacketBytes = 16 * 1024 * 1024;
    public const int MaxPrintJobBytes = 32 * 1024 * 1024;
    public static readonly TimeSpan MaximumClockSkew = TimeSpan.FromMinutes(5);

    public static bool IsValidPairingCode(string pairingCode) =>
        pairingCode.StartsWith("RPM-", StringComparison.OrdinalIgnoreCase) &&
        pairingCode.Length == 10 &&
        pairingCode[4..].All(c => char.IsAsciiLetterOrDigit(c));

    public static string ComputeAuthTag(NetworkPacket packet, string pairingCode)
    {
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(pairingCode));
        return Convert.ToBase64String(hmac.ComputeHash(Encoding.UTF8.GetBytes(Canonicalize(packet))));
    }

    public static bool VerifyAuthTag(NetworkPacket packet, string pairingCode)
    {
        if (string.IsNullOrWhiteSpace(packet.AuthTagBase64) || !IsValidPairingCode(pairingCode))
        {
            return false;
        }

        try
        {
            byte[] actual = Convert.FromBase64String(packet.AuthTagBase64);
            byte[] expected = Convert.FromBase64String(ComputeAuthTag(packet, pairingCode));
            return CryptographicOperations.FixedTimeEquals(actual, expected);
        }
        catch (FormatException)
        {
            return false;
        }
    }

    public static bool IsFresh(NetworkPacket packet, DateTime nowUtc) =>
        Math.Abs((nowUtc - packet.Timestamp.ToUniversalTime()).TotalSeconds) <= MaximumClockSkew.TotalSeconds;

    private static string Canonicalize(NetworkPacket packet) => string.Join("\n",
        packet.PacketId,
        (int)packet.Type,
        packet.SenderId,
        packet.TargetId,
        packet.PairingCode,
        packet.Timestamp.ToUniversalTime().Ticks,
        packet.PayloadJson,
        packet.EncryptedPayloadBase64,
        packet.NonceBase64,
        packet.TagBase64);
}
