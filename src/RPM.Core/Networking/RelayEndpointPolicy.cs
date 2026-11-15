using System.Net;
using RPM.Core.Config;
using RPM.Core.Models;

namespace RPM.Core.Networking;

public static class RelayEndpointPolicy
{
    public static bool IsLoopback(string endpoint)
    {
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri)) return false;
        if (string.Equals(uri.Host, "localhost", StringComparison.OrdinalIgnoreCase)) return true;
        return IPAddress.TryParse(uri.Host, out var address) && IPAddress.IsLoopback(address);
    }

    public static bool IsPrivateNetworkHost(string host)
    {
        if (!IPAddress.TryParse(host, out var address)) return false;
        if (IPAddress.IsLoopback(address)) return true;
        if (address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
        {
            byte[] bytes = address.GetAddressBytes();
            return bytes[0] == 10
                || (bytes[0] == 172 && bytes[1] is >= 16 and <= 31)
                || (bytes[0] == 192 && bytes[1] == 168)
                || (bytes[0] == 169 && bytes[1] == 254);
        }

        return address.IsIPv6LinkLocal || address.IsIPv6UniqueLocal;
    }

    public static bool TryValidateForRole(AppRole role, string endpoint, out string reason)
    {
        reason = string.Empty;
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) || uri.Scheme is not ("ws" or "wss"))
        {
            reason = "Địa chỉ kết nối nội bộ không hợp lệ.";
            return false;
        }

        if (role == AppRole.Client && IsLoopback(endpoint))
        {
            reason = "Máy con không được dùng localhost của chính nó để tìm máy chủ.";
            return false;
        }

        if (uri.Scheme == "ws" && !IsPrivateNetworkHost(uri.Host))
        {
            reason = "ws:// chỉ được phép tới IP mạng LAN riêng (10.x, 172.16-31.x, 192.168.x hoặc link-local); Internet phải dùng wss://.";
            return false;
        }

        return true;
    }
}
