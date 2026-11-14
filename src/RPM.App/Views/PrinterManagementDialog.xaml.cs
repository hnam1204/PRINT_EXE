using System.Collections.ObjectModel;
using System.Windows;
using RPM.App.Services;
using RPM.Core.Models;

namespace RPM.App.Views;

public partial class PrinterManagementDialog : Window
{
    private readonly ServerService _serverService;
    private readonly ObservableCollection<SharedPrinterInfo> _printers = new();

    public PrinterManagementDialog(ServerService serverService)
    {
        _serverService = serverService;
        InitializeComponent();

        DgPrinters.ItemsSource = _printers;
        Loaded += (s, e) => RefreshPrinters();
    }

    private void RefreshPrinters()
    {
        _serverService.LoadPrinters();
        _printers.Clear();
        foreach (var p in _serverService.SharedPrinters)
        {
            _printers.Add(p);
        }
    }

    private void BtnRescan_Click(object sender, RoutedEventArgs e)
    {
        RefreshPrinters();
        MessageBox.Show($"Đã quét thấy {_printers.Count} máy in trên hệ thống Windows.", "Thông báo", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void BtnToggleShare_Click(object sender, RoutedEventArgs e)
    {
        if (DgPrinters.SelectedItem is SharedPrinterInfo selected)
        {
            _serverService.TogglePrinterSharing(selected.Name);
            RefreshPrinters();
        }
        else
        {
            MessageBox.Show("Vui lòng chọn một máy in từ danh sách để bật/tắt chia sẻ.", "Thông báo", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void BtnSetDefault_Click(object sender, RoutedEventArgs e)
    {
        if (DgPrinters.SelectedItem is SharedPrinterInfo selected)
        {
            _serverService.SetDefaultPrinter(selected.Name);
            RefreshPrinters();
            MessageBox.Show($"Đã đặt '{selected.Name}' làm máy in chính để chia sẻ qua mạng!", "Thành công", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        else
        {
            MessageBox.Show("Vui lòng chọn một máy in để đặt làm máy in chính.", "Thông báo", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void BtnPrintTest_Click(object sender, RoutedEventArgs e)
    {
        if (DgPrinters.SelectedItem is SharedPrinterInfo selected)
        {
            bool ok = _serverService.PrinterService.PrintTestPage(selected.Name, "Máy Chủ Cục Bộ", Environment.MachineName, out string err);
            if (ok)
            {
                MessageBox.Show($"Đã gửi trang in kiểm tra tới '{selected.Name}'. Vui lòng kiểm tra khay giấy máy in!", "Thành công", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            else
            {
                MessageBox.Show($"In thử thất bại: {err}", "Lỗi in", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
        else
        {
            MessageBox.Show("Vui lòng chọn máy in để in trang kiểm tra.", "Thông báo", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void BtnClose_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }
}
