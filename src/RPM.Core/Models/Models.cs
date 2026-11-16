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
    Cancelled = 4,
    // Keep UNKNOWN separate from a confirmed failure. A restart or lost
    // acknowledgement must never become an instruction to print again.
    Unknown = 5
}

public enum PrinterAccessMode
{
    AllShared = 0,
    SelectedOnly = 1,
    None = 2
}

public static class PrinterAccessPolicy
{
    public static PrinterAccessMode Normalize(PrinterAccessMode mode, IReadOnlyCollection<string>? allowedPrinters)
    {
        // Older settings only stored a list: a non-empty legacy list means selected-only.
        if (mode == PrinterAccessMode.AllShared && allowedPrinters is { Count: > 0 })
            return PrinterAccessMode.SelectedOnly;
        return mode;
    }

    public static IReadOnlyList<SharedPrinterInfo> Filter(
        IEnumerable<SharedPrinterInfo> sharedPrinters,
        PrinterAccessMode mode,
        IReadOnlyCollection<string>? allowedPrinters)
    {
        var shared = sharedPrinters
            .Where(printer => printer.IsShared)
            .ToList();
        return Normalize(mode, allowedPrinters) switch
        {
            PrinterAccessMode.None => Array.Empty<SharedPrinterInfo>(),
            PrinterAccessMode.SelectedOnly => shared
                .Where(printer => allowedPrinters?.Contains(printer.Name, StringComparer.OrdinalIgnoreCase) == true)
                .ToList(),
            _ => shared
        };
    }

    public static bool Allows(PrinterAccessMode mode, IReadOnlyCollection<string>? allowedPrinters, string printerName)
    {
        return Normalize(mode, allowedPrinters) switch
        {
            PrinterAccessMode.None => false,
            PrinterAccessMode.SelectedOnly => allowedPrinters?.Contains(printerName, StringComparer.OrdinalIgnoreCase) == true,
            _ => true
        };
    }

    public static string ToApiValue(PrinterAccessMode mode) => mode switch
    {
        PrinterAccessMode.SelectedOnly => "SELECTED_ONLY",
        PrinterAccessMode.None => "NONE",
        _ => "ALL_SHARED"
    };

    public static PrinterAccessMode FromApiValue(string? value) => value?.Trim().ToUpperInvariant() switch
    {
        "SELECTED_ONLY" => PrinterAccessMode.SelectedOnly,
        "NONE" => PrinterAccessMode.None,
        _ => PrinterAccessMode.AllShared
    };
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
    public bool ReconnectAllowed { get; set; } = false;
    public bool IsOnline { get; set; } = false;
    public string PairingId { get; set; } = string.Empty;

    // Extensibility & Enterprise fleet management
    public string Department { get; set; } = "Chung";
    public List<string> AllowedPrinters { get; set; } = new();
    public PrinterAccessMode PrinterPermissionMode { get; set; } = PrinterAccessMode.AllShared;
    public int PrintJobsCount { get; set; } = 0;

    [System.Text.Json.Serialization.JsonIgnore]
    public string AllowedPrintersSummary => AllowedPrinters == null || AllowedPrinters.Count == 0
        ? (EffectivePrinterPermissionMode == PrinterAccessMode.AllShared ? "Tất cả máy in chia sẻ" : "Không được cấp máy in")
        : string.Join(", ", AllowedPrinters);

    [System.Text.Json.Serialization.JsonIgnore]
    public PrinterAccessMode EffectivePrinterPermissionMode => PrinterAccessPolicy.Normalize(PrinterPermissionMode, AllowedPrinters);

    [System.Text.Json.Serialization.JsonIgnore]
    public string ConnectionStatus => IsOnline ? "Máy đang trực tuyến" : "Máy đang ngoại tuyến";

    [System.Text.Json.Serialization.JsonIgnore]
    public string PermissionStatus => IsBlocked ? "Đã từ chối" : ReconnectAllowed ? "Được phép kết nối lại" : !IsApproved ? "Chờ duyệt" : "Đã được chấp nhận";

    [System.Text.Json.Serialization.JsonIgnore]
    public string DeviceStatus => PermissionStatus;
}

public static class DeviceInfoPolicy
{
    public static bool IsRemoteDeviceId(string deviceId, string currentDeviceId)
    {
        return !string.IsNullOrWhiteSpace(deviceId) &&
            !string.Equals(deviceId, currentDeviceId, StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsRemoteDevice(DeviceInfo device, string currentDeviceId)
    {
        return device is not null && IsRemoteDeviceId(device.DeviceId, currentDeviceId);
    }
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
    public string DocumentFormat { get; set; } = "application/octet-stream";
    public string Orientation { get; set; } = "Portrait";
    public bool IsColor { get; set; } = false;
    public bool IsDuplex { get; set; } = false;
    public PrintJobStatus Status { get; set; } = PrintJobStatus.Pending;
    public string StatusMessage { get; set; } = "Đang chờ xử lý.";
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
        PrintJobStatus.Printing => StatusMessage.Contains("nhận", StringComparison.OrdinalIgnoreCase)
            ? "Đang nhận tài liệu"
            : "Đang gửi đến máy in",
        PrintJobStatus.Completed => StatusMessage.Contains("Chưa xác minh", StringComparison.OrdinalIgnoreCase)
            ? "Đã gửi đến máy in"
            : "Đã xác nhận hoàn tất",
        PrintJobStatus.Failed => "Không thể in",
        PrintJobStatus.Cancelled => "Đã hủy",
        PrintJobStatus.Unknown => "Chưa xác định",
        _ => Status.ToString()
    };

    public string ClientDeviceName => !string.IsNullOrEmpty(SenderDeviceName) ? SenderDeviceName : (!string.IsNullOrEmpty(SenderDeviceId) ? SenderDeviceId : "Máy con");
    public string ErrorMessage => StatusMessage;
}

