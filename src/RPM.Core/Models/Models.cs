namespace RPM.Core.Models;

public enum AppRole
{
    Unconfigured = 0,
    Server = 1,
    Client = 2
}

public enum PrintJobStatus
{
    Pending = 0,
    Printing = 1,
    Completed = 2,
    Failed = 3,
    Cancelled = 4
}

public class DeviceInfo
{
    public string DeviceId { get; set; } = string.Empty;
    public string DeviceName { get; set; } = string.Empty;
    public string IpAddress { get; set; } = "127.0.0.1";
    public string OperatingSystem { get; set; } = string.Empty;
    public DateTime FirstConnected { get; set; } = DateTime.UtcNow;
    public DateTime LastSeen { get; set; } = DateTime.UtcNow;
    public bool IsApproved { get; set; } = false;
    public bool IsBlocked { get; set; } = false;
    public bool IsOnline { get; set; } = false;

    // Extensibility & Enterprise fleet management
    public string Department { get; set; } = "Chung";
    public List<string> AllowedPrinters { get; set; } = new();
    public int PrintJobsCount { get; set; } = 0;

    [System.Text.Json.Serialization.JsonIgnore]
    public string AllowedPrintersSummary => AllowedPrinters == null || AllowedPrinters.Count == 0
        ? "Tất cả máy in chia sẻ"
        : string.Join(", ", AllowedPrinters);
}

public class SharedPrinterInfo
{
    public string Name { get; set; } = string.Empty;
    public string DriverName { get; set; } = string.Empty;
    public string PortName { get; set; } = string.Empty;
    public bool IsDefault { get; set; } = false;
    public bool IsShared { get; set; } = false;
    public string Status { get; set; } = "Ready";
    public bool SupportsColor { get; set; } = false;
    public bool SupportsDuplex { get; set; } = false;
    public List<string> PaperSizes { get; set; } = new();

    // Live hardware and port detection
    public bool IsUsb => !string.IsNullOrEmpty(PortName) && PortName.StartsWith("USB", StringComparison.OrdinalIgnoreCase);
    public string ConnectionDisplay => IsUsb ? $"USB ({PortName})" : (string.IsNullOrEmpty(PortName) ? "Hệ thống" : PortName);
    public int JobsInSpooler { get; set; } = 0;
}

public class PrintJob
{
    public string JobId { get; set; } = Guid.NewGuid().ToString("N");
    public string DocumentName { get; set; } = "Document";
    public string TargetPrinter { get; set; } = string.Empty;
    public string SenderDeviceId { get; set; } = string.Empty;
    public string SenderDeviceName { get; set; } = string.Empty;
    public string Department { get; set; } = string.Empty;
    public DateTime SubmittedAt { get; set; } = DateTime.UtcNow;
    public DateTime? CompletedAt { get; set; }
    public int PageCount { get; set; } = 1;
    public int Copies { get; set; } = 1;
    public string PaperSize { get; set; } = "A4";
    public string Orientation { get; set; } = "Portrait";
    public bool IsColor { get; set; } = false;
    public bool IsDuplex { get; set; } = false;
    public PrintJobStatus Status { get; set; } = PrintJobStatus.Pending;
    public string StatusMessage { get; set; } = "Pending in queue";
    public long FileSizeBytes { get; set; }
    public string ContentSha256 { get; set; } = string.Empty;
    public string? LocalTempFilePath { get; set; }

    public string FormattedFileSize => FileSizeBytes switch
    {
        >= 1024 * 1024 => $"{(double)FileSizeBytes / (1024 * 1024):F1} MB",
        >= 1024 => $"{(double)FileSizeBytes / 1024:F0} KB",
        _ => $"{FileSizeBytes} B"
    };

    public string StatusText => Status switch
    {
        PrintJobStatus.Pending => "Đang chờ",
        PrintJobStatus.Printing => "Đang in",
        PrintJobStatus.Completed => "Hoàn tất",
        PrintJobStatus.Failed => "Lỗi",
        PrintJobStatus.Cancelled => "Đã hủy",
        _ => Status.ToString()
    };

    public string ClientDeviceName => !string.IsNullOrEmpty(SenderDeviceName) ? SenderDeviceName : (!string.IsNullOrEmpty(SenderDeviceId) ? SenderDeviceId : "Máy con");
    public string ErrorMessage => StatusMessage;
}

