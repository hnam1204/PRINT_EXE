using RPM.Core.Models;

namespace RPM.Core.Cloud;

/// <summary>
/// Resolves the pairing IDs that must keep a Host relay alive. The persisted
/// DeviceInfo value is preferred, with the Cloud refresh map as a recovery path.
/// </summary>
public static class CloudPairingResolver
{
    public static IReadOnlySet<string> ResolveActivePairingIds(
        IEnumerable<DeviceInfo> clients,
        IReadOnlyDictionary<string, string> refreshedPairingIds,
        string currentDeviceId)
    {
        var result = new HashSet<string>(StringComparer.Ordinal);

        foreach (var client in clients)
        {
            if (!DeviceInfoPolicy.IsRemoteDevice(client, currentDeviceId) ||
                !client.IsApproved || client.IsBlocked)
            {
                continue;
            }

            string pairingId = client.PairingId;
            if (string.IsNullOrWhiteSpace(pairingId))
            {
                if (refreshedPairingIds.TryGetValue(client.DeviceId, out var refreshedPairingId))
                    pairingId = refreshedPairingId;
            }

            if (!string.IsNullOrWhiteSpace(pairingId))
                result.Add(pairingId);
        }

        return result;
    }
}
