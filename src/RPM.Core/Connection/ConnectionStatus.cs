namespace RPM.Core.Connection;

public enum ActivationStatus
{
    NotActivated,
    VerificationPending,
    Valid,
    Revoked,
    Expired,
    Invalid
}

public enum ConnectionMode
{
    None,
    Cloud,
    Lan
}

public enum TransportStatus
{
    Offline,
    Online,
    Degraded
}

public sealed record ConnectionStatusSnapshot(
    ActivationStatus ActivationStatus,
    ConnectionMode ConnectionMode,
    TransportStatus TransportStatus,
    string PrimaryText,
    string? SecondaryText,
    bool IsChecking = false)
{
    public bool IsPrimaryConnected => TransportStatus == TransportStatus.Online;
}

/// <summary>
/// Resolves the one user-facing connection state used by every WPF surface.
/// Stored activation metadata is intentionally not an input: only a backend
/// validation result can produce ActivationStatus.Valid.
/// </summary>
public static class ConnectionStatusResolver
{
    public static bool IsPreviouslyVerifiedActivation(bool hasStoredToken, DateTime? lastVerifiedAtUtc) =>
        hasStoredToken && lastVerifiedAtUtc.HasValue;

    public static string GetActivationText(ActivationStatus status) => status switch
    {
        ActivationStatus.Valid => "Mã kích hoạt đã được xác minh",
        ActivationStatus.VerificationPending => "Đang xác minh mã kích hoạt",
        ActivationStatus.Revoked => "Mã kích hoạt đã bị thu hồi",
        ActivationStatus.Expired => "Mã kích hoạt đã hết hạn",
        ActivationStatus.Invalid => "Mã kích hoạt không hợp lệ",
        _ => "Chưa kích hoạt dịch vụ"
    };

    public static ConnectionStatusSnapshot Resolve(
        ActivationStatus activationStatus,
        bool cloudOnline,
        bool relayOnline,
        bool previouslyVerified = false,
        bool startupReady = true)
    {
        if (!startupReady)
        {
            return new ConnectionStatusSnapshot(
                activationStatus,
                ConnectionMode.None,
                TransportStatus.Offline,
                "Đang kiểm tra kết nối dịch vụ...",
                null,
                IsChecking: true);
        }

        if (activationStatus == ActivationStatus.Valid)
        {
            if (cloudOnline)
            {
                return new ConnectionStatusSnapshot(
                    activationStatus,
                    ConnectionMode.Cloud,
                    TransportStatus.Online,
                    "Đã kết nối dịch vụ",
                    relayOnline ? "Kết nối nội bộ vẫn sẵn sàng" : null);
            }

            return new ConnectionStatusSnapshot(
                activationStatus,
                relayOnline ? ConnectionMode.Lan : ConnectionMode.None,
                relayOnline ? TransportStatus.Degraded : TransportStatus.Offline,
                "Mất kết nối dịch vụ",
                relayOnline ? "Kết nối nội bộ vẫn sẵn sàng" : null);
        }

        if (activationStatus is ActivationStatus.Revoked or ActivationStatus.Expired or ActivationStatus.Invalid)
        {
            string text = activationStatus switch
            {
                ActivationStatus.Revoked => "Mã kích hoạt đã bị thu hồi",
                ActivationStatus.Expired => "Mã kích hoạt đã hết hạn",
                _ => "Mã kích hoạt không hợp lệ"
            };

            return new ConnectionStatusSnapshot(
                activationStatus,
                relayOnline ? ConnectionMode.Lan : ConnectionMode.None,
                relayOnline ? TransportStatus.Degraded : TransportStatus.Offline,
                text,
                relayOnline ? "Kết nối nội bộ vẫn sẵn sàng" : null);
        }

        if (previouslyVerified && !cloudOnline)
        {
            return new ConnectionStatusSnapshot(
                activationStatus,
                relayOnline ? ConnectionMode.Lan : ConnectionMode.None,
                relayOnline ? TransportStatus.Degraded : TransportStatus.Offline,
                "Mất kết nối dịch vụ",
                relayOnline ? "Kết nối nội bộ vẫn sẵn sàng" : null);
        }

        return new ConnectionStatusSnapshot(
            activationStatus,
            relayOnline ? ConnectionMode.Lan : ConnectionMode.None,
            relayOnline ? TransportStatus.Online : TransportStatus.Offline,
            relayOnline ? "Đã kết nối nội bộ" : "Chưa kết nối nội bộ",
            null);
    }
}
