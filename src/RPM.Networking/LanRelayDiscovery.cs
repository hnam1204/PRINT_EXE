using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using RPM.Core.Networking;

namespace RPM.Networking;

/// <summary>
/// Discovers a Relay host on the local network without exposing a pairing code.
/// The pairing code is still required by the authenticated WebSocket handshake.
/// </summary>
public static class LanRelayDiscovery
{
    public const int DefaultDiscoveryPort = 9091;
    private const string Request = "RPM_DISCOVER_RELAY_V1";
    private const string AdvertisementPrefix = "RPM_RELAY_ADVERTISEMENT_V1";

    public static string BuildAdvertisement(string address, int relayPort)
    {
        if (!IPAddress.TryParse(address, out var ip) || !IsAdvertisableAddress(ip))
            throw new ArgumentException("Discovery address must be a private IPv4 address.", nameof(address));
        if (relayPort is < 1 or > 65535) throw new ArgumentOutOfRangeException(nameof(relayPort));
        return $"{AdvertisementPrefix}|{ip}|{relayPort}";
    }

    public static bool TryParseAdvertisement(string? value, out string endpoint)
    {
        endpoint = string.Empty;
        if (string.IsNullOrWhiteSpace(value)) return false;
        string[] parts = value.Split('|', StringSplitOptions.TrimEntries);
        if (parts.Length != 3 || !string.Equals(parts[0], AdvertisementPrefix, StringComparison.Ordinal)) return false;
        if (!IPAddress.TryParse(parts[1], out var ip) || !IsAdvertisableAddress(ip)) return false;
        if (!int.TryParse(parts[2], out int port) || port is < 1 or > 65535) return false;
        endpoint = $"ws://{ip}:{port}/";
        return true;
    }

    public static async Task<IReadOnlyList<string>> DiscoverAsync(
        int relayPort = 9090,
        int discoveryPort = DefaultDiscoveryPort,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        if (relayPort is < 1 or > 65535) throw new ArgumentOutOfRangeException(nameof(relayPort));
        if (discoveryPort is < 1 or > 65535) throw new ArgumentOutOfRangeException(nameof(discoveryPort));

        using var udp = new UdpClient(AddressFamily.InterNetwork) { EnableBroadcast = true };
        byte[] request = Encoding.ASCII.GetBytes(Request);
        var targets = GetBroadcastAddresses().Prepend(IPAddress.Broadcast).Distinct().ToList();
        foreach (var target in targets)
        {
            try
            {
                await udp.SendAsync(request, request.Length, new IPEndPoint(target, discoveryPort));
            }
            catch (SocketException)
            {
                // One blocked network adapter must not prevent discovery on others.
            }
        }

        var results = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        DateTime deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(2));
        while (DateTime.UtcNow < deadline && !cancellationToken.IsCancellationRequested)
        {
            TimeSpan remaining = deadline - DateTime.UtcNow;
            var receiveTask = udp.ReceiveAsync(cancellationToken).AsTask();
            var completed = await Task.WhenAny(receiveTask, Task.Delay(remaining, cancellationToken));
            if (completed != receiveTask) break;

            UdpReceiveResult response = await receiveTask;
            string payload = Encoding.ASCII.GetString(response.Buffer);
            if (!TryParseAdvertisement(payload, out var advertised)) continue;

            // Use the packet source address rather than trusting an advertised host address.
            if (!RelayEndpointPolicy.IsPrivateNetworkHost(response.RemoteEndPoint.Address.ToString())) continue;
            var advertisedUri = new Uri(advertised);
            string endpoint = $"ws://{response.RemoteEndPoint.Address}:{advertisedUri.Port}/";
            results.Add(endpoint);
        }

        return results.ToArray();
    }

    public static IEnumerable<IPAddress> GetBroadcastAddresses()
    {
        foreach (var adapter in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (adapter.OperationalStatus != OperationalStatus.Up ||
                adapter.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel)
                continue;

            IPInterfaceProperties properties;
            try { properties = adapter.GetIPProperties(); }
            catch { continue; }

            foreach (var address in properties.UnicastAddresses)
            {
                if (address.Address.AddressFamily != AddressFamily.InterNetwork || address.IPv4Mask is null) continue;
                byte[] ip = address.Address.GetAddressBytes();
                byte[] mask = address.IPv4Mask.GetAddressBytes();
                byte[] broadcast = new byte[4];
                for (int i = 0; i < broadcast.Length; i++) broadcast[i] = (byte)(ip[i] | (mask[i] ^ 255));
                yield return new IPAddress(broadcast);
            }
        }
    }

    public static IEnumerable<IPAddress> GetPrivateIpv4Addresses()
    {
        foreach (var adapter in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (adapter.OperationalStatus != OperationalStatus.Up ||
                adapter.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel)
                continue;

            IPInterfaceProperties properties;
            try { properties = adapter.GetIPProperties(); }
            catch { continue; }

            foreach (var address in properties.UnicastAddresses.Select(item => item.Address))
            {
                if (address.AddressFamily == AddressFamily.InterNetwork &&
                    !IPAddress.IsLoopback(address) &&
                    RelayEndpointPolicy.IsPrivateNetworkHost(address.ToString()))
                    yield return address;
            }
        }
    }

    internal static bool IsDiscoveryRequest(byte[] payload) =>
        string.Equals(Encoding.ASCII.GetString(payload), Request, StringComparison.Ordinal);

    internal static byte[] CreateAdvertisement(string address, int relayPort) =>
        Encoding.ASCII.GetBytes(BuildAdvertisement(address, relayPort));

    private static bool IsAdvertisableAddress(IPAddress address) =>
        address.AddressFamily == AddressFamily.InterNetwork &&
        !IPAddress.IsLoopback(address) &&
        RelayEndpointPolicy.IsPrivateNetworkHost(address.ToString());
}
