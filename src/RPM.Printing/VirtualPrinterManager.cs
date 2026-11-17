using System.ComponentModel;
using System.Diagnostics;
using System.Drawing.Printing;
using System.IO;
using System.Management;
using System.Text;
using System.Text.Json;

namespace RPM.Printing;

public static class VirtualPrinterManager
{
    public const string DefaultPrinterName = "Remote Print Manager";
    private const string IppPath = "/printers/rpm";
    private const int InstallTimeoutMilliseconds = 120_000;

    public static bool IsVirtualPrinterInstalled(string printerName)
    {
        foreach (string installed in PrinterSettings.InstalledPrinters)
        {
            if (string.Equals(installed, printerName, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    public static string? FindPrinterBoundToLocalIpp(int port)
    {
        if (!OperatingSystem.IsWindows()) return null;

        try
        {
            using var searcher = new ManagementObjectSearcher("SELECT Name, PortName FROM Win32_Printer");
            foreach (ManagementObject printer in searcher.Get())
            {
                string name = printer["Name"]?.ToString() ?? string.Empty;
                string portName = printer["PortName"]?.ToString() ?? string.Empty;
                if (!string.IsNullOrWhiteSpace(name) && IsMatchingIppPort(portName, port))
                    return name;
            }
        }
        catch { }

        return null;
    }

    internal static bool IsMatchingIppPort(string? portName, int port)
    {
        if (string.IsNullOrWhiteSpace(portName) || port is < 1 or > 65535) return false;
        if (!Uri.TryCreate(portName.Trim().TrimEnd('/'), UriKind.Absolute, out var uri)) return false;
        if (!string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)) return false;
        if (uri.Port != port || !IsLoopbackHost(uri.Host)) return false;

        string path = string.IsNullOrWhiteSpace(uri.AbsolutePath) ? string.Empty : uri.AbsolutePath.TrimEnd('/');
        return string.IsNullOrEmpty(path) || string.Equals(path, IppPath, StringComparison.OrdinalIgnoreCase);
    }

    public static async Task<(bool Success, string Message)> InstallVirtualPrinterAsync(string printerName, int port = 9199)
    {
        if (!OperatingSystem.IsWindows())
            return (false, "Cài đặt hàng đợi Windows chỉ được hỗ trợ trên Windows.");
        if (string.IsNullOrWhiteSpace(printerName))
            return (false, "Tên hàng đợi máy in không được để trống.");
        if (port is < 1 or > 65535)
            return (false, $"Cổng IPP không hợp lệ: {port}.");

        return await Task.Run(() => RunElevatedInstallation(printerName.Trim(), port));
    }

    internal static string BuildNativeIppInstallScript(string printerName, int port)
    {
        string safePrinterName = EscapePowerShellLiteral(printerName);
        string safeIppUrl = EscapePowerShellLiteral(GetIppUrl(port));

        return $@"
param(
    [Parameter(Mandatory = $true)][string]$ResultPath
)

$ErrorActionPreference = 'Stop'
$printerName = '{safePrinterName}'
$ippUrl = '{safeIppUrl}'

function Write-InstallResult([bool]$success, [string]$stage, [string]$message, [object]$details = $null) {{
    $result = [ordered]@{{
        Success = $success
        Stage = $stage
        Message = $message
        Details = $details
    }}
    $result | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $ResultPath -Encoding UTF8
    if ($success) {{ exit 0 }}
    exit 1
}}

try {{
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = [Security.Principal.WindowsPrincipal]::new($identity)
    if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {{
        Write-InstallResult $false 'permissions' 'PowerShell cài đặt chưa chạy với quyền quản trị viên.'
    }}

    $addPrinter = Get-Command Add-Printer -ErrorAction Stop
    if (-not $addPrinter.Parameters.ContainsKey('IppURL')) {{
        Write-InstallResult $false 'capability' 'Windows PrintManagement không hỗ trợ tham số Add-Printer -IppURL.'
    }}

    $spooler = Get-Service -Name Spooler -ErrorAction Stop
    if ($spooler.Status -ne 'Running') {{
        Write-InstallResult $false 'spooler' (""Windows Print Spooler đang ở trạng thái: {{0}}."" -f $spooler.Status)
    }}

    $existing = Get-Printer -Name $printerName -ErrorAction SilentlyContinue
    if ($null -ne $existing) {{
        $existingPort = [string]$existing.PortName
        $existingDriver = [string]$existing.DriverName
        if ($existingPort.TrimEnd('/') -ieq $ippUrl.TrimEnd('/')) {{
            Write-InstallResult $true 'existing' (""Hàng đợi '{{0}}' đã tồn tại và đang dùng đúng IPP URL."" -f $printerName) @{{
                PrinterName = $existing.Name
                PortName = $existingPort
                DriverName = $existingDriver
            }}
        }}

        Write-InstallResult $false 'existing-queue' (""Hàng đợi '{{0}}' đã tồn tại nhưng đang trỏ tới cổng '{{1}}'. Không tự ý thay đổi hoặc xóa hàng đợi hiện có."" -f $printerName, $existingPort) @{{
            PrinterName = $existing.Name
            PortName = $existingPort
            DriverName = $existingDriver
            ExpectedIppUrl = $ippUrl
        }}
    }}

    $stage = 'ipp-probe'
    $probe = Invoke-WebRequest -Uri $ippUrl -Method Get -UseBasicParsing -TimeoutSec 10 -ErrorAction Stop
    if ($probe.StatusCode -lt 200 -or $probe.StatusCode -ge 300) {{
        Write-InstallResult $false $stage (""IPP endpoint trả về HTTP {{0}} thay vì 2xx."" -f $probe.StatusCode) @{{
            StatusCode = $probe.StatusCode
            Url = $ippUrl
        }}
    }}

    $stage = 'add-printer'
    Add-Printer -Name $printerName -IppURL $ippUrl -ErrorAction Stop
    Start-Sleep -Milliseconds 500

    $created = Get-Printer -Name $printerName -ErrorAction Stop
    $createdPort = [string]$created.PortName
    if ($createdPort.TrimEnd('/') -ine $ippUrl.TrimEnd('/')) {{
        Write-InstallResult $false 'verify-port' (""Windows đã tạo hàng đợi nhưng cổng thực tế là '{{0}}', không phải '{{1}}'. Không tự ý xóa hàng đợi."" -f $createdPort, $ippUrl) @{{
            PrinterName = $created.Name
            PortName = $createdPort
            DriverName = $created.DriverName
            ExpectedIppUrl = $ippUrl
        }}
    }}

    Write-InstallResult $true 'complete' (""Đã tạo hàng đợi '{{0}}' bằng Add-Printer -IppURL."" -f $printerName) @{{
        PrinterName = $created.Name
        PortName = $createdPort
        DriverName = $created.DriverName
    }}
}}
catch {{
    $details = [ordered]@{{
        ExceptionMessage = $_.Exception.Message
        FullyQualifiedErrorId = $_.FullyQualifiedErrorId
        Category = $_.CategoryInfo.ToString()
        ScriptStackTrace = $_.ScriptStackTrace
    }}
    Write-InstallResult $false 'powershell' $_.Exception.Message $details
}}
";
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

    private static (bool Success, string Message) RunElevatedInstallation(string printerName, int port)
    {
        string operationId = Guid.NewGuid().ToString("N");
        string tempFolder = Path.Combine(Path.GetTempPath(), "RemotePrintManager", "printer-install");
        string resultPath = Path.Combine(tempFolder, $"result-{operationId}.json");

        try
        {
            Directory.CreateDirectory(tempFolder);
            string script = BuildNativeIppInstallScript(printerName, port);
            string command = $"$script = [Text.Encoding]::Unicode.GetString([Convert]::FromBase64String('{Convert.ToBase64String(Encoding.Unicode.GetBytes(script))}')); & ([scriptblock]::Create($script)) -ResultPath '{EscapePowerShellLiteral(resultPath)}'";
            string encodedCommand = Convert.ToBase64String(Encoding.Unicode.GetBytes(command));

            var psi = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = $"-NoProfile -ExecutionPolicy Bypass -EncodedCommand {encodedCommand}",
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden
            };

            using var process = Process.Start(psi);
            if (process is null)
                return (false, "Không thể khởi động tiến trình cài đặt PowerShell nâng quyền.");

            if (!process.WaitForExit(InstallTimeoutMilliseconds))
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                return (false, "Cài đặt hàng đợi quá thời gian chờ 120 giây.");
            }

            if (File.Exists(resultPath))
                return ReadInstallResult(resultPath, process.ExitCode);

            return process.ExitCode == 0
                ? (false, "PowerShell kết thúc nhưng không trả về kết quả xác minh hàng đợi.")
                : (false, $"PowerShell cài đặt thất bại với mã thoát {process.ExitCode}.");
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            return (false, "Bạn đã hủy yêu cầu quyền quản trị viên; chưa thay đổi máy in hoặc cổng in.");
        }
        catch (Exception ex)
        {
            return (false, $"Không thể khởi động cài đặt hàng đợi: {ex.Message}");
        }
        finally
        {
            TryDelete(resultPath);
        }
    }

    private static (bool Success, string Message) ReadInstallResult(string resultPath, int exitCode)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(resultPath));
            JsonElement root = document.RootElement;
            bool success = root.TryGetProperty("Success", out var successValue) && successValue.GetBoolean();
            string stage = root.TryGetProperty("Stage", out var stageValue) ? stageValue.GetString() ?? "unknown" : "unknown";
            string message = root.TryGetProperty("Message", out var messageValue) ? messageValue.GetString() ?? "" : "";
            string details = root.TryGetProperty("Details", out var detailsValue) && detailsValue.ValueKind != JsonValueKind.Null
                ? detailsValue.ToString()
                : string.Empty;

            if (!success && !string.IsNullOrWhiteSpace(details))
                message = $"{message} [Giai đoạn: {stage}; Chi tiết: {details}]";
            else if (!success)
                message = $"{message} [Giai đoạn: {stage}; Mã thoát: {exitCode}]";

            return (success, message);
        }
        catch (Exception ex)
        {
            return (false, $"Không đọc được kết quả cài đặt PowerShell: {ex.Message}");
        }
    }

    private static string GetIppUrl(int port) => $"http://127.0.0.1:{port}{IppPath}";

    private static bool IsLoopbackHost(string host) =>
        string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(host, "127.0.0.1", StringComparison.OrdinalIgnoreCase);

    private static void TryDelete(string path)
    {
        try { if (!string.IsNullOrWhiteSpace(path) && File.Exists(path)) File.Delete(path); } catch { }
    }

    private static string EscapePowerShellLiteral(string value) => value.Replace("'", "''", StringComparison.Ordinal);

    private static string EscapePowerShellArgument(string value) => value.Replace("\"", "\\\"", StringComparison.Ordinal);
}
