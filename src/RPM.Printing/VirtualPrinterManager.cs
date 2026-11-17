using System.ComponentModel;
using System.Diagnostics;
using System.Drawing.Printing;
using System.IO;
using System.Management;
using Microsoft.Win32;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.NetworkInformation;
using System.Net;
using System.Net.Sockets;
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
        if (IsWsdIppPort(portName, LocalIppServer.GetStablePrinterUuid(port))) return true;
        if (!Uri.TryCreate(portName.Trim().TrimEnd('/'), UriKind.Absolute, out var uri)) return false;
        if (!uri.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) &&
            !uri.Scheme.Equals("ipp", StringComparison.OrdinalIgnoreCase)) return false;
        if (uri.Port != port || !IsLocalHost(uri.Host)) return false;

        string path = string.IsNullOrWhiteSpace(uri.AbsolutePath) ? string.Empty : uri.AbsolutePath.TrimEnd('/');
        return string.IsNullOrEmpty(path) || string.Equals(path, IppPath, StringComparison.OrdinalIgnoreCase);
    }

    internal static bool IsWsdIppPort(string portName, string? expectedPrinterUuid)
    {
        if (!OperatingSystem.IsWindows() || !portName.StartsWith("WSD-", StringComparison.OrdinalIgnoreCase))
            return false;

        try
        {
            using RegistryKey? key = Registry.LocalMachine.OpenSubKey(
                $@"SYSTEM\CurrentControlSet\Control\Print\Monitors\WSD Port\Ports\{portName}");
            string? printerUuid = key?.GetValue("Printer UUID")?.ToString();
            string? ippPortId = key?.GetValue("IPP PortId")?.ToString();
            return !string.IsNullOrWhiteSpace(ippPortId) &&
                (string.IsNullOrWhiteSpace(expectedPrinterUuid) ||
                 string.Equals(NormalizePrinterUuid(printerUuid), NormalizePrinterUuid(expectedPrinterUuid), StringComparison.OrdinalIgnoreCase));
        }
        catch
        {
            return false;
        }
    }

    internal static string NormalizePrinterUuid(string? value)
    {
        string normalized = (value ?? string.Empty).Trim();
        if (normalized.StartsWith("urn:uuid:", StringComparison.OrdinalIgnoreCase))
            normalized = normalized[9..];
        return normalized.Trim('{', '}');
    }

    public static async Task<(bool Success, string Message)> InstallVirtualPrinterAsync(
        string printerName,
        int port = 9199,
        Func<bool>? onLanBindingReady = null)
    {
        if (!OperatingSystem.IsWindows())
            return (false, "Cài đặt hàng đợi Windows chỉ được hỗ trợ trên Windows.");
        if (string.IsNullOrWhiteSpace(printerName))
            return (false, "Tên hàng đợi máy in không được để trống.");
        if (port is < 1 or > 65535)
            return (false, $"Cổng IPP không hợp lệ: {port}.");

        var ippProbe = await ProbeIppEndpointAsync(port);
        if (!ippProbe.Success && onLanBindingReady is null)
            return ippProbe;

        return await Task.Run(() => RunElevatedInstallation(printerName.Trim(), port, onLanBindingReady));
    }

    internal static async Task<(bool Success, string Message)> ProbeIppEndpointAsync(
        int port,
        CancellationToken cancellationToken = default)
    {
        string ippUrl = GetIppUrl(port);
        string transportUrl = GetLoopbackHttpIppUrl(port);
        int requestId = Random.Shared.Next(1, int.MaxValue);
        byte[] requestBytes = BuildGetPrinterAttributesRequest(ippUrl, requestId);

        try
        {
            using var handler = new HttpClientHandler { UseProxy = false };
            using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(10) };
            using var request = new HttpRequestMessage(HttpMethod.Post, transportUrl)
            {
                Content = new ByteArrayContent(requestBytes)
            };
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/ipp"));
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/ipp");

            using HttpResponseMessage response = await http.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);
            byte[] responseBytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);

            if ((int)response.StatusCode != 200)
                return (false, $"IPP discovery trả HTTP {(int)response.StatusCode} ({response.ReasonPhrase}) cho {transportUrl}.");

            string? contentType = response.Content.Headers.ContentType?.MediaType;
            if (!string.Equals(contentType, "application/ipp", StringComparison.OrdinalIgnoreCase))
                return (false, $"IPP discovery trả Content-Type '{contentType ?? "(trống)"}', cần application/ipp.");

            if (!TryValidatePrinterAttributesResponse(responseBytes, ippUrl, requestId, out string error))
                return (false, $"IPP discovery response không hợp lệ: {error}");

            return (true, "IPP Get-Printer-Attributes probe thành công.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return (false, "IPP discovery bị hủy trước khi hoàn tất.");
        }
        catch (Exception ex)
        {
                return (false, $"Không kết nối được IPP endpoint '{ippUrl}' qua HTTP '{transportUrl}': {ex.Message}");
        }
    }

    private static byte[] BuildGetPrinterAttributesRequest(string ippUrl, int requestId)
    {
        using var ms = new MemoryStream();
        ms.WriteByte(0x01);
        ms.WriteByte(0x01);
        WriteBigEndian(ms, (ushort)0x000B);
        WriteBigEndian(ms, requestId);
        ms.WriteByte(0x01);
        WriteAttribute(ms, 0x47, "attributes-charset", "utf-8");
        WriteAttribute(ms, 0x48, "attributes-natural-language", "en-us");
        WriteAttribute(ms, 0x45, "printer-uri", ippUrl);
        ms.WriteByte(0x03);
        return ms.ToArray();
    }

    private static bool TryValidatePrinterAttributesResponse(
        byte[] data,
        string expectedUri,
        int expectedRequestId,
        out string error)
    {
        error = string.Empty;
        if (data.Length < 9)
            return Fail("response header is truncated", out error);

        if (data[0] != 0x01 || data[1] > 0x01)
            return Fail("unsupported IPP response version", out error);

        ushort statusCode = (ushort)((data[2] << 8) | data[3]);
        int requestId = (data[4] << 24) | (data[5] << 16) | (data[6] << 8) | data[7];
        if (statusCode != 0x0000)
            return Fail($"IPP status=0x{statusCode:X4}", out error);
        if (requestId != expectedRequestId)
            return Fail($"response request-id={requestId}, expected {expectedRequestId}", out error);

        var attributes = new Dictionary<string, List<(byte Tag, byte[] Value)>>(StringComparer.OrdinalIgnoreCase);
        int index = 8;
        string? currentAttributeName = null;
        bool sawEndOfAttributes = false;
        while (index < data.Length)
        {
            byte tag = data[index++];
            if (tag == 0x03)
            {
                sawEndOfAttributes = true;
                break;
            }
            if (tag is >= 0x01 and <= 0x02 or >= 0x04 and <= 0x07) continue;
            if (!TryReadUnsignedShort(data, ref index, out int nameLength) || index + nameLength > data.Length)
                return Fail("attribute name is truncated", out error);
            if (nameLength == 0)
            {
                if (currentAttributeName is null)
                    return Fail("attribute continuation has no preceding name", out error);
            }
            else
            {
                currentAttributeName = Encoding.UTF8.GetString(data, index, nameLength);
            }
            string name = currentAttributeName!;
            index += nameLength;
            if (!TryReadUnsignedShort(data, ref index, out int valueLength) || index + valueLength > data.Length)
                return Fail($"attribute '{name}' value is truncated", out error);
            byte[] value = data[index..(index + valueLength)];
            index += valueLength;
            if (!attributes.TryGetValue(name, out var values))
            {
                values = new List<(byte Tag, byte[] Value)>();
                attributes[name] = values;
            }
            values.Add((tag, value));
        }

        if (!sawEndOfAttributes)
            return Fail("response thiếu end-of-attributes", out error);
        if (index != data.Length)
            return Fail("response chứa dữ liệu sau end-of-attributes", out error);

        if (!attributes.TryGetValue("printer-uri-supported", out var uriValues) ||
            !uriValues.Any(v => v.Tag == 0x45 && string.Equals(Encoding.UTF8.GetString(v.Value), expectedUri, StringComparison.OrdinalIgnoreCase)))
            return Fail("printer-uri-supported không khớp endpoint", out error);
        if (!HasText(attributes, "printer-name", tag: 0x42) || !HasText(attributes, "charset-supported", "utf-8", 0x47))
            return Fail("thiếu printer-name hoặc charset-supported=utf-8", out error);
        if (!HasText(attributes, "ipp-versions-supported", "1.1", 0x44))
            return Fail("thiếu ipp-versions-supported=1.1", out error);
        if (!HasText(attributes, "uri-authentication-supported", "none", 0x44) ||
            !HasText(attributes, "uri-security-supported", "none", 0x44))
            return Fail("thiếu URI security/authentication metadata", out error);
        // Microsoft IPP Class Driver renders the queue output as PDF. The
        // server accepts that payload and forwards it through the existing
        // bounded raw-job pipeline; other PDLs remain intentionally absent.
        if (!HasText(attributes, "document-format-supported", "application/pdf", 0x49) ||
            !HasText(attributes, "document-format-supported", "application/octet-stream", 0x49))
            return Fail("thiếu document-format-supported=application/pdf và application/octet-stream", out error);
        if (!HasInt(attributes, "printer-state", 3, 0x23) || !HasInt(attributes, "operations-supported", 0x000B, 0x23))
            return Fail("thiếu printer-state=3 hoặc Get-Printer-Attributes trong operations-supported", out error);

        return true;
    }

    private static bool HasText(
        IReadOnlyDictionary<string, List<(byte Tag, byte[] Value)>> attributes,
        string name,
        string? expected = null,
        byte? tag = null) =>
        attributes.TryGetValue(name, out var values) &&
        values.Any(v => (tag is null || v.Tag == tag.Value) &&
            (expected is null || string.Equals(Encoding.UTF8.GetString(v.Value), expected, StringComparison.OrdinalIgnoreCase)));

    private static bool HasInt(
        IReadOnlyDictionary<string, List<(byte Tag, byte[] Value)>> attributes,
        string name,
        int expected,
        byte? tag = null) =>
        attributes.TryGetValue(name, out var values) &&
        values.Any(v => (tag is null || v.Tag == tag.Value) && v.Value.Length == 4 &&
            ((v.Value[0] << 24) | (v.Value[1] << 16) | (v.Value[2] << 8) | v.Value[3]) == expected);

    private static bool TryReadUnsignedShort(byte[] data, ref int index, out int value)
    {
        if (index + 2 > data.Length)
        {
            value = 0;
            return false;
        }
        value = (data[index] << 8) | data[index + 1];
        index += 2;
        return true;
    }

    private static bool Fail(string message, out string error)
    {
        error = message;
        return false;
    }

    private static void WriteBigEndian(Stream stream, int value)
    {
        stream.WriteByte((byte)(value >> 24));
        stream.WriteByte((byte)(value >> 16));
        stream.WriteByte((byte)(value >> 8));
        stream.WriteByte((byte)value);
    }

    private static void WriteBigEndian(Stream stream, ushort value)
    {
        stream.WriteByte((byte)(value >> 8));
        stream.WriteByte((byte)value);
    }

    private static void WriteAttribute(Stream stream, byte tag, string name, string value)
    {
        byte[] nameBytes = Encoding.ASCII.GetBytes(name);
        byte[] valueBytes = Encoding.UTF8.GetBytes(value);
        stream.WriteByte(tag);
        stream.WriteByte((byte)(nameBytes.Length >> 8));
        stream.WriteByte((byte)nameBytes.Length);
        stream.Write(nameBytes, 0, nameBytes.Length);
        stream.WriteByte((byte)(valueBytes.Length >> 8));
        stream.WriteByte((byte)valueBytes.Length);
        stream.Write(valueBytes, 0, valueBytes.Length);
    }

    internal static string BuildNativeIppInstallScript(string printerName, int port)
    {
        string safePrinterName = EscapePowerShellLiteral(printerName);
        string safeIppUrl = EscapePowerShellLiteral(GetIppUrl(port));

        return $@"
param(
    [Parameter(Mandatory = $true)][string]$ResultPath,
    [Parameter(Mandatory = $true)][string]$ReadyPath
)

$ErrorActionPreference = 'Stop'
$printerName = '{safePrinterName}'
$ippUrl = '{safeIppUrl}'
$transportUrl = '{EscapePowerShellLiteral(GetQueueTransportUrl(port))}'
$urlAcl = '{EscapePowerShellLiteral(GetQueueUrlAcl(port))}'
$urlAcls = @($urlAcl, 'http://127.0.0.1:{port}/', 'http://localhost:{port}/')

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

function Get-IppTextAttribute([byte[]]$bytes, [string]$attributeName) {{
    $index = 8
    $currentName = $null
    while ($index -lt $bytes.Length) {{
        $tag = $bytes[$index]
        $index++
        if ($tag -eq 3) {{ break }}
        if (($tag -ge 1 -and $tag -le 2) -or ($tag -ge 4 -and $tag -le 8)) {{
            $currentName = $null
            continue
        }}
        if ($index + 2 -gt $bytes.Length) {{ break }}
        $nameLength = ($bytes[$index] -shl 8) -bor $bytes[$index + 1]
        $index += 2
        if ($index + $nameLength + 2 -gt $bytes.Length) {{ break }}
        if ($nameLength -gt 0) {{ $currentName = [Text.Encoding]::UTF8.GetString($bytes, $index, $nameLength) }}
        if ([string]::IsNullOrWhiteSpace($currentName)) {{ break }}
        $index += $nameLength
        $valueLength = ($bytes[$index] -shl 8) -bor $bytes[$index + 1]
        $index += 2
        if ($index + $valueLength -gt $bytes.Length) {{ break }}
        if ($currentName -ieq $attributeName) {{ return [Text.Encoding]::UTF8.GetString($bytes, $index, $valueLength) }}
        $index += $valueLength
    }}
    return $null
}}

function Test-IppWsdPort([string]$portName, [string]$expectedPrinterUuid) {{
    if ([string]::IsNullOrWhiteSpace($portName) -or $portName -notmatch '^WSD-') {{ return $false }}
    $port = Get-PrinterPort -Name $portName -ErrorAction Stop
    $keyPath = ""HKLM:\SYSTEM\CurrentControlSet\Control\Print\Monitors\WSD Port\Ports\$portName""
    $metadata = Get-ItemProperty -LiteralPath $keyPath -ErrorAction Stop
    $registryUuid = [string]$metadata.'Printer UUID'
    if ($registryUuid -match '^urn:uuid:') {{ $registryUuid = $registryUuid.Substring(9) }}
    if ($expectedPrinterUuid -match '^urn:uuid:') {{ $expectedPrinterUuid = $expectedPrinterUuid.Substring(9) }}
    $registryUuid = $registryUuid.Trim('{{}}')
    $expectedPrinterUuid = $expectedPrinterUuid.Trim('{{}}')
    return $port.PortMonitor -ieq 'WSD Port Monitor' -and
        $port.Description -ieq 'IPP Port' -and
        $registryUuid -ieq $expectedPrinterUuid -and
        -not [string]::IsNullOrWhiteSpace([string]$metadata.'IPP PortId')
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

    $urlAclUser = ""$env:USERDOMAIN\$env:USERNAME""
    foreach ($reservation in $urlAcls) {{
        $urlAclListing = (& netsh http show urlacl url=$reservation 2>&1 | Out-String)
        $hasReservation = $LASTEXITCODE -eq 0 -and $urlAclListing -match [regex]::Escape($reservation)
        $hasCurrentUser = $hasReservation -and $urlAclListing -match [regex]::Escape($urlAclUser)
        if (-not $hasCurrentUser) {{
            $urlAclOutput = (& netsh http add urlacl url=$reservation user=$urlAclUser 2>&1 | Out-String)
            if ($LASTEXITCODE -ne 0) {{
                $verification = (& netsh http show urlacl url=$reservation 2>&1 | Out-String)
                $verifiedAfterAdd = $verification -match [regex]::Escape($reservation) -and
                    $verification -match [regex]::Escape($urlAclUser)
                if ($verifiedAfterAdd) {{ continue }}
                Write-InstallResult $false 'url-acl' (""Không thể cấp URL ACL cho IPP endpoint '{{0}}'."" -f $reservation) @{{
                    UrlAcl = $reservation
                    User = $urlAclUser
                    NetshOutput = (""{{0}} {{1}}"" -f $urlAclOutput.Trim(), $verification.Trim()).Trim()
                    ExitCode = $LASTEXITCODE
                }}
            }}
        }}
    }}

    # LocalIppServer is restarted by the parent after URL ACL creation.
    $ready = [ordered]@{{
        Success = $true
        Stage = 'binding-ready'
        Message = 'URL ACL IPP đã sẵn sàng; đang chờ LocalIppServer bind lại địa chỉ LAN.'
        Details = @{{ UrlAcls = $urlAcls }}
    }}
    $ready | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $ResultPath -Encoding UTF8
    $readyDeadline = [DateTime]::UtcNow.AddSeconds(30)
    while (-not (Test-Path -LiteralPath $ReadyPath) -and [DateTime]::UtcNow -lt $readyDeadline) {{
        Start-Sleep -Milliseconds 100
    }}
    if (-not (Test-Path -LiteralPath $ReadyPath)) {{
        Write-InstallResult $false 'lan-binding' 'LocalIppServer không xác nhận bind lại địa chỉ LAN trong thời gian cho phép.' @{{ UrlAcls = $urlAcls }}
    }}

    $stage = 'ipp-probe'
    $probeRequestId = 16909060
    $ippRequest = [IO.MemoryStream]::new()
    $header = [byte[]](1, 1, 0, 11, 1, 2, 3, 4)
    $ippRequest.Write($header, 0, $header.Length)
    function Write-IppText([IO.MemoryStream]$stream, [byte]$tag, [string]$name, [string]$value) {{
        $nameBytes = [Text.Encoding]::ASCII.GetBytes($name)
        $valueBytes = [Text.Encoding]::UTF8.GetBytes($value)
        $stream.WriteByte($tag)
        $stream.WriteByte([byte]($nameBytes.Length -shr 8))
        $stream.WriteByte([byte]$nameBytes.Length)
        $stream.Write($nameBytes, 0, $nameBytes.Length)
        $stream.WriteByte([byte]($valueBytes.Length -shr 8))
        $stream.WriteByte([byte]$valueBytes.Length)
        $stream.Write($valueBytes, 0, $valueBytes.Length)
    }}
    $ippRequest.WriteByte(1)
    Write-IppText $ippRequest 0x47 'attributes-charset' 'utf-8'
    Write-IppText $ippRequest 0x48 'attributes-natural-language' 'en-us'
    Write-IppText $ippRequest 0x45 'printer-uri' $ippUrl
    $ippRequest.WriteByte(3)
    $probe = Invoke-WebRequest -Uri $transportUrl -Method Post -ContentType 'application/ipp' -Body $ippRequest.ToArray() -UseBasicParsing -TimeoutSec 10 -ErrorAction Stop
    $probeBytes = $probe.RawContentStream.ToArray()
    $probeContentType = [string]$probe.Headers['Content-Type']
    if ($probe.StatusCode -ne 200 -or $probeContentType.Split(';')[0].Trim() -ine 'application/ipp' -or $probeBytes.Length -lt 9) {{
        Write-InstallResult $false $stage 'IPP endpoint không trả về response IPP hợp lệ.' @{{
            StatusCode = $probe.StatusCode
            ContentType = $probeContentType
            ResponseLength = $probeBytes.Length
            Url = $ippUrl
            TransportUrl = $transportUrl
        }}
    }}
    $probeIppStatus = ([int]$probeBytes[2] -shl 8) -bor [int]$probeBytes[3]
    if ($probeIppStatus -ne 0) {{
        Write-InstallResult $false $stage (""IPP discovery trả status 0x{{0:X4}} thay vì successful-ok."" -f $probeIppStatus) @{{
            Url = $ippUrl
            IppStatus = ('0x{{0:X4}}' -f $probeIppStatus)
        }}
    }}
    $responseRequestId = ([int]$probeBytes[4] -shl 24) -bor ([int]$probeBytes[5] -shl 16) -bor ([int]$probeBytes[6] -shl 8) -bor [int]$probeBytes[7]
    if ($responseRequestId -ne $probeRequestId) {{
        Write-InstallResult $false $stage (""IPP response request-id {{0}} không khớp {{1}}."" -f $responseRequestId, $probeRequestId) @{{
            Url = $ippUrl
            ResponseRequestId = $responseRequestId
            ExpectedRequestId = $probeRequestId
        }}
    }}
    $probePrinterUuid = Get-IppTextAttribute $probeBytes 'printer-uuid'
    if ([string]::IsNullOrWhiteSpace($probePrinterUuid)) {{
        Write-InstallResult $false $stage 'IPP discovery không trả printer-uuid hợp lệ.' @{{ Url = $ippUrl }}
    }}

    $existing = Get-Printer -Name $printerName -ErrorAction SilentlyContinue
    if ($null -ne $existing) {{
        $existingPort = [string]$existing.PortName
        $existingDriver = [string]$existing.DriverName
        $existingMatches = $existingPort.TrimEnd('/') -ieq $ippUrl.TrimEnd('/')
        if (-not $existingMatches -and $existingPort -match '^WSD-') {{
            try {{ $existingMatches = Test-IppWsdPort $existingPort $probePrinterUuid }} catch {{ $existingMatches = $false }}
        }}
        if ($existingMatches) {{
            Write-InstallResult $true 'existing' (""Hàng đợi '{{0}}' đã tồn tại và được Windows liên kết với IPP endpoint '{{1}}'."" -f $printerName, $ippUrl) @{{
                PrinterName = $existing.Name
                PortName = $existingPort
                DriverName = $existingDriver
                ExpectedIppUrl = $ippUrl
                PrinterUuid = $probePrinterUuid
            }}
        }}

        Write-InstallResult $false 'existing-queue' (""Hàng đợi '{{0}}' đã tồn tại nhưng đang trỏ tới cổng '{{1}}'. Không tự ý thay đổi hoặc xóa hàng đợi hiện có."" -f $printerName, $existingPort) @{{
            PrinterName = $existing.Name
            PortName = $existingPort
            DriverName = $existingDriver
            ExpectedIppUrl = $ippUrl
        }}
    }}

    $stage = 'add-printer'
    Add-Printer -Name $printerName -IppURL $ippUrl -ErrorAction Stop
    Start-Sleep -Milliseconds 500

    $created = Get-Printer -Name $printerName -ErrorAction Stop
    $createdPort = [string]$created.PortName
    $createdUri = [Uri]$createdPort
    $expectedUri = [Uri]$ippUrl
    $sameEndpoint = $false
    if ($createdPort -match '^(http|ipp)://') {{
        $sameEndpoint = $createdUri.Host -ieq $expectedUri.Host -and
            $createdUri.Port -eq $expectedUri.Port -and
            $createdUri.AbsolutePath.TrimEnd('/') -ieq $expectedUri.AbsolutePath.TrimEnd('/')
    }} else {{
        try {{ $sameEndpoint = Test-IppWsdPort $createdPort $probePrinterUuid }} catch {{ $sameEndpoint = $false }}
    }}
    if (-not $sameEndpoint) {{
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

    private static (bool Success, string Message) RunElevatedInstallation(
        string printerName,
        int port,
        Func<bool>? onLanBindingReady)
    {
        string operationId = Guid.NewGuid().ToString("N");
        string tempFolder = Path.Combine(Path.GetTempPath(), "RemotePrintManager", "printer-install");
        string resultPath = Path.Combine(tempFolder, $"result-{operationId}.json");
        string scriptPath = Path.Combine(tempFolder, $"install-{operationId}.ps1");
        string readyPath = Path.Combine(tempFolder, $"ready-{operationId}.signal");

        try
        {
            Directory.CreateDirectory(tempFolder);
            string script = BuildNativeIppInstallScript(printerName, port);
            // Windows PowerShell 5.1 needs a UTF-8 BOM to parse Vietnamese literals reliably.
            File.WriteAllText(scriptPath, script, new UTF8Encoding(true));
            var psi = BuildElevatedInstallationStartInfo(scriptPath, resultPath, readyPath);

            using var process = Process.Start(psi);
            if (process is null)
                return (false, "Không thể khởi động tiến trình cài đặt PowerShell nâng quyền.");

            bool bindingReadyHandled = false;
            var deadline = DateTime.UtcNow.AddMilliseconds(InstallTimeoutMilliseconds);
            while (!process.HasExited && DateTime.UtcNow < deadline)
            {
                if (!bindingReadyHandled && TryReadInstallStage(resultPath, "binding-ready"))
                {
                    bindingReadyHandled = true;
                    bool bindingReady = onLanBindingReady?.Invoke() ?? true;
                    if (!bindingReady)
                        return (false, "LocalIppServer không thể bind địa chỉ LAN sau khi URL ACL đã được cấp.");
                    File.WriteAllText(readyPath, "ready", Encoding.ASCII);
                }

                Thread.Sleep(100);
            }

            if (!process.HasExited)
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
            TryDelete(scriptPath);
            TryDelete(readyPath);
        }
    }

    internal static ProcessStartInfo BuildElevatedInstallationStartInfo(
        string scriptPath,
        string resultPath,
        string? readyPath = null)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = $"-NoProfile -ExecutionPolicy Bypass -File {QuoteProcessArgument(scriptPath)} -ResultPath {QuoteProcessArgument(resultPath)}" +
                (readyPath is null ? string.Empty : $" -ReadyPath {QuoteProcessArgument(readyPath)}"),
            UseShellExecute = true,
            Verb = "runas",
            WindowStyle = ProcessWindowStyle.Hidden
        };
        return psi;
    }

    private static bool TryReadInstallStage(string path, string expectedStage)
    {
        try
        {
            if (!File.Exists(path)) return false;
            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path));
            return document.RootElement.TryGetProperty("Stage", out var stage) &&
                string.Equals(stage.GetString(), expectedStage, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
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

    private static string GetIppUrl(int port) => $"ipp://{GetPreferredLocalHost()}:{port}{IppPath}";

    private static string GetLoopbackHttpIppUrl(int port) => $"http://127.0.0.1:{port}{IppPath}";

    private static string GetQueueTransportUrl(int port) => $"http://{GetPreferredLocalHost()}:{port}{IppPath}";

    private static string GetQueueUrlAcl(int port) => $"http://{GetPreferredLocalHost()}:{port}/";

    private static bool IsLocalHost(string host) =>
        string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(host, "127.0.0.1", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(host, Environment.MachineName, StringComparison.OrdinalIgnoreCase) ||
        IsLocalIpv4(host);

    internal static string GetPreferredLocalHost()
    {
        try
        {
            string? gatewayAddress = NetworkInterface.GetAllNetworkInterfaces()
                .Where(adapter => adapter.OperationalStatus == OperationalStatus.Up &&
                                  adapter.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                .SelectMany(adapter =>
                {
                    var properties = adapter.GetIPProperties();
                    bool hasIpv4Gateway = properties.GatewayAddresses.Any(gateway =>
                        gateway.Address.AddressFamily == AddressFamily.InterNetwork &&
                        !IPAddress.IsLoopback(gateway.Address));
                    return hasIpv4Gateway
                        ? properties.UnicastAddresses
                            .Where(item => item.Address.AddressFamily == AddressFamily.InterNetwork &&
                                           !IPAddress.IsLoopback(item.Address) &&
                                           !item.Address.ToString().StartsWith("169.254.", StringComparison.Ordinal))
                            .Select(item => item.Address.ToString())
                        : Enumerable.Empty<string>();
                })
                .FirstOrDefault();

            if (!string.IsNullOrWhiteSpace(gatewayAddress))
                return gatewayAddress;

            string? address = Dns.GetHostEntry(Dns.GetHostName()).AddressList
                .Where(item => item.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(item))
                .Where(item => !item.ToString().StartsWith("169.254.", StringComparison.Ordinal))
                .Select(item => item.ToString())
                .FirstOrDefault();

            if (!string.IsNullOrWhiteSpace(address))
                return address;
        }
        catch
        {
            // Fall back to the machine name when local address discovery is unavailable.
        }

        return Environment.MachineName;
    }

    private static bool IsLocalIpv4(string host)
    {
        try
        {
            return Dns.GetHostEntry(Dns.GetHostName()).AddressList.Any(address =>
                address.AddressFamily == AddressFamily.InterNetwork &&
                string.Equals(address.ToString(), host, StringComparison.OrdinalIgnoreCase));
        }
        catch
        {
            return false;
        }
    }

    private static void TryDelete(string path)
    {
        try { if (!string.IsNullOrWhiteSpace(path) && File.Exists(path)) File.Delete(path); } catch { }
    }

    private static string EscapePowerShellLiteral(string value) => value.Replace("'", "''", StringComparison.Ordinal);

    private static string EscapePowerShellArgument(string value) => value.Replace("\"", "\\\"", StringComparison.Ordinal);

    private static string QuoteProcessArgument(string value) =>
        $"\"{value.Replace("\"", "\\\"", StringComparison.Ordinal)}\"";
}
