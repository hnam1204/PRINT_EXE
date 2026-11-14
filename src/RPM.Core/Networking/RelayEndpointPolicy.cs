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

    public static bool TryValidateForRole(AppRole role, string endpoint, out string reason)
    {
        reason = string.Empty;
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) || uri.Scheme is not ("ws" or "wss"))
        {
            reason = "Địa chỉ Relay không hợp lệ.";
            return false;
        }

        if (role == AppRole.Client && IsLoopback(endpoint))
        {
            reason = "Máy con không được dùng localhost của chính nó để tìm máy chủ.";
            return false;
        }

        if (uri.Scheme == "ws" && !IsLoopback(endpoint))
        {
            reason = "Kết nối Relay ngoài máy phải dùng wss://.";
            return false;
        }

        return true;
    }
}
