using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using RPM.App.Services;
using RPM.Core.Config;
using RPM.Core.Models;
using RPM.Printing;

namespace RPM.App.Views;

public partial class MainDashboardView : UserControl
{
    private readonly AppSettings _settings;
    private readonly ServerService? _serverService;
    private readonly ClientService? _clientService;
    private readonly ObservableCollection<PrintJob> _recentJobs = new();

    public event EventHandler? OnMinimizeRequested;
    public event EventHandler? OnRoleChangeRequested;

    public MainDashboardView(AppSettings settings, ServerService? serverService, ClientService? clientService)
    {
        _settings = settings;
        _serverService = serverService;
        _clientService = clientService;

        InitializeComponent();

        IcRecentJobs.ItemsSource = _recentJobs;

        if (_serverService != null)
        {
            _serverService.OnStatusChanged += (s, e) => Dispatcher.Invoke(UpdateServerUI);
            _serverService.PrintJobs.CollectionChanged += (s, e) => Dispatcher.Invoke(RefreshRecentJobs);
        }

        if (_clientService != null)
        {
            _clientService.OnStateChanged += (s, e) => Dispatcher.Invoke(UpdateClientUI);
            _clientService.PrintHistory.CollectionChanged += (s, e) => Dispatcher.Invoke(RefreshRecentJobs);
        }

        Loaded += MainDashboardView_Loaded;
    }

    private void MainDashboardView_Loaded(object sender, RoutedEventArgs e)
    {
        if (_settings.Role == AppRole.Server)
        {
            PanelServerMode.Visibility = Visibility.Visible;
            PanelClientMode.Visibility = Visibility.Collapsed;
            ToolbarServer.Visibility = Visibility.Visible;
            ToolbarClient.Visibility = Visibility.Collapsed;

            TxtRoleSubtitle.Text = "MÁY CHỦ IN ẤN (SERVER)";
            TxtCardBTitle.Text = "THÔNG TIN MÁY CHỦ & KẾT NỐI";
            TxtBadgeRole.Text = "Máy Chủ";
            TxtServerHostName.Text = _settings.DeviceName;
            TxtServerPairingCode.Text = _settings.PairingCode;

            UpdateServerUI();
        }
        else
        {
            PanelServerMode.Visibility = Visibility.Collapsed;
            PanelClientMode.Visibility = Visibility.Visible;
            ToolbarServer.Visibility = Visibility.Collapsed;
            ToolbarClient.Visibility = Visibility.Visible;

            TxtRoleSubtitle.Text = "MÁY CON IN TỪ XA (CLIENT)";
            TxtCardBTitle.Text = "KẾT NỐI TỚI MÁY CHỦ";
            TxtBadgeRole.Text = "Máy Con";
            TxtClientCodeInput.Text = _settings.PairingCode;

            UpdateClientUI();
        }

        RefreshRecentJobs();
    }

    private void UpdateServerUI()
    {
        if (_serverService == null) return;

        // Header status pill
        bool isOnline = _serverService.Relay.IsConnected;
        TxtHeaderStatus.Text = isOnline ? "Máy chủ đang hoạt động (Online)" : "Máy chủ nội bộ (Sẵn sàng)";
        DotStatus.Fill = (SolidColorBrush)Application.Current.Resources["SuccessBrush"];
        PillStatus.Background = (SolidColorBrush)Application.Current.Resources["SuccessLightBrush"];
        TxtHeaderStatus.Foreground = (SolidColorBrush)Application.Current.Resources["SuccessBrush"];

        // Server connection status
        TxtServerConnectionStatus.Text = isOnline ? "Đã kết nối Relay Broker (WSS)" : "Chạy máy chủ nội bộ trên cổng 9090";

        // Shared printer
        int sharedCount = _serverService.SharedPrinters.Count(p => p.IsShared);
        if (_serverService.ActiveSharedPrinter != null)
        {
            TxtSharedPrinterName.Text = _serverService.ActiveSharedPrinter.Name;
            TxtSharedPrinterPort.Text = $"Cổng: {_serverService.ActiveSharedPrinter.PortName} • Đang chia sẻ {sharedCount} máy in";
            TxtActivePrinterSummary.Text = $"Máy in chính: {_serverService.ActiveSharedPrinter.Name}";
        }
        else
        {
            TxtSharedPrinterName.Text = "Chưa chọn máy in";
            TxtSharedPrinterPort.Text = "Nhấn 'Đổi máy in...' để chọn máy in chia sẻ";
            TxtActivePrinterSummary.Text = "Máy in: Chưa chọn máy in";
        }

        // Clients stats
        int onlineClients = _serverService.ConnectedClients.Count(c => c.IsOnline && c.IsApproved);
        int pendingClients = _serverService.ConnectedClients.Count(c => !c.IsApproved && !c.IsBlocked);
        TxtServerClientsSummary.Text = $"{_serverService.ConnectedClients.Count} máy con ({onlineClients} trực tuyến • {pendingClients} chờ duyệt)";

        RefreshRecentJobs();
    }

    private void UpdateClientUI()
    {
        if (_clientService == null) return;

        bool isApproved = _clientService.IsApproved;
        TxtHeaderStatus.Text = isApproved ? "Đã kết nối máy chủ (Sẵn sàng)" : _clientService.ConnectionStatus;

        if (isApproved)
        {
            DotStatus.Fill = (SolidColorBrush)Application.Current.Resources["SuccessBrush"];
            PillStatus.Background = (SolidColorBrush)Application.Current.Resources["SuccessLightBrush"];
            TxtHeaderStatus.Foreground = (SolidColorBrush)Application.Current.Resources["SuccessBrush"];

            TxtClientApprovalStatus.Text = "Đã phê duyệt";
            BorderApprovalStatus.Background = (SolidColorBrush)Application.Current.Resources["SuccessLightBrush"];
            TxtClientApprovalStatus.Foreground = (SolidColorBrush)Application.Current.Resources["SuccessBrush"];

            TxtClientConnectBtnText.Text = "Ngắt kết nối";
            BtnClientConnectToggle.Style = (Style)Application.Current.Resources["DestructiveBtn"];
        }
        else
        {
            DotStatus.Fill = (SolidColorBrush)Application.Current.Resources["WarningBrush"];
            PillStatus.Background = (SolidColorBrush)Application.Current.Resources["WarningLightBrush"];
            TxtHeaderStatus.Foreground = (SolidColorBrush)Application.Current.Resources["WarningBrush"];

            TxtClientApprovalStatus.Text = _clientService.ConnectionStatus;
            BorderApprovalStatus.Background = (SolidColorBrush)Application.Current.Resources["WarningLightBrush"];
            TxtClientApprovalStatus.Foreground = (SolidColorBrush)Application.Current.Resources["WarningBrush"];

            TxtClientConnectBtnText.Text = "Kết nối in ấn";
            BtnClientConnectToggle.Style = (Style)Application.Current.Resources["PrimaryBtn"];
        }

        if (_clientService.RemotePrinter != null)
        {
            TxtClientPrinterName.Text = _clientService.RemotePrinter.Name;
            TxtClientPrinterStatus.Text = $"Cổng: 127.0.0.1:{_settings.LocalIppPort} (RFC 8011 IPP)";
            TxtActivePrinterSummary.Text = $"Máy in từ xa: {_clientService.RemotePrinter.Name}";
        }
        else
        {
            TxtClientPrinterName.Text = "Chưa kết nối máy in";
            TxtClientPrinterStatus.Text = "Vui lòng kết nối tới máy chủ";
            TxtActivePrinterSummary.Text = "Máy in: Đang chờ kết nối";
        }

        RefreshRecentJobs();
    }

    private void RefreshRecentJobs()
    {
        _recentJobs.Clear();
        IEnumerable<PrintJob> source = _serverService != null 
            ? _serverService.PrintJobs 
            : (_clientService != null ? _clientService.PrintHistory : Enumerable.Empty<PrintJob>());

        var list = source.Reverse().Take(4).ToList();
        foreach (var job in list)
        {
            _recentJobs.Add(job);
        }

        PanelEmptyJobs.Visibility = _recentJobs.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        int pendingCount = source.Count(j => j.Status == PrintJobStatus.Pending || j.Status == PrintJobStatus.Printing);
        int completedCount = source.Count(j => j.Status == PrintJobStatus.Completed);
        TxtQueueQuickStats.Text = $"{pendingCount} chờ • {completedCount} hoàn tất";
    }

    // =========================================================
    // EVENT HANDLERS
    // =========================================================

    private void BtnCopyPairingCode_Click(object sender, RoutedEventArgs e)
    {
        Clipboard.SetText(_settings.PairingCode);
        MessageBox.Show($"Đã sao chép mã ghép đôi '{_settings.PairingCode}' vào bộ nhớ tạm!\nBạn hãy gửi mã này cho các máy con để kết nối.", "Sao chép thành công", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private async void BtnClientConnectToggle_Click(object sender, RoutedEventArgs e)
    {
        if (_clientService == null) return;

        if (_clientService.IsApproved)
        {
            if (MessageBox.Show("Bạn có muốn ngắt kết nối với máy chủ hiện tại?", "Xác nhận ngắt kết nối", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
            {
                await _clientService.DisconnectAsync();
                UpdateClientUI();
            }
        }
        else
        {
            string code = TxtClientCodeInput.Text.Trim();
            if (string.IsNullOrEmpty(code))
            {
                MessageBox.Show("Vui lòng nhập mã ghép đôi (ví dụ: RPM-8197).", "Chưa nhập mã", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            _settings.PairingCode = code;
            ConfigManager.Save(_settings);
            await _clientService.ConnectToServerAsync(code);
            UpdateClientUI();
        }
    }

    private async void BtnClientTestPrint_Click(object sender, RoutedEventArgs e)
    {
        if (_clientService == null) return;
        if (!_clientService.IsApproved)
        {
            MessageBox.Show("Máy con chưa được máy chủ phê duyệt kết nối. Vui lòng kết nối và nhờ máy chủ bấm Phê duyệt trước.", "Chưa được duyệt", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        bool ok = await _clientService.SendTestPrintAsync();
        if (ok)
        {
            MessageBox.Show($"Đã gửi lệnh in trang kiểm tra thành công!", "In kiểm tra", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        else
        {
            MessageBox.Show("Không thể gửi lệnh in. Hãy chắc chắn rằng máy chủ đang mở và kết nối mạng ổn định.", "Thông báo", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private async void BtnClientInstallVirtualPrinter_Click(object sender, RoutedEventArgs e)
    {
        string expectedPrinterName = _clientService?.RemotePrinter != null 
            ? $"{_clientService.RemotePrinter.Name} (Remote)" 
            : "Remote Office Printer";

        var (success, msg) = await VirtualPrinterManager.InstallVirtualPrinterAsync(expectedPrinterName, _settings.LocalIppPort);
        if (success)
        {
            MessageBox.Show($"Đã cài đặt máy in ảo '{expectedPrinterName}' vào Windows thành công!\nBây giờ bạn có thể mở Word, Excel, Chrome và nhấn Ctrl + P để in.", "Cài đặt thành công", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        else
        {
            MessageBox.Show($"Không thể tự động cài máy in: {msg}\n\n* Lưu ý: Lệnh này đòi hỏi quyền Administrator của Windows. Vui lòng đóng phần mềm và mở lại bằng chuột phải -> 'Run as administrator'.", "Yêu cầu quyền Administrator", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void BtnOpenPrinterDialog_Click(object sender, RoutedEventArgs e)
    {
        if (_serverService != null)
        {
            var dialog = new PrinterManagementDialog(_serverService)
            {
                Owner = Window.GetWindow(this)
            };
            dialog.ShowDialog();
            UpdateServerUI();
        }
    }

    private void BtnOpenDeviceDialog_Click(object sender, RoutedEventArgs e)
    {
        if (_serverService != null)
        {
            var dialog = new DeviceManagementDialog(_serverService, _settings)
            {
                Owner = Window.GetWindow(this)
            };
            dialog.ShowDialog();
            UpdateServerUI();
        }
    }

    private void BtnOpenQueueDialog_Click(object sender, RoutedEventArgs e)
    {
        ObservableCollection<PrintJob> jobs = _serverService != null 
            ? _serverService.PrintJobs 
            : (_clientService != null ? _clientService.PrintHistory : new ObservableCollection<PrintJob>());

        var dialog = new PrintQueueDialog(jobs, () =>
        {
            if (_serverService != null) _serverService.ClearCompletedJobs();
        })
        {
            Owner = Window.GetWindow(this)
        };
        dialog.ShowDialog();
        RefreshRecentJobs();
    }

    private void BtnOpenDiagnostics_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new CompatibilityDialog(_settings)
        {
            Owner = Window.GetWindow(this)
        };
        dialog.ShowDialog();
    }

    private void BtnOpenSettings_Click(object sender, RoutedEventArgs e)
    {
        Func<bool>? activeCheck = _serverService != null 
            ? () => _serverService.PrintJobs.Any(j => j.Status == PrintJobStatus.Printing || j.Status == PrintJobStatus.Pending)
            : (_clientService != null ? () => _clientService.PrintHistory.Any(j => j.Status == PrintJobStatus.Printing || j.Status == PrintJobStatus.Pending) : null);

        var dialog = new SettingsDialog(_settings, activeCheck)
        {
            Owner = Window.GetWindow(this)
        };

        dialog.OnRoleChanged += (s, ev) =>
        {
            OnRoleChangeRequested?.Invoke(this, EventArgs.Empty);
        };

        dialog.ShowDialog();

        if (dialog.SettingsSaved)
        {
            if (_serverService != null) UpdateServerUI();
            if (_clientService != null) UpdateClientUI();
        }
    }

    private void BtnMinimize_Click(object sender, RoutedEventArgs e)
    {
        OnMinimizeRequested?.Invoke(this, EventArgs.Empty);
    }
}
