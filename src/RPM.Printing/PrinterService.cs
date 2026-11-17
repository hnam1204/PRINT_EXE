using System.Drawing;
using System.Drawing.Printing;
using System.IO;
using System.Management;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Reflection;
using Windows.Data.Pdf;
using Windows.Storage;
using Windows.Storage.Streams;
using RPM.Core.Models;

namespace RPM.Printing;

public class PrinterService
{
    public List<SharedPrinterInfo> GetInstalledPrinters()
    {
        var map = new Dictionary<string, SharedPrinterInfo>(StringComparer.OrdinalIgnoreCase);

        // 1. Query real hardware and port details via WMI Win32_Printer
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            try
            {
                using var searcher = new ManagementObjectSearcher(
                    "SELECT Name, DriverName, PortName, Default, WorkOffline, PrinterStatus, ExtendedPrinterStatus, SpoolEnabled FROM Win32_Printer"
                );

                foreach (ManagementObject obj in searcher.Get())
                {
                    string name = obj["Name"]?.ToString() ?? string.Empty;
                    if (string.IsNullOrWhiteSpace(name)) continue;

                    string driver = obj["DriverName"]?.ToString() ?? string.Empty;
                    string port = obj["PortName"]?.ToString() ?? string.Empty;
                    bool isDefault = (bool)(obj["Default"] ?? false);
                    bool workOffline = (bool)(obj["WorkOffline"] ?? false);
                    uint statusVal = 3;
                    if (obj["PrinterStatus"] != null)
                    {
                        uint.TryParse(obj["PrinterStatus"].ToString(), out statusVal);
                    }

                    string statusStr = "Ready";
                    if (workOffline)
                    {
                        statusStr = "Offline";
                    }
                    else
                    {
                        statusStr = statusVal switch
                        {
                            3 => "Ready",
                            4 => "Printing",
                            5 => "Warmup",
                            7 => "Offline",
                            1 => "Paused",
                            2 => "Error",
                            _ => "Ready"
                        };
                    }

                    map[name] = new SharedPrinterInfo
                    {
                        Name = name,
                        DriverName = driver,
                        PortName = port,
                        IsDefault = isDefault,
                        Status = statusStr
                    };
                }
            }
            catch
            {
                // Fallback to Win32 Spooler API
            }
        }

        // 2. Query capabilities (Color, Duplex, PaperSizes) via PrinterSettings
        string defaultPrinter = string.Empty;
        try
        {
            var defaultSettings = new PrinterSettings();
            if (defaultSettings.IsValid)
            {
                defaultPrinter = defaultSettings.PrinterName;
            }
        }
        catch { }

        foreach (string printerName in PrinterSettings.InstalledPrinters)
        {
            if (!map.TryGetValue(printerName, out var info))
            {
                info = new SharedPrinterInfo
                {
                    Name = printerName,
                    IsDefault = string.Equals(printerName, defaultPrinter, StringComparison.OrdinalIgnoreCase),
                    Status = "Ready"
                };
                map[printerName] = info;
            }

            try
            {
                var settings = new PrinterSettings { PrinterName = printerName };
                if (settings.IsValid)
                {
                    info.SupportsColor = settings.SupportsColor;
                    info.SupportsDuplex = settings.CanDuplex;

                    foreach (PaperSize paper in settings.PaperSizes)
                    {
                        info.PaperSizes.Add(paper.PaperName);
                    }
                }
            }
            catch
            {
                info.Status = "Unavailable";
            }
        }

        return map.Values.OrderByDescending(x => x.IsDefault).ThenBy(x => x.Name).ToList();
    }

    public int GetActiveSpoolerJobsCount()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return 0;
        try
        {
            using var searcher = new ManagementObjectSearcher("SELECT Document, JobStatus FROM Win32_PrintJob");
            return searcher.Get().Count;
        }
        catch
        {
            return 0;
        }
    }

    public bool PrintTestPage(string printerName, string clientName, string serverName, out string errorMessage)
    {
        errorMessage = string.Empty;
        try
        {
            using var printDoc = new PrintDocument();
            printDoc.PrinterSettings.PrinterName = printerName;

            if (!printDoc.PrinterSettings.IsValid)
            {
                errorMessage = $"Máy in '{printerName}' không hợp lệ hoặc không thể truy cập.";
                return false;
            }

            printDoc.DefaultPageSettings.Landscape = false;
            PaperSize? a4Paper = printDoc.PrinterSettings.PaperSizes
                .Cast<PaperSize>()
                .FirstOrDefault(paper => paper.Kind == PaperKind.A4);
            if (a4Paper is not null)
            {
                printDoc.DefaultPageSettings.PaperSize = a4Paper;
            }

            printDoc.DocumentName = $"RPM Test Page - {DateTime.Now:yyyyMMdd_HHmmss}";

            printDoc.PrintPage += (sender, e) =>
            {
                if (e.Graphics == null) return;

                var g = e.Graphics;
                using var titleFont = new Font("Segoe UI", 19, FontStyle.Bold);
                using var headingFont = new Font("Segoe UI", 11, FontStyle.Bold);
                using var bodyFont = new Font("Segoe UI", 10, FontStyle.Regular);
                using var resultFont = new Font("Segoe UI", 11, FontStyle.Bold);
                using var accentPen = new Pen(Color.FromArgb(0, 120, 215), 2);
                using var dividerPen = new Pen(Color.LightGray, 1);
                using var accentBrush = new SolidBrush(Color.FromArgb(0, 120, 215));
                using var textBrush = new SolidBrush(Color.FromArgb(35, 35, 35));
                using var resultBrush = new SolidBrush(Color.FromArgb(0, 105, 70));
                using var format = new StringFormat
                {
                    Alignment = StringAlignment.Near,
                    LineAlignment = StringAlignment.Near,
                    Trimming = StringTrimming.EllipsisCharacter
                };

                Rectangle bounds = e.MarginBounds;
                int left = bounds.Left;
                int width = bounds.Width;
                int y = bounds.Top;
                int labelWidth = Math.Min(170, width / 3);
                int valueLeft = left + labelWidth;
                int valueWidth = Math.Max(100, width - labelWidth);

                g.DrawRectangle(accentPen, left, y, width, 72);
                using (var titleFormat = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
                {
                    g.DrawString("REMOTE PRINT MANAGER (RPM)", titleFont, accentBrush,
                        new RectangleF(left + 12, y + 8, width - 24, 55), titleFormat);
                }

                y += 98;
                g.DrawString(
                    "Phần mềm quản lý và in ấn từ xa qua Internet, giúp kết nối máy Client với máy in tại Host.",
                    bodyFont,
                    textBrush,
                    new RectangleF(left, y, width, 48),
                    format);
                y += 68;

                g.DrawString("Thông tin kiểm tra", headingFont, accentBrush, left, y);
                y += 28;
                g.DrawLine(dividerPen, left, y, left + width, y);
                y += 16;

                void DrawRow(string label, string value)
                {
                    g.DrawString(label, headingFont, textBrush, new RectangleF(left, y, labelWidth - 10, 25), format);
                    g.DrawString(value, bodyFont, textBrush, new RectangleF(valueLeft, y, valueWidth, 25), format);
                    y += 31;
                }

                DrawRow("Tên máy in", printerName);
                DrawRow("Tên máy chủ", serverName);
                DrawRow("Thời gian in", DateTime.Now.ToString("dd/MM/yyyy HH:mm:ss"));
                DrawRow("Phát triển bởi", "Nguyễn Hải Nam");
                DrawRow("Phiên bản", GetCurrentApplicationVersion());

                y += 18;
                g.DrawLine(dividerPen, left, y, left + width, y);
                y += 24;
                g.DrawString("KẾT QUẢ", headingFont, accentBrush, left, y);
                y += 30;
                g.DrawString(
                    "Trang kiểm tra đã được gửi đến máy in thành công.",
                    resultFont,
                    resultBrush,
                    new RectangleF(left, y, width, 30),
                    format);
            };

            printDoc.Print();
            return true;
        }
        catch (Exception ex)
        {
            errorMessage = ex.Message;
            return false;
        }
    }

    private static string GetCurrentApplicationVersion()
    {
        Assembly assembly = Assembly.GetEntryAssembly() ?? typeof(PrinterService).Assembly;
        Version? version = assembly.GetName().Version;
        return version is null ? "Không xác định" : $"{version.Major}.{version.Minor}.{version.Build}";
    }

    public bool PrintRawBytes(string printerName, byte[] data, string documentName, out string errorMessage)
    {
        errorMessage = string.Empty;
        try
        {
            bool success = RawPrinterHelper.SendBytesToPrinter(printerName, data, documentName, "RAW");
            if (!success)
            {
                errorMessage = "Không thể gửi dữ liệu RAW tới Spooler máy in.";
            }
            return success;
        }
        catch (Exception ex)
        {
            errorMessage = ex.Message;
            return false;
        }
    }

    public bool PrintFileDirectly(string printerName, string filePath, string documentName, out string errorMessage)
    {
        errorMessage = string.Empty;
        if (!File.Exists(filePath))
        {
            errorMessage = $"Không tìm thấy file: {filePath}";
            return false;
        }

        try
        {
            bool success = string.Equals(Path.GetExtension(filePath), ".pdf", StringComparison.OrdinalIgnoreCase)
                ? PrintPdfFile(printerName, filePath, documentName, out errorMessage)
                : RawPrinterHelper.SendFileToPrinter(printerName, filePath, documentName, "RAW");
            if (!success)
                errorMessage = string.IsNullOrWhiteSpace(errorMessage)
                    ? "Không thể gửi tài liệu tới Spooler máy in."
                    : errorMessage;
            return success;
        }
        catch (Exception ex)
        {
            errorMessage = ex.Message;
            return false;
        }
    }

    private static bool PrintPdfFile(string printerName, string filePath, string documentName, out string errorMessage)
    {
        errorMessage = string.Empty;
        try
        {
            StorageFile file = StorageFile.GetFileFromPathAsync(filePath).AsTask().GetAwaiter().GetResult();
            PdfDocument pdf = PdfDocument.LoadFromFileAsync(file).AsTask().GetAwaiter().GetResult();
            if (pdf.PageCount == 0)
            {
                errorMessage = "Tài liệu PDF không có trang để in.";
                return false;
            }

            var pages = new List<Bitmap>((int)pdf.PageCount);
            try
            {
                for (uint index = 0; index < pdf.PageCount; index++)
                {
                    PdfPage page = pdf.GetPage(index);
                    if (page is null) throw new InvalidDataException($"Không thể đọc trang PDF {index + 1}.");
                    using var rendered = new InMemoryRandomAccessStream();
                    page.RenderToStreamAsync(rendered).AsTask().GetAwaiter().GetResult();
                    rendered.Seek(0);
                    using Stream imageStream = rendered.AsStreamForRead();
                    using var loaded = new Bitmap(imageStream);
                    pages.Add(new Bitmap(loaded));
                }

                using var printDocument = new PrintDocument
                {
                    PrinterSettings = new PrinterSettings { PrinterName = printerName },
                    DocumentName = documentName
                };
                if (!printDocument.PrinterSettings.IsValid)
                {
                    errorMessage = $"Máy in '{printerName}' không hợp lệ hoặc không thể truy cập.";
                    return false;
                }

                int pageIndex = 0;
                printDocument.PrintPage += (_, args) =>
                {
                    Bitmap page = pages[pageIndex++];
                    if (args.Graphics is null) throw new InvalidOperationException("Windows không cung cấp Graphics context cho trang PDF.");
                    Rectangle bounds = args.MarginBounds;
                    float scale = Math.Min(
                        (float)bounds.Width / page.Width,
                        (float)bounds.Height / page.Height);
                    int width = Math.Max(1, (int)(page.Width * scale));
                    int height = Math.Max(1, (int)(page.Height * scale));
                    int left = bounds.Left + (bounds.Width - width) / 2;
                    int top = bounds.Top + (bounds.Height - height) / 2;
                    args.Graphics.DrawImage(page, new Rectangle(left, top, width, height));
                    args.HasMorePages = pageIndex < pages.Count;
                };
                printDocument.Print();
                return true;
            }
            finally
            {
                foreach (Bitmap page in pages) page.Dispose();
            }
        }
        catch (Exception ex)
        {
            errorMessage = $"Không thể render PDF trước khi gửi tới Spooler: {ex.Message}";
            return false;
        }
    }
}
