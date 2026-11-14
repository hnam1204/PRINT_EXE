using System.Windows;
using System.Windows.Media;
using RPM.Core.Config;
using RPM.Core.Diagnostics;
using RPM.Core.Models;
using RPM.Printing;

namespace RPM.App.Views;

public partial class CompatibilityDialog : Window
{
    private readonly AppSettings? _settings;
    private readonly PrinterService _printerService = new();

    public CompatibilityDialog(AppSettings? settings = null)
    {
        InitializeComponent();
        _settings = settings;
        Loaded += (s, e) => RunDiagnostics();
    }

    private void RunDiagnostics()
    {
        List<SharedPrinterInfo>? printers = null;
        try
        {
            printers = _printerService.GetInstalledPrinters();
        }
        catch { }

        var report = CompatibilityChecker.RunDiagnostics(_settings, printers);

        DgDiagnostics.ItemsSource = report.Items;
        TxtDeviceSpec.Text = $"{report.OperatingSystem} • {report.Architecture}";

        switch (report.OverallLevel)
        {
            case CompatibilityLevel.Compatible:
                BannerOverall.Background = new SolidColorBrush(Color.FromArgb(255, 232, 248, 238)); // #E8F8EE
                BannerOverall.BorderBrush = new SolidColorBrush(Color.FromArgb(255, 21, 184, 74));   // #15B84A
                TxtOverallTitle.Text = "● THIẾT BỊ HOÀN TOÀN TƯƠNG THÍCH (100% SẴN SÀNG)";
                TxtOverallTitle.Foreground = new SolidColorBrush(Color.FromArgb(255, 21, 184, 74));  // #15B84A
                TxtOverallMessage.Text = report.SummaryMessage;
                break;

            case CompatibilityLevel.NeedsSetup:
                BannerOverall.Background = new SolidColorBrush(Color.FromArgb(255, 254, 246, 230)); // #FEF6E6
                BannerOverall.BorderBrush = new SolidColorBrush(Color.FromArgb(255, 242, 169, 0));   // #F2A900
                TxtOverallTitle.Text = "▲ THIẾT BỊ CẦN THIẾT LẬP BỔ SUNG";
                TxtOverallTitle.Foreground = new SolidColorBrush(Color.FromArgb(255, 242, 169, 0));  // #F2A900
                TxtOverallMessage.Text = report.SummaryMessage;
                break;

            case CompatibilityLevel.Incompatible:
                BannerOverall.Background = new SolidColorBrush(Color.FromArgb(255, 250, 236, 235)); // #FAECEB
                BannerOverall.BorderBrush = new SolidColorBrush(Color.FromArgb(255, 197, 61, 50));   // #C53D32
                TxtOverallTitle.Text = "✕ THIẾT BỊ KHÔNG TƯƠNG THÍCH";
                TxtOverallTitle.Foreground = new SolidColorBrush(Color.FromArgb(255, 197, 61, 50));  // #C53D32
                TxtOverallMessage.Text = report.SummaryMessage;
                break;
        }
    }

    private void BtnRescan_Click(object sender, RoutedEventArgs e)
    {
        RunDiagnostics();
    }

    private void BtnRestartSpooler_Click(object sender, RoutedEventArgs e)
    {
        bool restarted = CompatibilityChecker.RestartSpoolerService(out string err);
        if (restarted)
        {
            MessageBox.Show("Đã khởi động lại dịch vụ Windows Print Spooler thành công!", "Thành công", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        else
        {
            MessageBox.Show($"Không thể tự động khởi động dịch vụ Spooler: {err}\nVui lòng chạy ứng dụng với quyền 'Run as administrator'.", "Thông báo", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        RunDiagnostics();
    }

    private void BtnClose_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }
}

