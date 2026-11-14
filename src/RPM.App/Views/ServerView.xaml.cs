using System.Collections.ObjectModel;
using System.Windows;
using RPM.App.Services;
using RPM.Core.Config;
using RPM.Core.Models;
using RPM.Printing;

namespace RPM.App.Views;

public partial class ServerView : UserControl
{
    private readonly ServerService _serverService;
    private readonly AppSettings _settings;
    private readonly ObservableCollection<SharedPrinterInfo> _printers = new();
    private readonly ObservableCollection<string> _logs = new();

    public event EventHandler? OnSwitchToClientModeRequested;
    public event EventHandler? OnMinimizeRequested;

    public ServerView(ServerService serverService, AppSettings settings)
    {
        InitializeComponent();
        _serverService = serverService;
        _settings = settings;

        DgPrinters.ItemsSource = _printers;
        DgClients.ItemsSource = _serverService.ConnectedClients;
        DgQueue.ItemsSource = _serverService.PrintJobs;
        LstActivityLog.ItemsSource = _logs;

        _serverService.OnLog += ServerService_OnLog;
        _serverService.OnStatusChanged += (s, e) => Dispatcher.Invoke(UpdateDashboardUI);

        Loaded += ServerView_Loaded;
    }

    private void ServerView_Loaded(object sender, RoutedEventArgs e)
    {
        TxtPairingCode.Text = _settings.PairingCode;
        TxtSettingPairingCode.Text = _settings.PairingCode;
        TxtServerName.Text = _settings.DeviceName;
        TxtRelayUrl.Text = _settings.RelayServerUrl;
        ChkAutoStart.IsChecked = _settings.AutoStartWithWindows;
        ChkMinimizeTray.IsChecked = _settings.MinimizeToSystemTray;
        ChkNotifications.IsChecked = _settings.ShowDesktopNotifications;

        RefreshPrinters();
        UpdateDashboardUI();
    }

    private void ServerService_OnLog(object? sender, string message)
    {
        Dispatcher.Invoke(() =>
        {
            string logLine = $"[{DateTime.Now:HH:mm:ss}] {message}";
            _logs.Insert(0, logLine);
            if (_logs.Count > 200) _logs.RemoveAt(_logs.Count - 1);
        });
    }

    private void UpdateDashboardUI()
    {
        if (_serverService.ActiveSharedPrinter != null)
        {
            TxtOverviewPrinterName.Text = _serverService.ActiveSharedPrinter.Name;
            TxtOverviewPrinterStatus.Text = _serverService.ActiveSharedPrinter.IsShared 
                ? "Trạng thái: Đang chia sẻ trực tuyến" 
                : "Trạng thái: Tạm dừng chia sẻ";
        }
        else
        {
            TxtOverviewPrinterName.Text = "Chưa chọn máy in";
            TxtOverviewPrinterStatus.Text = "Trạng thái: Đang chờ thiết lập trong tab Máy In";
        }

        int onlineClients = _serverService.ConnectedClients.Count(c => c.IsOnline && c.IsApproved);
        int pendingClients = _serverService.ConnectedClients.Count(c => !c.IsApproved && !c.IsBlocked);
        TxtOverviewClientsCount.Text = $"{onlineClients} / 3 Trực tuyến";
        TxtOverviewPendingClients.Text = $"{pendingClients} máy con đang chờ phê duyệt";

        int completedJobs = _serverService.PrintJobs.Count(j => j.Status == PrintJobStatus.Completed);
        int pendingJobs = _serverService.PrintJobs.Count(j => j.Status == PrintJobStatus.Pending || j.Status == PrintJobStatus.Printing);
        TxtOverviewJobsCount.Text = $"{completedJobs} Hoàn tất";
        TxtOverviewQueueStatus.Text = $"Hàng đợi: {pendingJobs} đang chờ xử lý";

        TxtRelayStatus.Text = _serverService.Relay.IsConnected ? "Đã kết nối ổn định (WSS)" : "Đang kết nối lại...";
    }

    private void RefreshPrinters()
    {
        _printers.Clear();
        var installed = _serverService.PrinterService.GetInstalledPrinters();
        foreach (var p in installed)
        {
            if (_serverService.ActiveSharedPrinter != null && p.Name == _serverService.ActiveSharedPrinter.Name)
            {
                p.IsShared = _serverService.ActiveSharedPrinter.IsShared;
            }
            _printers.Add(p);
        }
    }

    private void BtnCopyPairingCode_Click(object sender, RoutedEventArgs e)
    {
        Clipboard.SetText(_settings.PairingCode);
        MessageBox.Show($"Đã sao chép mã ghép đôi '{_settings.PairingCode}' vào bộ nhớ tạm!\nBạn hãy gửi mã này cho 3 máy con.", "Sao chép thành công", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void BtnRescanPrinters_Click(object sender, RoutedEventArgs e)
    {
        RefreshPrinters();
        MessageBox.Show($"Đã quét thấy {_printers.Count} máy in trên máy tính.", "Quét máy in", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void BtnSetSharedPrinter_Click(object sender, RoutedEventArgs e)
    {
        if (DgPrinters.SelectedItem is SharedPrinterInfo selected)
        {
            _serverService.SetSharedPrinter(selected.Name);
            RefreshPrinters();
            UpdateDashboardUI();
            MessageBox.Show($"Đã chọn '{selected.Name}' làm máy in chia sẻ cho công ty!", "Thành công", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        else
        {
            MessageBox.Show("Vui lòng chọn một máy in từ danh sách trước.", "Chưa chọn máy in", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void BtnTestPrint_Click(object sender, RoutedEventArgs e)
    {
        string printerName = _serverService.ActiveSharedPrinter?.Name ?? string.Empty;
        if (DgPrinters.SelectedItem is SharedPrinterInfo sel)
        {
            printerName = sel.Name;
        }

        if (string.IsNullOrEmpty(printerName))
        {
            MessageBox.Show("Vui lòng chọn một máy in để in thử nghiệm.", "Cảnh báo", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        bool ok = _serverService.PrinterService.PrintTestPage(printerName, "Máy Chủ Cục Bộ", _settings.DeviceName, out string err);
        if (ok)
        {
            MessageBox.Show($"Đã gửi trang in kiểm tra tới '{printerName}'. Vui lòng kiểm tra khay giấy máy in!", "Thành công", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        else
        {
            MessageBox.Show($"In thử nghiệm thất bại: {err}", "Lỗi in", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void BtnPauseSharing_Click(object sender, RoutedEventArgs e)
    {
        _serverService.PauseSharing();
        RefreshPrinters();
        UpdateDashboardUI();
    }

    private async void BtnApproveClient_Click(object sender, RoutedEventArgs e)
    {
        if (DgClients.SelectedItem is DeviceInfo client)
        {
            await _serverService.ApproveClientAsync(client);
            DgClients.Items.Refresh();
            UpdateDashboardUI();
            MessageBox.Show($"Đã phê duyệt máy con '{client.DeviceName}'. Máy con có thể in tài liệu ngay bây giờ!", "Phê duyệt thành công", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        else
        {
            MessageBox.Show("Vui lòng chọn máy con muốn phê duyệt.", "Thông báo", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private async void BtnRevokeClient_Click(object sender, RoutedEventArgs e)
    {
        if (DgClients.SelectedItem is DeviceInfo client)
        {
            if (MessageBox.Show($"Bạn có chắc chắn muốn thu hồi quyền in của '{client.DeviceName}'?", "Xác nhận thu hồi", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
            {
                await _serverService.RevokeClientAsync(client);
                DgClients.Items.Refresh();
                UpdateDashboardUI();
            }
        }
    }

    private void BtnRefreshQueue_Click(object sender, RoutedEventArgs e)
    {
        DgQueue.Items.Refresh();
        UpdateDashboardUI();
    }

    private void BtnClearHistory_Click(object sender, RoutedEventArgs e)
    {
        var completed = _serverService.PrintJobs.Where(j => j.Status == PrintJobStatus.Completed || j.Status == PrintJobStatus.Failed).ToList();
        foreach (var job in completed)
        {
            _serverService.PrintJobs.Remove(job);
        }
        UpdateDashboardUI();
    }

    private void BtnClearLog_Click(object sender, RoutedEventArgs e)
    {
        _logs.Clear();
    }

    private void BtnRegenerateCode_Click(object sender, RoutedEventArgs e)
    {
        string newCode = Core.Security.RPMCrypto.GeneratePairingCode();
        TxtSettingPairingCode.Text = newCode;
    }

    private void BtnSaveSettings_Click(object sender, RoutedEventArgs e)
    {
        _settings.DeviceName = TxtServerName.Text.Trim();
        _settings.PairingCode = TxtSettingPairingCode.Text.Trim();
        _settings.RelayServerUrl = TxtRelayUrl.Text.Trim();
        _settings.AutoStartWithWindows = ChkAutoStart.IsChecked ?? true;
        _settings.MinimizeToSystemTray = ChkMinimizeTray.IsChecked ?? true;
        _settings.ShowDesktopNotifications = ChkNotifications.IsChecked ?? true;

        ConfigManager.Save(_settings);
        TxtPairingCode.Text = _settings.PairingCode;

        MessageBox.Show("Cài đặt máy chủ đã được lưu thành công!", "Thông báo", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void BtnSwitchToClient_Click(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show("Bạn có muốn chuyển máy tính này sang Chế Độ Máy Con không?", "Chuyển chế độ", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
        {
            _settings.Role = AppRole.Client;
            ConfigManager.Save(_settings);
            OnSwitchToClientModeRequested?.Invoke(this, EventArgs.Empty);
        }
    }

    private void BtnMinimize_Click(object sender, RoutedEventArgs e)
    {
        OnMinimizeRequested?.Invoke(this, EventArgs.Empty);
    }
}
