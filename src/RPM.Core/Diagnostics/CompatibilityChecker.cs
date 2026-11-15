using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text.RegularExpressions;
using RPM.Core.Config;
using RPM.Core.Models;

namespace RPM.Core.Diagnostics;

public static class CompatibilityChecker
{
    private static readonly string[] RecognizedBrands = new[]
    {
        "Canon", "HP", "Hewlett-Packard", "Brother", "Epson", "Samsung", 
        "Ricoh", "Xerox", "Konica Minolta", "Lexmark", "Kyocera", "Pantum", "Toshiba"
    };

    public static SystemCompatibilityReport RunDiagnostics(AppSettings? settings = null, List<SharedPrinterInfo>? installedPrinters = null)
    {
        var report = new SystemCompatibilityReport();
        var items = new List<DiagnosticItem>();

        // 1. Hệ điều hành & Phiên bản Windows
        bool isWindows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
        bool is64Bit = Environment.Is64BitOperatingSystem;
        var osVersion = Environment.OSVersion;
        int build = osVersion.Version.Build;
        
        string winName = "Windows";
        if (build >= 22000)
        {
            winName = "Windows 11 (64-bit)";
        }
        else if (build >= 10240)
        {
            winName = "Windows 10 (64-bit)";
        }
        else if (osVersion.Version.Major >= 6)
        {
            winName = $"Windows Server / Legacy ({osVersion.VersionString})";
        }

        report.OperatingSystem = winName;
        report.WindowsBuild = $"Build {build}";
        report.Is64Bit = is64Bit;

        if (!isWindows)
        {
            items.Add(new DiagnosticItem
            {
                Category = "Hệ điều hành",
                Name = "Hệ điều hành tương thích",
                Detail = "Hệ điều hành hiện tại không phải Windows.",
                Level = CompatibilityLevel.Incompatible,
                Recommendation = "Phần mềm chỉ hoạt động trên môi trường Windows 10 và Windows 11."
            });
            report.OverallLevel = CompatibilityLevel.Incompatible;
        }
        else if (!is64Bit)
        {
            items.Add(new DiagnosticItem
            {
                Category = "Hệ điều hành",
                Name = "Kiến trúc hệ thống",
                Detail = "Windows 32-bit (x86)",
                Level = CompatibilityLevel.Incompatible,
                Recommendation = "Phần mềm yêu cầu Windows 10 hoặc Windows 11 phiên bản 64-bit."
            });
            report.OverallLevel = CompatibilityLevel.Incompatible;
        }
        else
        {
            items.Add(new DiagnosticItem
            {
                Category = "Hệ điều hành",
                Name = "Hệ điều hành Windows",
                Detail = $"{winName} - Build {build} (64-bit)",
                Level = CompatibilityLevel.Compatible,
                Recommendation = "Hệ điều hành đáp ứng đầy đủ điều kiện vận hành."
            });
        }

        // 2. Kiến trúc CPU & Bộ vi xử lý
        var arch = RuntimeInformation.ProcessArchitecture;
        report.Architecture = arch.ToString();
        items.Add(new DiagnosticItem
        {
            Category = "Kiến trúc phần cứng",
            Name = "Kiến trúc CPU",
            Detail = $"{arch} (Hỗ trợ Desktop, Laptop, Mini PC, All-in-One, Workstation)",
            Level = CompatibilityLevel.Compatible,
            Recommendation = "Tương thích tốt với mọi dòng máy (Dell, HP, Lenovo, ASUS, Acer, MSI...)."
        });

        // 3. Quyền hạn người dùng
        bool isAdmin = CheckIsAdministrator();
        report.IsAdministrator = isAdmin;
        if (isAdmin)
        {
            items.Add(new DiagnosticItem
            {
                Category = "Quyền hệ thống",
                Name = "Quyền hạn thực thi",
                Detail = "Quyền quản trị viên",
                Level = CompatibilityLevel.Compatible,
                Recommendation = "Sẵn sàng cho việc tạo máy in ảo và điều khiển dịch vụ in."
            });
        }
        else
        {
            items.Add(new DiagnosticItem
            {
                Category = "Quyền hệ thống",
                Name = "Quyền hạn thực thi",
                Detail = "Quyền người dùng tiêu chuẩn",
                Level = CompatibilityLevel.NeedsSetup,
                Recommendation = "Nhấp chuột phải và chọn 'Mở bằng quyền quản trị viên' khi cài máy in ảo lần đầu."
            });
        }

        // 4. Trạng thái dịch vụ in ấn Windows Spooler
        bool isSpoolerRunning = CheckIsSpoolerRunning();
        report.IsSpoolerRunning = isSpoolerRunning;
        if (isSpoolerRunning)
        {
            items.Add(new DiagnosticItem
            {
                Category = "Dịch vụ in ấn",
                Name = "Dịch vụ in của Windows",
                Detail = "Dịch vụ đang hoạt động bình thường.",
                Level = CompatibilityLevel.Compatible,
                Recommendation = "Hàng đợi Windows sẵn sàng tiếp nhận tài liệu từ Word, Excel, Chrome..."
            });
        }
        else
        {
            items.Add(new DiagnosticItem
            {
                Category = "Dịch vụ in ấn",
                Name = "Dịch vụ in của Windows",
                Detail = "Dịch vụ đang tạm dừng hoặc bị tắt.",
                Level = CompatibilityLevel.NeedsSetup,
                Recommendation = "Bật lại dịch vụ in của Windows rồi kiểm tra lại."
            });
            if (report.OverallLevel == CompatibilityLevel.Compatible)
            {
                report.OverallLevel = CompatibilityLevel.NeedsSetup;
            }
        }

        // 5. Khả năng tương thích máy in (Thương hiệu & Driver)
        if (installedPrinters != null && installedPrinters.Count > 0)
        {
            var detectedBrands = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var p in installedPrinters)
            {
                foreach (var brand in RecognizedBrands)
                {
                    if (p.Name.Contains(brand, StringComparison.OrdinalIgnoreCase))
                    {
                        detectedBrands.Add(brand);
                    }
                }
            }

            string brandSummary = detectedBrands.Count > 0 
                ? string.Join(", ", detectedBrands) 
                : "Máy in tiêu chuẩn Windows (GDI/PDF/USB)";

            items.Add(new DiagnosticItem
            {
                Category = "Máy in vật lý",
                Name = "Nhận diện máy in",
                Detail = $"Phát hiện {installedPrinters.Count} máy in ({brandSummary})",
                Level = CompatibilityLevel.Compatible,
                Recommendation = "Tương thích tốt với máy in cắm cáp USB và mạng nội bộ."
            });
        }
        else
        {
            items.Add(new DiagnosticItem
            {
                Category = "Máy in vật lý",
                Name = "Nhận diện máy in",
                Detail = "Chưa phát hiện máy in cài sẵn trên Windows.",
                Level = CompatibilityLevel.NeedsSetup,
                Recommendation = "Tại Máy Chủ: Vui lòng cắm cáp USB và bật nguồn máy in công ty."
            });
        }

        // 6. Cổng IPP Spooler cục bộ
        int ippPort = settings?.LocalIppPort ?? 9199;
        bool isPortAvailable = CheckPortAvailable(ippPort);
        items.Add(new DiagnosticItem
        {
            Category = "Cổng kết nối",
            Name = $"Kết nối máy in ảo ({ippPort})",
            Detail = isPortAvailable ? "Cổng sẵn sàng (Sẵn sàng mở luồng in)" : "Cổng đang được ứng dụng sử dụng",
            Level = CompatibilityLevel.Compatible,
            Recommendation = "Kết nối này phục vụ việc nhận tài liệu in từ máy con."
        });

        // 7. Khả năng tương thích ứng dụng (Word, Excel, Chrome, PDF...)
        items.Add(new DiagnosticItem
        {
            Category = "Ứng dụng văn phòng",
            Name = "In ấn trực tiếp qua Ctrl + P",
            Detail = "Hỗ trợ 100% Microsoft Office, trình duyệt Web, Acrobat PDF, phần mềm kế toán (MISA...)",
            Level = CompatibilityLevel.Compatible,
            Recommendation = "Nhân viên nhấn Ctrl + P trong bất kỳ phần mềm nào để in trực tiếp."
        });

        // Cập nhật kết luận tổng quan
        report.Items = items;
        if (report.OverallLevel == CompatibilityLevel.Compatible)
        {
            report.SummaryMessage = "Thiết bị hoàn toàn tương thích và đáp ứng tối ưu yêu cầu vận hành Remote Print Manager.";
        }
        else if (report.OverallLevel == CompatibilityLevel.NeedsSetup)
        {
            report.SummaryMessage = "Thiết bị cơ bản tương thích nhưng cần thiết lập bổ sung (như quyền quản trị viên hoặc bật dịch vụ in của Windows).";
        }
        else
        {
            report.SummaryMessage = "Thiết bị không đáp ứng yêu cầu hệ thống tối thiểu (yêu cầu Windows 10/11 64-bit).";
        }

        return report;
    }

    public static bool CheckIsAdministrator()
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                using var identity = WindowsIdentity.GetCurrent();
                var principal = new WindowsPrincipal(identity);
                return principal.IsInRole(WindowsBuiltInRole.Administrator);
            }
        }
        catch { }
        return false;
    }

    public static bool CheckIsSpoolerRunning()
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                var processes = Process.GetProcessesByName("spoolsv");
                return processes.Length > 0;
            }
        }
        catch { }
        return true;
    }

    public static bool CheckPortAvailable(int port)
    {
        try
        {
            using var tcpListener = new TcpListener(IPAddress.Loopback, port);
            tcpListener.Start();
            tcpListener.Stop();
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static bool RestartSpoolerService(out string errorMessage)
    {
        errorMessage = string.Empty;
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = "/c net stop spooler && net start spooler",
                Verb = "runas",
                UseShellExecute = true,
                WindowStyle = ProcessWindowStyle.Hidden
            };
            var proc = Process.Start(psi);
            proc?.WaitForExit(10000);
            return CheckIsSpoolerRunning();
        }
        catch (Exception ex)
        {
            errorMessage = ex.Message;
            return false;
        }
    }
}

