namespace RPM.Core.Models;

public static class PrintJobStatusMapper
{
    public static PrintJobStatus FromCloud(string? status, PrintJobStatus fallback)
    {
        return status?.Trim().ToUpperInvariant() switch
        {
            "COMPLETED" or "SPOOLER_ACCEPTED" => PrintJobStatus.Completed,
            "FAILED" or "CANCELED" => PrintJobStatus.Failed,
            "UNKNOWN" => PrintJobStatus.Unknown,
            "DOWNLOADING" or "SPOOLING" => PrintJobStatus.Printing,
            _ => fallback
        };
    }

    public static string MessageFromCloud(string? status) => status?.Trim().ToUpperInvariant() switch
    {
        "SPOOLER_ACCEPTED" => "Đã gửi đến máy in. Chưa xác minh được việc in ra giấy.",
        "UNKNOWN" => "Chưa xác định kết quả in; không tự động in lại.",
        "COMPLETED" => "Đã gửi đến máy in.",
        _ => $"Trạng thái máy chủ: {status}"
    };
}
