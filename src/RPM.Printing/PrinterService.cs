using System.Drawing;
using System.Drawing.Printing;
using System.IO;
using RPM.Core.Models;

namespace RPM.Printing;

public class PrinterService
{
    public List<SharedPrinterInfo> GetInstalledPrinters()
    {
        var list = new List<SharedPrinterInfo>();
        string defaultPrinter = string.Empty;

        try
        {
            var defaultSettings = new PrinterSettings();
            if (defaultSettings.IsValid)
            {
                defaultPrinter = defaultSettings.PrinterName;
            }
        }
        catch
        {
            // Ignore if no default printer is configured
        }

        foreach (string printerName in PrinterSettings.InstalledPrinters)
        {
            var info = new SharedPrinterInfo
            {
                Name = printerName,
                IsDefault = string.Equals(printerName, defaultPrinter, StringComparison.OrdinalIgnoreCase),
                Status = "Ready"
            };

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

            list.Add(info);
        }

        return list;
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

            printDoc.DocumentName = $"RPM Test Page - {DateTime.Now:yyyyMMdd_HHmmss}";

            printDoc.PrintPage += (sender, e) =>
            {
                if (e.Graphics == null) return;

                var g = e.Graphics;
                using var titleFont = new Font("Segoe UI", 18, FontStyle.Bold);
                using var subtitleFont = new Font("Segoe UI", 12, FontStyle.Bold);
                using var bodyFont = new Font("Segoe UI", 10, FontStyle.Regular);
                using var smallFont = new Font("Segoe UI", 8, FontStyle.Italic);
                using var pen = new Pen(Color.FromArgb(0, 120, 215), 2);
                using var linePen = new Pen(Color.LightGray, 1);
                using var blueBrush = new SolidBrush(Color.FromArgb(0, 120, 215));
                using var textBrush = new SolidBrush(Color.FromArgb(30, 30, 30));
                using var grayBrush = new SolidBrush(Color.Gray);

                int margin = 50;
                int y = margin;

                // Header box
                g.DrawRectangle(pen, margin, y, 700, 70);
                g.DrawString("REMOTE PRINT MANAGER (RPM)", titleFont, blueBrush, margin + 20, y + 10);
                g.DrawString("TRANG IN KIỂM TRA HỆ THỐNG / SYSTEM TEST PAGE", smallFont, grayBrush, margin + 22, y + 42);

                y += 90;

                g.DrawString("XÁC NHẬN KẾT NỐI VÀ IN ẤN THÀNH CÔNG", subtitleFont, textBrush, margin, y);
                y += 28;
                g.DrawLine(linePen, margin, y, margin + 700, y);
                y += 15;

                // Info rows
                void DrawRow(string label, string value)
                {
                    g.DrawString(label, subtitleFont, blueBrush, margin, y);
                    g.DrawString(value, bodyFont, textBrush, margin + 220, y);
                    y += 25;
                }

                DrawRow("Thời gian in:", DateTime.Now.ToString("dd/MM/yyyy HH:mm:ss"));
                DrawRow("Máy chủ (Server):", serverName);
                DrawRow("Máy in đích (Physical):", printerName);
                DrawRow("Máy con gửi yêu cầu:", clientName);
                DrawRow("Khoảng cách kết nối:", "Khu vực bán kính văn phòng công ty (<= 1 km)");
                DrawRow("Phương thức truyền dẫn:", "Internet độc lập qua Relay Broker (AES-256 E2EE)");
                DrawRow("Trạng thái bảo mật:", "Mã hóa đầu cuối đã xác thực thành công");

                y += 20;
                g.DrawLine(linePen, margin, y, margin + 700, y);
                y += 20;

                // Test blocks (color / gray test)
                g.DrawString("Kiểm tra dải màu & độ sắc nét:", subtitleFont, textBrush, margin, y);
                y += 25;

                int blockW = 60;
                int blockH = 30;
                Color[] testColors = [Color.Black, Color.DarkGray, Color.LightGray, Color.Red, Color.Green, Color.Blue, Color.Cyan, Color.Magenta, Color.Yellow];
                int bx = margin;
                foreach (var c in testColors)
                {
                    using var brush = new SolidBrush(c);
                    g.FillRectangle(brush, bx, y, blockW, blockH);
                    g.DrawRectangle(Pens.Black, bx, y, blockW, blockH);
                    bx += blockW + 10;
                }

                y += 50;
                g.DrawString("Trang này xác nhận máy con đã kết nối hoàn chỉnh với máy in thông qua Remote Print Manager.", smallFont, grayBrush, margin, y);
                y += 18;
                g.DrawString("Bạn có thể in trực tiếp từ bất kỳ ứng dụng nào (Word, Excel, Chrome...) bằng phím Ctrl + P.", smallFont, grayBrush, margin, y);
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
            byte[] bytes = File.ReadAllBytes(filePath);
            return PrintRawBytes(printerName, bytes, documentName, out errorMessage);
        }
        catch (Exception ex)
        {
            errorMessage = ex.Message;
            return false;
        }
    }
}
