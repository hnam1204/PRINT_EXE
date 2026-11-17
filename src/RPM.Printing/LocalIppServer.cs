using System.Net;
using System.Text;
using System.Diagnostics;
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
    private const ushort GetPrinterAttributesOperation = 0x000B;
    private const ushort SuccessfulOk = 0x0000;
    private const ushort ClientErrorBadRequest = 0x0400;
    private const ushort ClientErrorNotFound = 0x0406;
    private const ushort ServerErrorOperationNotSupported = 0x0501;
    private const int MaxIppHeaderBytes = 1024 * 1024;

    private HttpListener? _listener;
    private CancellationTokenSource? _cts;
    private bool _isRunning;
    public int Port { get; private set; }
    public string PrinterName { get; set; } = "Remote Print Manager";

    public event EventHandler<PrintJobReceivedEventArgs>? OnJobReceived;
    public event EventHandler<string>? OnLog;

    public bool IsRunning => _isRunning;

    public void Start(int port = 9199)
    {
        if (_isRunning) return;
        Port = port;

        try
        {
            _listener = new HttpListener();
            _listener.Prefixes.Add($"http://127.0.0.1:{Port}/");
            _listener.Prefixes.Add($"http://localhost:{Port}/");
            _listener.Start();

            _cts = new CancellationTokenSource();
            _isRunning = true;
            Task.Run(() => ListenLoopAsync(_cts.Token));
            OnLog?.Invoke(this, $"[IPP] Dịch vụ máy in ảo đã khởi động trên cổng {Port}.");
        }
        catch (Exception ex)
        {
            OnLog?.Invoke(this, $"[IPP Error] Không thể khởi động cổng {Port}: {ex.Message}");
            _isRunning = false;
        }
    }

    public void Stop()
    {
        _isRunning = false;
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
                $"requestId={requestId}, httpStatus={httpStatus}, ippStatus=0x{ippStatus:X4}, " +
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
        out ushort ippStatus,
        out string? parseError,
        Uri? requestUri)
    {
        capturedJob = null;
        documentData = null;
        operationId = 0;
        requestId = 0;
        ippStatus = ClientErrorBadRequest;
        parseError = null;

        if (data.Length < 8)
        {
            parseError = "header-too-short";
            return CreateSimpleIppResponse(0x01, 0x01, ClientErrorBadRequest, 1);
        }

        byte major = data[0];
        byte minor = data[1];
        operationId = (ushort)((data[2] << 8) | data[3]);
        requestId = (data[4] << 24) | (data[5] << 16) | (data[6] << 8) | data[7];

        if (major != 1 || minor > 1)
        {
            parseError = "unsupported-ipp-version";
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
            return CreatePrinterAttributesResponse(major, minor, requestId, requestUri);
        }

        if (operationId == ValidateJobOperation)
        {
            ippStatus = SuccessfulOk;
            return CreateOperationSuccessResponse(major, minor, requestId);
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

    private static bool IsGroupDelimiter(byte tag) => tag is >= 0x01 and <= 0x02 or >= 0x04 and <= 0x07;

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

    private byte[] CreatePrinterAttributesResponse(byte major, byte minor, int reqId, Uri? requestUri)
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

        // Printer attributes group
        ms.WriteByte(0x02);
        WriteAttribute(ms, 0x45, "printer-uri-supported", GetPrinterUri(requestUri));
        WriteAttribute(ms, 0x44, "uri-authentication-supported", "none");
        WriteAttribute(ms, 0x44, "uri-security-supported", "none");
        WriteAttribute(ms, 0x44, "ipp-versions-supported", "1.1");
        WriteAttribute(ms, 0x47, "charset-configured", "utf-8");
        WriteAttribute(ms, 0x47, "charset-supported", "utf-8");
        WriteAttribute(ms, 0x48, "natural-language-configured", "en-us");
        WriteAttribute(ms, 0x48, "generated-natural-language-supported", "en-us");
        WriteAttributeInt(ms, 0x23, "operations-supported", 0x0002); // Print-Job
        WriteAttributeInt(ms, 0x23, "operations-supported", 0x0004); // Validate-Job
        WriteAttributeInt(ms, 0x23, "operations-supported", 0x0005); // Create-Job
        WriteAttributeInt(ms, 0x23, "operations-supported", 0x0006); // Send-Document
        WriteAttributeInt(ms, 0x23, "operations-supported", 0x000B); // Get-Printer-Attributes
        WriteAttribute(ms, 0x42, "printer-name", PrinterName);
        WriteAttribute(ms, 0x42, "printer-info", "Remote Print Manager virtual IPP queue");
        WriteAttribute(ms, 0x42, "printer-make-and-model", "Remote Print Manager IPP Class Queue");
        WriteAttributeInt(ms, 0x23, "printer-state", 3); // 3 = idle / ready
        WriteAttribute(ms, 0x44, "printer-state-reasons", "none");
        WriteAttributeBool(ms, "printer-is-accepting-jobs", true);
        WriteAttributeInt(ms, 0x21, "queued-job-count", 0);
        WriteAttribute(ms, 0x49, "document-format-supported", "application/octet-stream");
        WriteAttribute(ms, 0x49, "document-format-supported", "application/pdf");
        WriteAttribute(ms, 0x49, "document-format-supported", "application/postscript");

        ms.WriteByte(0x03); // end of attributes
        return ms.ToArray();
    }

    private string GetPrinterUri(Uri? requestUri)
    {
        if (requestUri is not null && IsPrinterResource(requestUri))
            return $"{requestUri.Scheme}://{requestUri.Host}:{requestUri.Port}/printers/rpm";
        return $"http://127.0.0.1:{Port}/printers/rpm";
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
