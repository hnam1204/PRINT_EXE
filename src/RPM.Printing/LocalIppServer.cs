using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Diagnostics;
using System.Security.Cryptography;
using RPM.Core.Security;
using RPM.Core.Config;
using System.IO;
using RPM.Core.Models;

namespace RPM.Printing;

public class PrintJobReceivedEventArgs : EventArgs
{
    public PrintJob Job { get; set; } = new();
    public byte[] Data { get; set; } = Array.Empty<byte>();
    public string? TempFilePath { get; set; }
    public long DataOffset { get; set; }
    public long DataLength { get; set; }
}

public class LocalIppServer : IDisposable
{
    private const ushort PrintJobOperation = 0x0002;
    private const ushort ValidateJobOperation = 0x0004;
    private const ushort CreateJobOperation = 0x0005;
    private const ushort SendDocumentOperation = 0x0006;
    private const ushort CancelJobOperation = 0x0008;
    private const ushort GetJobAttributesOperation = 0x0009;
    private const ushort GetJobsOperation = 0x000A;
    private const ushort GetPrinterAttributesOperation = 0x000B;
    private const ushort SuccessfulOk = 0x0000;
    private const ushort ClientErrorBadRequest = 0x0400;
    private const ushort ClientErrorDocumentFormatNotSupported = 0x040B;
    private const ushort ClientErrorNotFound = 0x0406;
    private const ushort ServerErrorOperationNotSupported = 0x0501;
    private const int MaxIppHeaderBytes = 1024 * 1024;
    private static readonly Encoding StrictUtf8 = new UTF8Encoding(false, true);

    private HttpListener? _listener;
    private CancellationTokenSource? _cts;
    private bool _isRunning;
    private string _printerUuid = string.Empty;
    public int Port { get; private set; }
    public string PrinterName { get; set; } = "Remote Print Manager";
    public bool IsLanBindingActive { get; private set; }

    public event EventHandler<PrintJobReceivedEventArgs>? OnJobReceived;
    public event EventHandler<string>? OnLog;

    public bool IsRunning => _isRunning;

    public void Restart(int port = 0)
    {
        int restartPort = port is > 0 and <= 65535 ? port : Port;
        Stop();
        Start(restartPort);
    }

    public void Start(int port = 9199)
    {
        if (_isRunning) return;
        Port = port;
        _printerUuid = GetStablePrinterUuid(port);
        IsLanBindingActive = false;

        try
        {
            _listener = new HttpListener();
            IReadOnlyList<string> bindingHosts = GetLocalBindingHosts();
            foreach (string host in bindingHosts)
                _listener.Prefixes.Add($"http://{host}:{Port}/");
            try
            {
                _listener.Start();
                IsLanBindingActive = bindingHosts.Any(host => !IsLoopbackHost(host));
            }
            catch (HttpListenerException ex) when (ex.ErrorCode == 5 && bindingHosts.Count > 1)
            {
                // A normal desktop process may not own the LAN URL until the
                // elevated queue installer creates its URL ACL. Keep local
                // printing available and let the installer request a restart.
                _listener.Close();
                _listener = new HttpListener();
                _listener.Prefixes.Add($"http://127.0.0.1:{Port}/");
                _listener.Start();
                IsLanBindingActive = false;
                OnLog?.Invoke(this,
                    $"[IPP] LAN binding chưa được cấp quyền trên cổng {Port}; đang chạy loopback cho đến khi cài hàng đợi " +
                    $"(ErrorCode={ex.ErrorCode}, Prefixes={string.Join(",", bindingHosts)}): {ex.Message}");
            }

            _cts = new CancellationTokenSource();
            _isRunning = true;
            Task.Run(() => ListenLoopAsync(_cts.Token));
            OnLog?.Invoke(this, $"[IPP] Dịch vụ máy in ảo đã khởi động trên cổng {Port}.");
        }
        catch (Exception ex)
        {
            string detail = ex is HttpListenerException listenerError
                ? $"ErrorCode={listenerError.ErrorCode}, {listenerError.Message}"
                : ex.Message;
            OnLog?.Invoke(this, $"[IPP Error] Không thể khởi động cổng {Port}: {detail}");
            _isRunning = false;
        }
    }

    public void Stop()
    {
        _isRunning = false;
        IsLanBindingActive = false;
        _cts?.Cancel();
        try
        {
            _listener?.Stop();
            _listener?.Close();
        }
        catch { }
    }

    private async Task ListenLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && _listener != null && _listener.IsListening)
        {
            try
            {
                var context = await _listener.GetContextAsync();
                _ = Task.Run(() => HandleRequest(context));
            }
            catch (HttpListenerException)
            {
                break;
            }
            catch (Exception ex)
            {
                OnLog?.Invoke(this, $"[IPP] Lỗi tiếp nhận request: {ex.Message}");
            }
        }
    }

    private void HandleRequest(HttpListenerContext ctx)
    {
        var startedAt = Stopwatch.GetTimestamp();
        string method = string.Empty;
        string endpoint = string.Empty;
        ushort operationId = 0;
        int requestId = 0;
        byte requestMajor = 0;
        byte requestMinor = 0;
        ushort ippStatus = ClientErrorBadRequest;
        string? parseError = null;
        int httpStatus = 500;

        try
        {
            var req = ctx.Request;
            var res = ctx.Response;
            method = req.HttpMethod;
            endpoint = GetSafeEndpoint(req);

            res.Headers.Add("Server", "RPM-IPP/1.0");

            if (req.HttpMethod.Equals("POST", StringComparison.OrdinalIgnoreCase))
            {
                if (!IsPrinterResource(req.Url))
                {
                    httpStatus = 404;
                    res.StatusCode = httpStatus;
                    res.Close();
                    parseError = "resource-path-not-found";
                    return;
                }

                if (!IsIppContentType(req.ContentType))
                {
                    httpStatus = 415;
                    res.StatusCode = httpStatus;
                    res.Close();
                    parseError = "content-type-must-be-application-ipp";
                    return;
                }

                // Windows IPP clients may use chunked transfer, where ContentLength64
                // is -1. The streaming guard below still enforces the hard limit.
                if (req.ContentLength64 > PacketSecurity.MaxDirectPrintJobBytes + 256 * 1024)
                {
                    httpStatus = 413;
                    res.StatusCode = 413;
                    res.Close();
                    parseError = "request-too-large";
                    return;
                }

                string tempFilePath = Path.Combine(ConfigManager.GetTempJobsFolder(), $"ipp-{Guid.NewGuid():N}.bin");
                var buffer = new byte[81920];
                long requestLength = 0;
                using (var output = new FileStream(tempFilePath, FileMode.CreateNew, FileAccess.Write, FileShare.Read, 128 * 1024))
                {
                    int read;
                    while ((read = req.InputStream.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        requestLength += read;
                        if (requestLength > PacketSecurity.MaxDirectPrintJobBytes + 256 * 1024)
                        {
                            httpStatus = 413;
                            res.StatusCode = 413;
                            res.Close();
                            TryDelete(tempFilePath);
                            parseError = "request-too-large";
                            return;
                        }
                        output.Write(buffer, 0, read);
                    }
                }

                int prefixLength = (int)Math.Min(requestLength, 1024 * 1024);
                byte[] requestBytes = ReadFileSegment(tempFilePath, 0, prefixLength);

                byte[] responseBytes = ProcessIppPacket(
                    requestBytes,
                    out var capturedJob,
                    out var documentData,
                    out operationId,
                    out requestId,
                    out requestMajor,
                    out requestMinor,
                    out ippStatus,
                    out parseError,
                    req.Url);
                httpStatus = 200;

                int documentOffset = FindDocumentOffset(requestBytes);
                long documentLength = documentOffset >= 0 ? requestLength - documentOffset : 0;
                if (capturedJob != null && documentOffset >= 0 && documentLength > 0)
                {
                    capturedJob.FileSizeBytes = documentLength;
                    var args = new PrintJobReceivedEventArgs
                    {
                        Job = capturedJob,
                        DataLength = documentLength,
                        DataOffset = documentOffset
                    };

                    if (documentLength <= 4L * 1024 * 1024)
                    {
                        args.Data = ReadFileSegment(tempFilePath, documentOffset, documentLength);
                        TryDelete(tempFilePath);
                    }
                    else
                    {
                        args.TempFilePath = tempFilePath;
                    }

                    OnLog?.Invoke(this, $"[IPP] Nhận lệnh in: operation=0x{operationId:X4}, requestId={requestId}, bytes={documentLength}");
                    OnJobReceived?.Invoke(this, args);
                }
                else
                {
                    TryDelete(tempFilePath);
                }

                res.ContentType = "application/ipp";
                res.StatusCode = 200;
                res.ContentLength64 = responseBytes.Length;
                res.OutputStream.Write(responseBytes, 0, responseBytes.Length);
                res.OutputStream.Flush();
                res.Close();
                return;
            }

            if (req.HttpMethod.Equals("OPTIONS", StringComparison.OrdinalIgnoreCase))
            {
                httpStatus = 200;
                res.StatusCode = httpStatus;
                res.Headers["Allow"] = "GET, POST, OPTIONS";
                res.Headers["Accept-Post"] = "application/ipp";
                res.Close();
                return;
            }

            if (!req.HttpMethod.Equals("GET", StringComparison.OrdinalIgnoreCase) || !IsStatusProbeResource(req.Url))
            {
                httpStatus = 404;
                res.StatusCode = httpStatus;
                res.Close();
                parseError = "resource-path-not-found";
                return;
            }

            // Keep a human-readable status probe, while IPP clients use POST with
            // application/ipp for the actual directed discovery exchange.
            httpStatus = 200;
            res.StatusCode = httpStatus;
            res.Headers["Allow"] = "GET, POST, OPTIONS";
            res.Headers["Accept-Post"] = "application/ipp";
            res.ContentType = "text/html; charset=utf-8";
            byte[] statusHtml = Encoding.UTF8.GetBytes($"<html><body><h2>Remote Print Manager (RPM) IPP Service</h2><p>Printer: {PrinterName} is Online</p></body></html>");
            res.ContentLength64 = statusHtml.Length;
            res.OutputStream.Write(statusHtml, 0, statusHtml.Length);
            res.Close();
        }
        catch (Exception ex)
        {
            parseError = "exception";
            try
            {
                ctx.Response.StatusCode = 500;
                ctx.Response.Close();
            }
            catch { }
            OnLog?.Invoke(this, $"[IPP] Xử lý lỗi: {ex.Message}");
        }
        finally
        {
            long elapsedMs = (long)(Stopwatch.GetTimestamp() - startedAt) * 1000 / Stopwatch.Frequency;
            OnLog?.Invoke(this,
                $"[IPP Discovery] method={method}, endpoint={endpoint}, operation=0x{operationId:X4}, " +
                $"version={requestMajor}.{requestMinor}, requestId={requestId}, httpStatus={httpStatus}, ippStatus=0x{ippStatus:X4}, " +
                $"parseError={parseError ?? "none"}, elapsedMs={elapsedMs}");
        }
    }

    private static bool IsIppContentType(string? contentType) =>
        !string.IsNullOrWhiteSpace(contentType) &&
        contentType.Split(';', 2)[0].Trim().Equals("application/ipp", StringComparison.OrdinalIgnoreCase);

    private static bool IsPrinterResource(Uri? uri) =>
        uri is not null &&
        string.Equals(uri.AbsolutePath.TrimEnd('/'), "/printers/rpm", StringComparison.OrdinalIgnoreCase);

    private static bool IsStatusProbeResource(Uri? uri) =>
        uri is not null &&
        (string.IsNullOrEmpty(uri.AbsolutePath.Trim('/')) || IsPrinterResource(uri));

    private static string GetSafeEndpoint(HttpListenerRequest request)
    {
        if (request.Url is null) return "unknown";
        return $"{request.Url.Scheme}://{request.Url.Host}:{request.Url.Port}{request.Url.AbsolutePath}";
    }

    private static IReadOnlyList<string> GetLocalBindingHosts()
    {
        var hosts = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "127.0.0.1",
            "localhost"
        };

        // The queue uses the same preferred local address. URL ACL creation is
        // handled only during printer installation; the app does not run as admin.
        string preferredHost = VirtualPrinterManager.GetPreferredLocalHost();
        if (!string.IsNullOrWhiteSpace(preferredHost) && !IsLoopbackHost(preferredHost))
            hosts.Add(preferredHost);

        return hosts.Where(host => !string.IsNullOrWhiteSpace(host)).ToArray();
    }

    private static bool IsLoopbackHost(string host) =>
        string.Equals(host, "127.0.0.1", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase) ||
        IPAddress.TryParse(host, out var address) && IPAddress.IsLoopback(address);

    internal static string GetStablePrinterUuid(int port)
    {
        byte[] hash = SHA1.HashData(Encoding.UTF8.GetBytes(
            $"RemotePrintManager:IPP:{Environment.MachineName}:{port}"));
        byte[] uuidBytes = hash[..16];
        uuidBytes[6] = (byte)((uuidBytes[6] & 0x0F) | 0x50); // UUID version 5 shape.
        uuidBytes[8] = (byte)((uuidBytes[8] & 0x3F) | 0x80); // RFC 4122 variant.
        return $"urn:uuid:{new Guid(uuidBytes):D}";
    }

    private static int FindDocumentOffset(byte[] data) =>
        TryFindAttributesEnd(data, out int endTagIndex) ? endTagIndex + 1 : -1;

    private static byte[] ReadFileSegment(string path, long offset, long length)
    {
        if (length <= 0 || length > int.MaxValue) return Array.Empty<byte>();
        byte[] result = new byte[(int)length];
        using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024);
        input.Position = offset;
        int total = 0;
        while (total < result.Length)
        {
            int read = input.Read(result, total, result.Length - total);
            if (read == 0) break;
            total += read;
        }
        return total == result.Length ? result : result[..total];
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch { }
    }

    private byte[] ProcessIppPacket(
        byte[] data,
        out PrintJob? capturedJob,
        out byte[]? documentData,
        out ushort operationId,
        out int requestId,
        out byte requestMajor,
        out byte requestMinor,
        out ushort ippStatus,
        out string? parseError,
        Uri? requestUri)
    {
        capturedJob = null;
        documentData = null;
        operationId = 0;
        requestId = 0;
        requestMajor = 0;
        requestMinor = 0;
        ippStatus = ClientErrorBadRequest;
        parseError = null;

        if (data.Length < 8)
        {
            parseError = "header-too-short";
            return CreateSimpleIppResponse(0x01, 0x01, ClientErrorBadRequest, 1);
        }

        byte major = data[0];
        byte minor = data[1];
        requestMajor = major;
        requestMinor = minor;
        operationId = (ushort)((data[2] << 8) | data[3]);
        requestId = (data[4] << 24) | (data[5] << 16) | (data[6] << 8) | data[7];

        if (!IsSupportedIppVersion(major, minor))
        {
            parseError = $"unsupported-ipp-version-{major}.{minor}";
            ippStatus = ClientErrorBadRequest;
            return CreateSimpleIppResponse(major, minor, ippStatus, requestId);
        }

        bool hasValidAttributes = TryFindAttributesEnd(data, out int endTagIndex);
        if (!hasValidAttributes)
        {
            parseError = "malformed-attributes";
            ippStatus = ClientErrorBadRequest;
            return CreateSimpleIppResponse(major, minor, ippStatus, requestId);
        }

        // Operation 0x0002 = Print-Job, 0x0006 = Send-Document.
        // Some Windows IPP clients split Create-Job and Send-Document instead
        // of using one Print-Job request, so both paths must capture the payload.
        if (operationId == PrintJobOperation || operationId == SendDocumentOperation)
        {
            string docName = "RemoteDocument_" + DateTime.Now.ToString("HHmmss");
            string documentFormat = GetAttributeValue(data, endTagIndex, "document-format") ?? "application/octet-stream";
            if (!IsSupportedDocumentFormat(documentFormat))
            {
                parseError = $"unsupported-document-format-{documentFormat}";
                ippStatus = ClientErrorDocumentFormatNotSupported;
                return CreateSimpleIppResponse(major, minor, ippStatus, requestId);
            }

            string? requestedJobName = GetAttributeValue(data, endTagIndex, "job-name");
            if (!string.IsNullOrWhiteSpace(requestedJobName))
                docName = SanitizeDocumentName(requestedJobName);

            if (endTagIndex != -1 && endTagIndex + 1 < data.Length)
            {
                int payloadLength = data.Length - (endTagIndex + 1);
                documentData = new byte[payloadLength];
                Buffer.BlockCopy(data, endTagIndex + 1, documentData, 0, payloadLength);

                capturedJob = new PrintJob
                {
                    JobId = Guid.NewGuid().ToString("N"),
                    DocumentName = docName,
                    FileSizeBytes = payloadLength,
                    DocumentFormat = documentFormat,
                    SubmittedAt = DateTime.UtcNow,
                    Status = PrintJobStatus.Pending,
                    StatusMessage = "Captured via Ctrl+P"
                };
            }

            ippStatus = SuccessfulOk;
            return CreatePrintJobResponse(major, minor, requestId, 1);
        }

        // Operation 0x0005 = Create-Job. The following Send-Document carries
        // the actual bytes and is handled above.
        if (operationId == CreateJobOperation)
        {
            ippStatus = SuccessfulOk;
            return CreatePrintJobResponse(major, minor, requestId, 1);
        }

        if (operationId == GetPrinterAttributesOperation)
        {
            ippStatus = SuccessfulOk;
            string? requestedPrinterUri = GetAttributeValue(data, endTagIndex, "printer-uri");
            var requestedAttributeValues = GetAttributeValues(data, endTagIndex, "requested-attributes");
            string requestedAttributes = string.Join(",", requestedAttributeValues);
            string requestedSummary = requestedAttributes.Length > 0 ? requestedAttributes : "(all/default)";
            OnLog?.Invoke(this,
                $"[IPP Discovery Request] version={major}.{minor}, requestId={requestId}, " +
                $"printerUri={requestedPrinterUri ?? "(missing)"}, requested={requestedSummary}");
            var requestedSet = requestedAttributeValues.Count == 0
                ? null
                : new HashSet<string>(requestedAttributeValues, StringComparer.OrdinalIgnoreCase);
            byte[] response = CreatePrinterAttributesResponse(major, minor, requestId, requestUri, requestedPrinterUri, requestedSet);
            LogResponseAttributeMetadata(response, requestId);
            return response;
        }

        if (operationId == ValidateJobOperation)
        {
            ippStatus = SuccessfulOk;
            return CreateOperationSuccessResponse(major, minor, requestId);
        }

        if (operationId == CancelJobOperation)
        {
            ippStatus = SuccessfulOk;
            return CreateOperationSuccessResponse(major, minor, requestId);
        }

        if (operationId == GetJobAttributesOperation)
        {
            ippStatus = SuccessfulOk;
            return CreateJobAttributesResponse(major, minor, requestId, requestUri);
        }

        if (operationId == GetJobsOperation)
        {
            ippStatus = SuccessfulOk;
            return CreateGetJobsResponse(major, minor, requestId);
        }

        parseError = "operation-not-supported";
        ippStatus = ServerErrorOperationNotSupported;
        return CreateSimpleIppResponse(major, minor, ippStatus, requestId);
    }

    private static string? GetAttributeValue(byte[] data, int endTagIndex, string attributeName)
    {
        int limit = endTagIndex > 8 ? endTagIndex : data.Length;
        int index = 8;
        string? currentName = null;
        while (index < limit)
        {
            byte valueTag = data[index++];
            if (valueTag == 0x03) break;
            if (IsGroupDelimiter(valueTag)) continue;
            if (index + 2 > limit) break;
            int nameLength = (data[index++] << 8) | data[index++];
            if (index + nameLength + 2 > limit) break;
            string name = nameLength == 0 && currentName is not null
                ? currentName
                : Encoding.UTF8.GetString(data, index, nameLength);
            index += nameLength;
            int valueLength = (data[index++] << 8) | data[index++];
            if (index + valueLength > limit) break;
            string value = Encoding.UTF8.GetString(data, index, valueLength);
            index += valueLength;
            if (nameLength > 0) currentName = name;
            if (string.Equals(name, attributeName, StringComparison.OrdinalIgnoreCase)) return value;
        }
        return null;
    }

    private static IReadOnlyList<string> GetAttributeValues(byte[] data, int endTagIndex, string attributeName)
    {
        var values = new List<string>();
        int limit = endTagIndex > 8 ? endTagIndex : data.Length;
        int index = 8;
        string? currentName = null;
        while (index < limit)
        {
            byte valueTag = data[index++];
            if (valueTag == 0x03) break;
            if (IsGroupDelimiter(valueTag)) continue;
            if (index + 2 > limit) break;
            int nameLength = (data[index++] << 8) | data[index++];
            if (index + nameLength + 2 > limit) break;
            string name = nameLength == 0 && currentName is not null
                ? currentName
                : Encoding.UTF8.GetString(data, index, nameLength);
            index += nameLength;
            int valueLength = (data[index++] << 8) | data[index++];
            if (index + valueLength > limit) break;
            string value = Encoding.UTF8.GetString(data, index, valueLength);
            index += valueLength;
            if (nameLength > 0) currentName = name;
            if (string.Equals(name, attributeName, StringComparison.OrdinalIgnoreCase))
                values.Add(value);
        }
        return values;
    }

    private void LogResponseAttributeMetadata(byte[] response, int requestId)
    {
        if (!TryDecodeIppAttributes(response, out var attributes, out string error))
        {
            OnLog?.Invoke(this, $"[IPP Discovery Response] requestId={requestId}, decodeError={error}");
            return;
        }

        string semanticError = TryValidateIppAttributeSemantics(attributes, out string validationError)
            ? "none"
            : validationError;

        OnLog?.Invoke(this,
            $"[IPP Discovery Response] requestId={requestId}, attributes=" +
            string.Join(",", attributes.Select(attribute =>
                $"group=0x{attribute.GroupTag:X2};name={attribute.Name};tag=0x{attribute.ValueTag:X2};value={DescribeIppValue(attribute.ValueTag, attribute.Value)}")) +
            $", semanticError={semanticError}");
    }

    /// <summary>
    /// Decodes an IPP message body after the 8-byte header and validates the
    /// name/value framing required by RFC 8010. Values are returned as raw
    /// bytes so callers can apply the correct value-tag semantics.
    /// </summary>
    internal static bool TryDecodeIppAttributes(
        byte[] data,
        out IReadOnlyList<IppAttributeMetadata> attributes,
        out string error)
    {
        var decoded = new List<IppAttributeMetadata>();
        attributes = decoded;
        error = string.Empty;

        if (data.Length < 9)
            return Fail("response header or end-of-attributes is truncated", out error);

        int index = 8;
        byte groupTag = 0;
        string? currentName = null;
        bool sawEndOfAttributes = false;

        while (index < data.Length)
        {
            int attributeOffset = index;
            byte valueTag = data[index++];
            if (valueTag == 0x03)
            {
                sawEndOfAttributes = true;
                break;
            }

            if (IsGroupDelimiter(valueTag))
            {
                groupTag = valueTag;
                currentName = null;
                continue;
            }

            if (groupTag == 0)
                return Fail($"attribute at offset {attributeOffset} appears before a group delimiter", out error);
            if (index + 2 > data.Length)
                return Fail($"attribute at offset {attributeOffset} has no name length", out error);

            int nameLength = (data[index++] << 8) | data[index++];
            if (nameLength > 255 || index + nameLength + 2 > data.Length)
                return Fail($"attribute at offset {attributeOffset} has an invalid name length", out error);

            if (nameLength == 0)
            {
                if (currentName is null)
                    return Fail($"attribute continuation at offset {attributeOffset} has no preceding name", out error);
            }
            else
            {
                try
                {
                    currentName = StrictUtf8.GetString(data, index, nameLength);
                }
                catch (DecoderFallbackException)
                {
                    return Fail($"attribute at offset {attributeOffset} has an invalid UTF-8 name", out error);
                }
            }
            index += nameLength;

            int valueLength = (data[index++] << 8) | data[index++];
            if (index + valueLength > data.Length)
                return Fail($"attribute '{currentName}' at offset {attributeOffset} has an invalid value length", out error);

            decoded.Add(new IppAttributeMetadata(
                groupTag,
                valueTag,
                currentName!,
                data[index..(index + valueLength)],
                attributeOffset));
            index += valueLength;
        }

        if (!sawEndOfAttributes)
            return Fail("response is missing end-of-attributes", out error);
        if (index != data.Length)
            return Fail("response contains bytes after end-of-attributes", out error);

        return true;
    }

    internal sealed record IppAttributeMetadata(
        byte GroupTag,
        byte ValueTag,
        string Name,
        byte[] Value,
        int Offset);

    internal static bool TryValidateIppAttributeSemantics(
        IReadOnlyList<IppAttributeMetadata> attributes,
        out string error)
    {
        error = string.Empty;
        foreach (IppAttributeMetadata attribute in attributes)
        {
            if (attribute.GroupTag is not (0x01 or 0x04))
                return Fail($"attribute '{attribute.Name}' uses unsupported group 0x{attribute.GroupTag:X2}", out error);

            if (attribute.Name.Any(character => character is < (char)0x21 or > (char)0x7E))
                return Fail($"attribute '{attribute.Name}' contains a non-US-ASCII name character", out error);

            if (!TryGetExpectedValueTag(attribute.Name, out byte expectedTag))
                continue;

            if (attribute.ValueTag != expectedTag)
                return Fail($"attribute '{attribute.Name}' uses tag 0x{attribute.ValueTag:X2}, expected 0x{expectedTag:X2}", out error);

            if (expectedTag is 0x21 or 0x23 && attribute.Value.Length != 4)
                return Fail($"attribute '{attribute.Name}' has {attribute.Value.Length} bytes, expected 4", out error);
            if (expectedTag == 0x22 && attribute.Value.Length != 1)
                return Fail($"attribute '{attribute.Name}' has {attribute.Value.Length} bytes, expected 1", out error);
            if (expectedTag is 0x41 or 0x42 or 0x44 or 0x45 or 0x47 or 0x48 or 0x49)
            {
                try { StrictUtf8.GetString(attribute.Value); }
                catch (DecoderFallbackException)
                {
                    return Fail($"attribute '{attribute.Name}' contains invalid UTF-8", out error);
                }
            }
        }

        return true;
    }

    private static bool TryGetExpectedValueTag(string name, out byte tag)
    {
        tag = name switch
        {
            "attributes-charset" or "charset-configured" or "charset-supported" => 0x47,
            "attributes-natural-language" or "natural-language-configured" or "generated-natural-language-supported" => 0x48,
            "printer-uri-supported" or "printer-uuid" or "printer-more-info" => 0x45,
            "printer-name" or "printer-dns-sd-name" or "printer-firmware-name" => 0x42,
            "printer-info" or "printer-location" or "printer-make-and-model" or "printer-state-message" or
                "printer-firmware-string-version" or "printer-device-id" or "mopria-certified" or
                "status-message" => 0x41,
            "uri-authentication-supported" or "uri-security-supported" or "ipp-versions-supported" or
                "printer-state-reasons" or "compression-supported" or "pdl-override-supported" or
                "media-supported" or "media-type-supported" => 0x44,
            "operations-supported" or "printer-state" or "printer-type" => 0x23,
            "queued-job-count" or "printer-up-time" => 0x21,
            "printer-is-accepting-jobs" or "multiple-document-jobs-supported" or "color-supported" => 0x22,
            "document-format-default" or "document-format-preferred" or "document-format-supported" => 0x49,
            _ => 0
        };
        return tag != 0;
    }

    private static string DescribeIppValue(byte valueTag, byte[] value)
    {
        if (valueTag == 0x22 && value.Length == 1)
            return value[0] == 0 ? "false" : value[0] == 1 ? "true" : $"invalid-bool(0x{value[0]:X2})";
        if (valueTag is 0x21 or 0x23 && value.Length == 4)
            return $"{(value[0] << 24) | (value[1] << 16) | (value[2] << 8) | value[3]}";
        if (valueTag is 0x45 or 0x41 or 0x42 or 0x44 or 0x47 or 0x48 or 0x49)
        {
            string text;
            try { text = StrictUtf8.GetString(value); }
            catch { return $"<invalid-utf8:{value.Length}-bytes>"; }
            text = new string(text.Where(character => !char.IsControl(character)).ToArray());
            return text.Length <= 160 ? text : text[..160] + "...";
        }
        return $"<{value.Length}-bytes>";
    }

    private static bool Fail(string message, out string error)
    {
        error = message;
        return false;
    }

    private static bool TryFindAttributesEnd(byte[] data, out int endTagIndex)
    {
        endTagIndex = -1;
        if (data.Length < 9) return false;

        int index = 8;
        while (index < data.Length)
        {
            byte valueTag = data[index++];
            if (valueTag == 0x03)
            {
                endTagIndex = index - 1;
                return true;
            }

            if (IsGroupDelimiter(valueTag)) continue;
            if (index + 2 > data.Length) return false;

            int nameLength = (data[index++] << 8) | data[index++];
            if (index + nameLength + 2 > data.Length) return false;
            index += nameLength;

            int valueLength = (data[index++] << 8) | data[index++];
            if (index + valueLength > data.Length) return false;
            index += valueLength;
        }

        return false;
    }

    private static bool IsGroupDelimiter(byte tag) => tag is >= 0x01 and <= 0x02 or >= 0x04 and <= 0x08;

    private static bool IsSupportedIppVersion(byte major, byte minor) =>
        (major == 1 && minor <= 1) || (major == 2 && minor == 0);

    private static bool IsSupportedDocumentFormat(string value) =>
        value.Equals("application/pdf", StringComparison.OrdinalIgnoreCase) ||
        value.Equals("application/octet-stream", StringComparison.OrdinalIgnoreCase);

    private static string SanitizeDocumentName(string value)
    {
        string cleaned = new(value.Where(c => char.IsLetterOrDigit(c) || c is '.' or '_' or '-' or ' ').ToArray());
        return string.IsNullOrWhiteSpace(cleaned) ? "RemoteDocument" : cleaned.Trim();
    }

    private byte[] CreateSimpleIppResponse(byte major, byte minor, short statusCode, int reqId)
    {
        using var ms = new MemoryStream();
        ms.WriteByte(major);
        ms.WriteByte(minor);
        ms.WriteByte((byte)(statusCode >> 8));
        ms.WriteByte((byte)(statusCode & 0xFF));
        ms.WriteByte((byte)(reqId >> 24));
        ms.WriteByte((byte)(reqId >> 16));
        ms.WriteByte((byte)(reqId >> 8));
        ms.WriteByte((byte)(reqId & 0xFF));
        ms.WriteByte(0x03); // end-of-attributes
        return ms.ToArray();
    }

    private byte[] CreateSimpleIppResponse(byte major, byte minor, ushort statusCode, int reqId) =>
        CreateSimpleIppResponse(major, minor, unchecked((short)statusCode), reqId);

    private static byte[] CreateOperationSuccessResponse(byte major, byte minor, int reqId)
    {
        using var ms = new MemoryStream();
        ms.WriteByte(major);
        ms.WriteByte(minor);
        ms.WriteByte(0x00);
        ms.WriteByte(0x00);
        ms.WriteByte((byte)(reqId >> 24));
        ms.WriteByte((byte)(reqId >> 16));
        ms.WriteByte((byte)(reqId >> 8));
        ms.WriteByte((byte)reqId);
        ms.WriteByte(0x01);
        WriteAttribute(ms, 0x47, "attributes-charset", "utf-8");
        WriteAttribute(ms, 0x48, "attributes-natural-language", "en-us");
        ms.WriteByte(0x03);
        return ms.ToArray();
    }

    private byte[] CreatePrinterAttributesResponse(
        byte major,
        byte minor,
        int reqId,
        Uri? requestUri,
        string? requestedPrinterUri,
        IReadOnlySet<string>? requestedAttributes)
    {
        using var ms = new MemoryStream();
        ms.WriteByte(major);
        ms.WriteByte(minor);
        ms.WriteByte(0x00); // 0x0000 = successful-ok
        ms.WriteByte(0x00);
        ms.WriteByte((byte)(reqId >> 24));
        ms.WriteByte((byte)(reqId >> 16));
        ms.WriteByte((byte)(reqId >> 8));
        ms.WriteByte((byte)(reqId & 0xFF));

        // Operation attributes group
        ms.WriteByte(0x01);
        WriteAttribute(ms, 0x47, "attributes-charset", "utf-8");
        WriteAttribute(ms, 0x48, "attributes-natural-language", "en-us");
        WriteAttribute(ms, 0x41, "status-message", "successful-ok");

        // Printer attributes group (RFC 8011 section 4.2.5).
        ms.WriteByte(0x04);
        WriteRequested(ms, requestedAttributes, 0x45, "printer-uri-supported", GetPrinterUri(requestUri, requestedPrinterUri));
        WriteRequested(ms, requestedAttributes, 0x44, "uri-authentication-supported", "none");
        WriteRequested(ms, requestedAttributes, 0x44, "uri-security-supported", "none");
        WriteRequestedSet(ms, requestedAttributes, 0x44, "ipp-versions-supported", "1.1", "2.0");
        WriteRequested(ms, requestedAttributes, 0x47, "charset-configured", "utf-8");
        WriteRequested(ms, requestedAttributes, 0x47, "charset-supported", "utf-8");
        WriteRequested(ms, requestedAttributes, 0x48, "natural-language-configured", "en-us");
        WriteRequested(ms, requestedAttributes, 0x48, "generated-natural-language-supported", "en-us");
        WriteRequested(ms, requestedAttributes, 0x45, "printer-uuid", _printerUuid);
        WriteRequested(ms, requestedAttributes, 0x41, "printer-location", "Local RPM virtual printer");
        WriteRequested(ms, requestedAttributes, 0x41, "printer-state-message", "Ready");
        WriteRequestedIntSet(ms, requestedAttributes, 0x23, "operations-supported", 0x0002, 0x0004, 0x0005, 0x0006, 0x0008, 0x0009, 0x000A, 0x000B);
        WriteRequested(ms, requestedAttributes, 0x42, "printer-name", PrinterName);
        WriteRequested(ms, requestedAttributes, 0x41, "printer-info", "Remote Print Manager virtual IPP queue");
        WriteRequested(ms, requestedAttributes, 0x41, "printer-make-and-model", "Remote Print Manager IPP Class Queue");
        string canonicalPrinterUri = GetPrinterUri(requestUri, requestedPrinterUri);
        WriteRequested(ms, requestedAttributes, 0x45, "printer-more-info", GetHttpPrinterUri(canonicalPrinterUri));
        WriteRequested(ms, requestedAttributes, 0x42, "printer-dns-sd-name", PrinterName);
        WriteRequested(ms, requestedAttributes, 0x42, "printer-firmware-name", "RemotePrintManager");
        WriteRequested(ms, requestedAttributes, 0x41, "printer-firmware-string-version", "1.0");
        WriteRequested(ms, requestedAttributes, 0x41, "printer-device-id", "MFG:Remote Print Manager;MDL:IPP Class Queue;CMD:RAW;");
        // Windows IPP Class Driver uses the Mopria profile marker during
        // directed discovery. CUPS emits this as text(255), not boolean.
        WriteRequested(ms, requestedAttributes, 0x41, "mopria-certified", "1.3");
        WriteRequestedInt(ms, requestedAttributes, 3, "printer-state", 0x23);
        WriteRequested(ms, requestedAttributes, 0x44, "printer-state-reasons", "none");
        WriteRequestedBool(ms, requestedAttributes, "printer-is-accepting-jobs", true);
        WriteRequestedBool(ms, requestedAttributes, "multiple-document-jobs-supported", false);
        WriteRequestedBool(ms, requestedAttributes, "color-supported", false);
        // printer-type is an IPP enum (RFC 8011), not an integer.
        // Bit 0 identifies a printer (0 is not a valid printer-type value).
        WriteRequestedInt(ms, requestedAttributes, 0x00000001, "printer-type", 0x23);
        WriteRequestedInt(ms, requestedAttributes, 0, "queued-job-count", 0x21);
        WriteRequestedInt(ms, requestedAttributes, (int)Math.Max(1, Environment.TickCount64 / 1000), "printer-up-time", 0x21);
        WriteRequested(ms, requestedAttributes, 0x44, "compression-supported", "none");
        WriteRequested(ms, requestedAttributes, 0x44, "pdl-override-supported", "not-attempted");
        // Microsoft IPP Class Driver's manifest declares application/pdf as
        // its rendered output format. Keep octet-stream for the existing raw
        // test path, but do not advertise other PDLs without a converter.
        WriteRequested(ms, requestedAttributes, 0x49, "document-format-default", "application/pdf");
        WriteRequested(ms, requestedAttributes, 0x49, "document-format-preferred", "application/pdf");
        WriteRequestedSet(ms, requestedAttributes, 0x49, "document-format-supported", "application/pdf", "application/octet-stream");
        WriteRequestedSet(ms, requestedAttributes, 0x44, "media-supported", "iso_a4_210x297mm", "na_letter_8.5x11in");
        WriteRequested(ms, requestedAttributes, 0x44, "media-type-supported", "stationery");
        ms.WriteByte(0x03); // end of attributes
        return ms.ToArray();
    }

    private static bool IsRequested(IReadOnlySet<string>? requestedAttributes, string attributeName) =>
        requestedAttributes is null ||
        requestedAttributes.Contains("all") ||
        requestedAttributes.Contains(attributeName) ||
        (requestedAttributes.Contains("printer-description") && attributeName is not "printer-state" and not "printer-state-reasons") ||
        (requestedAttributes.Contains("printer-status") && attributeName is "printer-state" or "printer-state-reasons" or "printer-is-accepting-jobs" or "queued-job-count");

    private void WriteRequested(MemoryStream stream, IReadOnlySet<string>? requested, byte tag, string name, string value)
    {
        if (IsRequested(requested, name)) WriteAttribute(stream, tag, name, value);
    }

    private void WriteRequestedSet(
        MemoryStream stream,
        IReadOnlySet<string>? requested,
        byte tag,
        string name,
        params string[] values)
    {
        if (!IsRequested(requested, name) || values.Length == 0) return;
        WriteAttribute(stream, tag, name, values[0]);
        for (int index = 1; index < values.Length; index++)
            WriteAttributeContinuation(stream, tag, values[index]);
    }

    private void WriteRequestedInt(MemoryStream stream, IReadOnlySet<string>? requested, int value, string name, byte tag)
    {
        if (IsRequested(requested, name)) WriteAttributeInt(stream, tag, name, value);
    }

    private void WriteRequestedIntSet(
        MemoryStream stream,
        IReadOnlySet<string>? requested,
        byte tag,
        string name,
        params int[] values)
    {
        if (!IsRequested(requested, name) || values.Length == 0) return;
        WriteAttributeInt(stream, tag, name, values[0]);
        for (int index = 1; index < values.Length; index++)
            WriteAttributeIntContinuation(stream, tag, values[index]);
    }

    private void WriteRequestedBool(MemoryStream stream, IReadOnlySet<string>? requested, string name, bool value)
    {
        if (IsRequested(requested, name)) WriteAttributeBool(stream, name, value);
    }

    private string GetPrinterUri(Uri? requestUri, string? requestedPrinterUri)
    {
        if (IsSupportedPrinterUri(requestedPrinterUri))
            return requestedPrinterUri!;

        if (requestUri is not null && IsPrinterResource(requestUri))
            return $"ipp://{requestUri.Host}:{requestUri.Port}/printers/rpm";
        return $"ipp://127.0.0.1:{Port}/printers/rpm";
    }

    private static string GetHttpPrinterUri(string printerUri)
    {
        if (Uri.TryCreate(printerUri, UriKind.Absolute, out var uri))
            return $"http://{uri.Host}:{uri.Port}{uri.AbsolutePath}";
        return printerUri;
    }

    private static bool IsSupportedPrinterUri(string? value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)) return false;
        if (!IsLocalHost(uri.Host))
            return false;
        if (!string.Equals(uri.AbsolutePath.TrimEnd('/'), "/printers/rpm", StringComparison.OrdinalIgnoreCase))
            return false;
        return uri.Scheme.Equals("ipp", StringComparison.OrdinalIgnoreCase) ||
            uri.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsLocalHost(string host)
    {
        if (string.Equals(host, "127.0.0.1", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(host, Environment.MachineName, StringComparison.OrdinalIgnoreCase))
            return true;

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

    private byte[] CreatePrintJobResponse(byte major, byte minor, int reqId, int jobId)
    {
        using var ms = new MemoryStream();
        ms.WriteByte(major);
        ms.WriteByte(minor);
        ms.WriteByte(0x00); // successful-ok
        ms.WriteByte(0x00);
        ms.WriteByte((byte)(reqId >> 24));
        ms.WriteByte((byte)(reqId >> 16));
        ms.WriteByte((byte)(reqId >> 8));
        ms.WriteByte((byte)(reqId & 0xFF));

        ms.WriteByte(0x01); // operation attributes
        WriteAttribute(ms, 0x47, "attributes-charset", "utf-8");
        WriteAttribute(ms, 0x48, "attributes-natural-language", "en-us");

        ms.WriteByte(0x02); // job attributes
        WriteAttributeInt(ms, 0x21, "job-id", jobId);
        WriteAttributeInt(ms, 0x23, "job-state", 3); // 3 = pending
        WriteAttribute(ms, 0x44, "job-state-reasons", "job-incoming");

        ms.WriteByte(0x03);
        return ms.ToArray();
    }

    private byte[] CreateJobAttributesResponse(byte major, byte minor, int reqId, Uri? requestUri)
    {
        using var ms = new MemoryStream();
        ms.WriteByte(major);
        ms.WriteByte(minor);
        ms.WriteByte(0x00);
        ms.WriteByte(0x00);
        ms.WriteByte((byte)(reqId >> 24));
        ms.WriteByte((byte)(reqId >> 16));
        ms.WriteByte((byte)(reqId >> 8));
        ms.WriteByte((byte)reqId);

        ms.WriteByte(0x01);
        WriteAttribute(ms, 0x47, "attributes-charset", "utf-8");
        WriteAttribute(ms, 0x48, "attributes-natural-language", "en-us");

        ms.WriteByte(0x02);
        WriteAttributeInt(ms, 0x21, "job-id", 1);
        WriteAttribute(ms, 0x45, "job-uri", $"ipp://{requestUri?.Host ?? "127.0.0.1"}:{requestUri?.Port ?? Port}/printers/rpm/jobs/1");
        WriteAttribute(ms, 0x45, "job-printer-uri", GetPrinterUri(requestUri, null));
        WriteAttribute(ms, 0x42, "job-name", "Remote Print Manager job");
        WriteAttributeInt(ms, 0x23, "job-state", 3);
        WriteAttribute(ms, 0x44, "job-state-reasons", "job-incoming");
        WriteAttribute(ms, 0x42, "job-originating-user-name", "RemotePrintManager");

        ms.WriteByte(0x03);
        return ms.ToArray();
    }

    private static byte[] CreateGetJobsResponse(byte major, byte minor, int reqId)
    {
        using var ms = new MemoryStream();
        ms.WriteByte(major);
        ms.WriteByte(minor);
        ms.WriteByte(0x00);
        ms.WriteByte(0x00);
        ms.WriteByte((byte)(reqId >> 24));
        ms.WriteByte((byte)(reqId >> 16));
        ms.WriteByte((byte)(reqId >> 8));
        ms.WriteByte((byte)reqId);

        ms.WriteByte(0x01);
        WriteAttribute(ms, 0x47, "attributes-charset", "utf-8");
        WriteAttribute(ms, 0x48, "attributes-natural-language", "en-us");
        ms.WriteByte(0x03);
        return ms.ToArray();
    }

    private static void WriteAttribute(MemoryStream ms, byte tag, string name, string value)
    {
        ms.WriteByte(tag);
        byte[] nameBytes = Encoding.ASCII.GetBytes(name);
        ms.WriteByte((byte)(nameBytes.Length >> 8));
        ms.WriteByte((byte)(nameBytes.Length & 0xFF));
        ms.Write(nameBytes, 0, nameBytes.Length);

        byte[] valBytes = Encoding.UTF8.GetBytes(value);
        ms.WriteByte((byte)(valBytes.Length >> 8));
        ms.WriteByte((byte)(valBytes.Length & 0xFF));
        ms.Write(valBytes, 0, valBytes.Length);
    }

    private static void WriteAttributeContinuation(MemoryStream ms, byte tag, string value)
    {
        byte[] valBytes = Encoding.UTF8.GetBytes(value);
        ms.WriteByte(tag);
        ms.WriteByte(0);
        ms.WriteByte(0);
        ms.WriteByte((byte)(valBytes.Length >> 8));
        ms.WriteByte((byte)(valBytes.Length & 0xFF));
        ms.Write(valBytes, 0, valBytes.Length);
    }

    private void WriteAttributeInt(MemoryStream ms, byte tag, string name, int value)
    {
        ms.WriteByte(tag);
        byte[] nameBytes = Encoding.ASCII.GetBytes(name);
        ms.WriteByte((byte)(nameBytes.Length >> 8));
        ms.WriteByte((byte)(nameBytes.Length & 0xFF));
        ms.Write(nameBytes, 0, nameBytes.Length);

        ms.WriteByte(0x00);
        ms.WriteByte(0x04); // 4-byte int
        ms.WriteByte((byte)(value >> 24));
        ms.WriteByte((byte)(value >> 16));
        ms.WriteByte((byte)(value >> 8));
        ms.WriteByte((byte)(value & 0xFF));
    }

    private static void WriteAttributeIntContinuation(MemoryStream ms, byte tag, int value)
    {
        ms.WriteByte(tag);
        ms.WriteByte(0);
        ms.WriteByte(0);
        ms.WriteByte(0);
        ms.WriteByte(0x04);
        ms.WriteByte((byte)(value >> 24));
        ms.WriteByte((byte)(value >> 16));
        ms.WriteByte((byte)(value >> 8));
        ms.WriteByte((byte)value);
    }

    private void WriteAttributeBool(MemoryStream ms, string name, bool value)
    {
        byte[] nameBytes = Encoding.ASCII.GetBytes(name);
        ms.WriteByte(0x22);
        ms.WriteByte((byte)(nameBytes.Length >> 8));
        ms.WriteByte((byte)(nameBytes.Length & 0xFF));
        ms.Write(nameBytes, 0, nameBytes.Length);
        ms.WriteByte(0x00);
        ms.WriteByte(0x01);
        ms.WriteByte(value ? (byte)1 : (byte)0);
    }

    public void Dispose()
    {
        Stop();
    }
}
