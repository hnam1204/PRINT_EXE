using RPM.Printing;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Printing;

var evidencePath = Path.Combine(Path.GetTempPath(), "rpm-ipp-runtime-evidence.txt");
File.Delete(evidencePath);
void Record(string message)
{
    Console.WriteLine(message);
    File.AppendAllText(evidencePath, message + Environment.NewLine);
}

using var ippServer = new LocalIppServer();
ippServer.OnLog += (_, message) => Record(message);
ippServer.Start(9199);
await Task.Delay(500);

if (args.Contains("--probe-only", StringComparer.OrdinalIgnoreCase))
{
    const int probeRequestId = 0x01020304;
    string ippUrl = $"ipp://{Environment.MachineName}:9199/printers/rpm";
    byte[] requestBytes = BuildGetPrinterAttributesRequest(ippUrl, probeRequestId);
    using var http = new HttpClient(new HttpClientHandler { UseProxy = false });
    using var request = new HttpRequestMessage(HttpMethod.Post, "http://127.0.0.1:9199/printers/rpm")
    {
        Content = new ByteArrayContent(requestBytes)
    };
    request.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/ipp");
    using var response = await http.SendAsync(request);
    byte[] responseBytes = await response.Content.ReadAsByteArrayAsync();
    Record($"PROBE_HTTP={(int)response.StatusCode} CONTENT_TYPE={response.Content.Headers.ContentType}");
    Record($"PROBE_REQUEST_HEX={Convert.ToHexString(requestBytes)}");
    Record($"PROBE_RESPONSE_HEX={Convert.ToHexString(responseBytes)}");
    return;
}

if (args.Contains("--print-job", StringComparer.OrdinalIgnoreCase))
{
    var received = new TaskCompletionSource<PrintJobReceivedEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
    ippServer.OnJobReceived += (_, eventArgs) => received.TrySetResult(eventArgs);

    using var document = new PrintDocument
    {
        PrinterSettings = { PrinterName = VirtualPrinterManager.DefaultPrinterName },
        DocumentName = "RPM Windows IPP runtime test"
    };
    document.PrintPage += (_, eventArgs) =>
    {
        using var font = new Font("Arial", 14);
        eventArgs.Graphics?.DrawString("Remote Print Manager IPP runtime test", font, Brushes.Black, 50, 50);
        eventArgs.HasMorePages = false;
    };

    Record($"PRINT_PRINTER_EXISTS={document.PrinterSettings.IsValid}");
    document.Print();
    Task completed = await Task.WhenAny(received.Task, Task.Delay(TimeSpan.FromSeconds(30)));
    if (completed != received.Task)
    {
        Record("PRINT_JOB_RESULT=TIMEOUT");
        return;
    }

    PrintJobReceivedEventArgs print = await received.Task;
    Record($"PRINT_JOB_RESULT=RECEIVED FORMAT={print.Job.DocumentFormat} BYTES={print.DataLength}");
    if (print.TempFilePath is not null)
        File.Delete(print.TempFilePath);
    return;
}

if (args.Contains("--serve", StringComparer.OrdinalIgnoreCase))
{
    Record("SERVER_RESULT=READY");
    await Task.Delay(Timeout.InfiniteTimeSpan);
}

var result = await VirtualPrinterManager.InstallVirtualPrinterAsync(
    VirtualPrinterManager.DefaultPrinterName,
    9199,
    () =>
    {
        ippServer.Restart(9199);
        return ippServer.IsRunning && ippServer.IsLanBindingActive;
    });

Record($"Success={result.Success}");
Record(result.Message);
Record(VirtualPrinterManager.FindPrinterBoundToLocalIpp(9199) ?? "NO_QUEUE");

var alternate = new ProcessStartInfo
{
    FileName = "powershell.exe",
    Arguments = "-NoProfile -NonInteractive -Command \"Add-Printer -Name 'Remote Print Manager' -IppURL 'http://MSI:9199/printers/rpm' -ErrorAction Stop; Get-Printer -Name 'Remote Print Manager' | Format-List Name,DriverName,PortName\"",
    UseShellExecute = false,
    RedirectStandardOutput = true,
    RedirectStandardError = true,
    CreateNoWindow = true
};
using var alternateProcess = Process.Start(alternate);
if (alternateProcess is not null)
{
    string output = await alternateProcess.StandardOutput.ReadToEndAsync();
    string error = await alternateProcess.StandardError.ReadToEndAsync();
    alternateProcess.WaitForExit();
    Record($"HTTP_ALTERNATE_EXIT={alternateProcess.ExitCode}");
    if (!string.IsNullOrWhiteSpace(output)) Record(output.Trim());
    if (!string.IsNullOrWhiteSpace(error)) Record(error.Trim());
}

static byte[] BuildGetPrinterAttributesRequest(string ippUrl, int requestId)
{
    using var stream = new MemoryStream();
    stream.WriteByte(1);
    stream.WriteByte(1);
    WriteBigEndian(stream, (ushort)0x000B);
    WriteBigEndianInt(stream, requestId);
    stream.WriteByte(0x01);
    WriteAttribute(stream, 0x47, "attributes-charset", "utf-8");
    WriteAttribute(stream, 0x48, "attributes-natural-language", "en-us");
    WriteAttribute(stream, 0x45, "printer-uri", ippUrl);
    stream.WriteByte(0x03);
    return stream.ToArray();
}

static void WriteBigEndian(Stream stream, ushort value)
{
    stream.WriteByte((byte)(value >> 8));
    stream.WriteByte((byte)value);
}

static void WriteBigEndianInt(Stream stream, int value)
{
    stream.WriteByte((byte)(value >> 24));
    stream.WriteByte((byte)(value >> 16));
    stream.WriteByte((byte)(value >> 8));
    stream.WriteByte((byte)value);
}

static void WriteAttribute(Stream stream, byte tag, string name, string value)
{
    byte[] nameBytes = System.Text.Encoding.ASCII.GetBytes(name);
    byte[] valueBytes = System.Text.Encoding.UTF8.GetBytes(value);
    stream.WriteByte(tag);
    WriteBigEndian(stream, (ushort)nameBytes.Length);
    stream.Write(nameBytes);
    WriteBigEndian(stream, (ushort)valueBytes.Length);
    stream.Write(valueBytes);
}
