using System.Diagnostics;
using System.Drawing.Printing;

namespace RPM.Printing;

public static class VirtualPrinterManager
{
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

    public static async Task<(bool Success, string Message)> InstallVirtualPrinterAsync(string printerName, int port = 9199)
    {
        if (IsVirtualPrinterInstalled(printerName))
        {
            return (true, $"Máy in '{printerName}' đã tồn tại trong hệ thống.");
        }

        string portUrl = $"http://127.0.0.1:{port}/printers/rpm";

        // PowerShell script to create IPP port and register printer with built-in Windows driver
        string script = $@"
$ErrorActionPreference = 'Stop'
$portName = '{portUrl}'
$printerName = '{printerName}'

# Determine best available built-in driver
$driver = 'Microsoft Print to PDF'
$installedDrivers = (Get-PrinterDriver).Name
if ($installedDrivers -contains 'Microsoft Software Printer Driver') {{
    $driver = 'Microsoft Software Printer Driver'
}} elseif ($installedDrivers -contains 'Microsoft Print to PDF') {{
    $driver = 'Microsoft Print to PDF'
}} elseif ($installedDrivers -contains 'Generic / Text Only') {{
    $driver = 'Generic / Text Only'
}}

try {{
    Add-PrinterPort -Name $portName -ErrorAction SilentlyContinue
}} catch {{}}

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
                    Arguments = $"-NoProfile -ExecutionPolicy Bypass -Command \"{script.Replace("\"", "\\\"")}\"",
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
        string script = $"Remove-Printer -Name '{printerName}' -ErrorAction SilentlyContinue";
        return await Task.Run(() =>
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "powershell.exe",
                    Arguments = $"-NoProfile -ExecutionPolicy Bypass -Command \"{script}\"",
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
}

