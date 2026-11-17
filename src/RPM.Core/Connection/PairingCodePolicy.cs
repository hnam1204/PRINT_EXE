using RPM.Core.Cloud;

namespace RPM.Core.Connection;

public sealed record CloudPairingIssueResult(
    bool Success,
    string? Code,
    string? SessionId,
    DateTime? ExpiresAt);

public static class PairingCodePolicy
{
    public static CloudPairingIssueResult ValidateCloudIssue(CloudPairingCodeResponse? response)
    {
        if (response is null ||
            !string.Equals(response.Status, "ACTIVE", StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(response.Code) ||
            string.IsNullOrWhiteSpace(response.SessionId) ||
            response.ExpiresAt <= DateTime.UtcNow)
        {
            return new CloudPairingIssueResult(false, null, null, null);
        }

        return new CloudPairingIssueResult(
            true,
            response.Code.Trim(),
            response.SessionId.Trim(),
            response.ExpiresAt);
    }

    public static string GetPrimaryCode(bool cloudEnabled, string? cloudCode, string localCode) =>
        cloudEnabled
            ? string.IsNullOrWhiteSpace(cloudCode) ? "Chưa có mã kết nối dịch vụ" : cloudCode
            : localCode;
}
