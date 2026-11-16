using System.Diagnostics;
using System.Drawing.Printing;
using System.Management;

namespace RPM.Printing;

public static class VirtualPrinterManager
{
    public const string DefaultPrinterName = "Remote Print Manager";

    public static bool IsVirtualPrinterInstalled(string printerName)
    {
        foreach (string installed in PrinterSettings.InstalledPrinters)
        {
            if (string.Equals(installed, printerName, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }

    public static string? FindPrinterBoundToLocalIpp(int port)
    {
        if (!OperatingSystem.IsWindows()) return null;
        string marker = $":{port}";
        try
        {
            using var searcher = new ManagementObjectSearcher("SELECT Name, PortName FROM Win32_Printer");
            foreach (ManagementObject printer in searcher.Get())
            {
                string name = printer["Name"]?.ToString() ?? string.Empty;
                string portName = printer["PortName"]?.ToString() ?? string.Empty;
                if (!string.IsNullOrWhiteSpace(name) &&
                    portName.Contains("127.0.0.1", StringComparison.OrdinalIgnoreCase) &&
                    portName.Contains(marker, StringComparison.OrdinalIgnoreCase))
                    return name;
            }
        }
        catch { }

        return null;
    }

    public static async Task<(bool Success, string Message)> InstallVirtualPrinterAsync(string printerName, int port = 9199)
    {
        if (IsVirtualPrinterInstalled(printerName))
        {
            return (true, $"Máy in '{printerName}' đã tồn tại trong hệ thống.");
        }

        string portUrl = $"http://127.0.0.1:{port}/printers/rpm";
        string safePortUrl = EscapePowerShellLiteral(portUrl);
        string safePrinterName = EscapePowerShellLiteral(printerName);

        // PowerShell script to create IPP port and register printer with built-in Windows driver
        string script = $@"
$ErrorActionPreference = 'Stop'
$portName = '{safePortUrl}'
$printerName = '{safePrinterName}'

# Select a driver that can feed an application-managed IPP queue.
$driver = $null
$installedDrivers = (Get-PrinterDriver).Name
foreach ($candidate in @(
    'Microsoft IPP Class Driver',
    'Universal Print Class Driver',
    'Microsoft enhanced Point and Print compatibility driver'
)) {{
    $match = $installedDrivers | Where-Object {{ $_ -ieq $candidate }} | Select-Object -First 1
    if ($match) {{ $driver = $match; break }}
}}
if (-not $driver) {{
    throw 'Không tìm thấy Windows IPP Class Driver tương thích.'
}}

if (-not (Get-PrinterPort -Name $portName -ErrorAction SilentlyContinue)) {{
    Add-PrinterPort -Name $portName -ErrorAction Stop
}}

try {{
    Add-Printer -Name $printerName -PortName $portName -DriverName $driver
    Write-Output 'OK'
}} catch {{
    Write-Error $_.Exception.Message
}}
";

        return await Task.Run(() =>
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "powershell.exe",
                    Arguments = $"-NoProfile -ExecutionPolicy Bypass -Command \"{EscapePowerShellArgument(script)}\"",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                using var proc = Process.Start(psi);
                if (proc == null)
                {
                    return (false, "Không thể khởi động tiến trình PowerShell.");
                }

                string stdout = proc.StandardOutput.ReadToEnd();
                string stderr = proc.StandardError.ReadToEnd();
                proc.WaitForExit(15000);

                if (IsVirtualPrinterInstalled(printerName))
                {
                    return (true, $"Đã cài đặt thành công máy in ảo '{printerName}' vào Windows!");
                }
                else
                {
                    string err = string.IsNullOrWhiteSpace(stderr) ? stdout : stderr;
                    return (false, $"Cài đặt máy in ảo thất bại: {err}");
                }
            }
            catch (Exception ex)
            {
                return (false, $"Lỗi: {ex.Message}");
            }
        });
    }

    public static async Task<bool> RemoveVirtualPrinterAsync(string printerName)
    {
        string safePrinterName = EscapePowerShellLiteral(printerName);
        string script = $"Remove-Printer -Name '{safePrinterName}' -ErrorAction SilentlyContinue";
        return await Task.Run(() =>
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "powershell.exe",
                    Arguments = $"-NoProfile -ExecutionPolicy Bypass -Command \"{EscapePowerShellArgument(script)}\"",
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                using var proc = Process.Start(psi);
                proc?.WaitForExit(10000);
                return !IsVirtualPrinterInstalled(printerName);
            }
            catch
            {
                return false;
            }
        });
    }

    private static string EscapePowerShellLiteral(string value) => value.Replace("'", "''", StringComparison.Ordinal);

    private static string EscapePowerShellArgument(string value) => value.Replace("\"", "\\\"", StringComparison.Ordinal);
}

