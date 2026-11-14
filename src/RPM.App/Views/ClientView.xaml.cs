using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Media;
using RPM.App.Services;
using RPM.Core.Config;
using RPM.Core.Models;
using RPM.Printing;

namespace RPM.App.Views;

public partial class ClientView : UserControl
{
    private readonly ClientService _clientService;
    private readonly AppSettings _settings;
    private readonly ObservableCollection<string> _logs = new();

    public event EventHandler? OnSwitchToServerModeRequested;
    public event EventHandler? OnMinimizeRequested;

    public ClientView(ClientService clientService, AppSettings settings)
    {
        InitializeComponent();
        _clientService = clientService;
        _settings = settings;

        DgClientHistory.ItemsSource = _clientService.PrintHistory;
        LstClientLog.ItemsSource = _logs;

        _clientService.OnLog += ClientService_OnLog;
        _clientService.OnStateChanged += (s, e) => Dispatcher.Invoke(UpdateUI);

        Loaded += ClientView_Loaded;
    }

    private void ClientView_Loaded(object sender, RoutedEventArgs e)
    {
        TxtInputPairingCode.Text = _settings.PairingCode;
        TxtClientRelayUrl.Text = _settings.RelayServerUrl;
        TxtClientDeviceName.Text = _settings.DeviceName;
        TxtClientIppPort.Text = _settings.LocalIppPort.ToString();
        ChkClientAutoStart.IsChecked = _settings.AutoStartWithWindows;
        ChkClientMinimizeTray.IsChecked = _settings.MinimizeToSystemTray;
        ChkClientNotifications.IsChecked = _settings.ShowDesktopNotifications;

        UpdateUI();

        ClientUpdateControl.Initialize(_settings);
        ClientUpdateControl.HasActivePrintJobsCheck = () => 
            _clientService.PrintHistory.Any(j => j.Status == PrintJobStatus.Printing || j.Status == PrintJobStatus.Pending);
    }

    private void ClientService_OnLog(object? sender, string message)
    {
        Dispatcher.Invoke(() =>
        {
            string logLine = $"[{DateTime.Now:HH:mm:ss}] {message}";
            _logs.Insert(0, logLine);
            if (_logs.Count > 200) _logs.RemoveAt(_logs.Count - 1);
        });
    }

    private void UpdateUI()
    {
        TxtClientStatus.Text = _clientService.ConnectionStatus;
        TxtApprovalStatus.Text = _clientService.ConnectionStatus;

        if (_clientService.IsApproved)
        {
            DotClientStatus.Fill = (SolidColorBrush)Application.Current.Resources["SuccessBrush"];
            TxtClientStatus.Foreground = (SolidColorBrush)Application.Current.Resources["SuccessBrush"];
            ClientStatusIndicator.Background = new SolidColorBrush(Color.FromArgb(255, 223, 246, 221));
        }
        else
        {
            DotClientStatus.Fill = (SolidColorBrush)Application.Current.Resources["WarningBrush"];
            TxtClientStatus.Foreground = (SolidColorBrush)Application.Current.Resources["WarningBrush"];
            ClientStatusIndicator.Background = new SolidColorBrush(Color.FromArgb(255, 255, 244, 206));
        }

        if (_clientService.RemotePrinter != null)
        {
            TxtClientPrinterName.Text = _clientService.RemotePrinter.Name;
            TxtClientPrinterStatus.Text = _clientService.RemotePrinter.IsShared 
                ? "Trạng thái: SẴN SÀNG IN (Máy chủ đang mở)" 
                : "Trạng thái: Máy chủ tạm dừng chia sẻ";

            TxtRemotePrinterDetails.Text = 
                $"• Tên máy in vật lý: {_clientService.RemotePrinter.Name}\n" +
                $"• Driver: {_clientService.RemotePrinter.DriverName}\n" +
                $"• Hỗ trợ màu: {(_clientService.RemotePrinter.SupportsColor ? "Có" : "Không")}\n" +
                $"• Hỗ trợ in 2 mặt (Duplex): {(_clientService.RemotePrinter.SupportsDuplex ? "Có" : "Không")}\n" +
                $"• Cổng ảo cục bộ: http://127.0.0.1:{_settings.LocalIppPort}/printers/rpm";
        }
        else
        {
            TxtClientPrinterName.Text = "Chưa kết nối máy chủ";
            TxtClientPrinterStatus.Text = "Trạng thái: Vui lòng kết nối ghép đôi";
            TxtRemotePrinterDetails.Text = "Chưa nhận được thông tin từ máy chủ.";
        }

        // Virtual printer presence
        string expectedPrinterName = _clientService.RemotePrinter != null 
            ? $"{_clientService.RemotePrinter.Name} (Remote)" 
            : "Remote Office Printer";

        bool isInstalled = VirtualPrinterManager.IsVirtualPrinterInstalled(expectedPrinterName);
        if (isInstalled)
        {
            TxtClientSpoolerStatus.Text = $"Máy in ảo Windows: ĐÃ TÍCH HỢP ('{expectedPrinterName}')";
            TxtClientSpoolerStatus.Foreground = (SolidColorBrush)Application.Current.Resources["SuccessBrush"];
            TxtVirtualInstallResult.Text = $"✓ Máy in ảo '{expectedPrinterName}' đã sẵn sàng. Bạn có thể nhấn Ctrl + P trong mọi ứng dụng để in!";
            TxtVirtualInstallResult.Foreground = (SolidColorBrush)Application.Current.Resources["SuccessBrush"];
        }
        else
        {
            TxtClientSpoolerStatus.Text = "Máy in ảo Windows: Chưa cài đặt (Bấm tab 'Máy in của tôi' để cài)";
            TxtClientSpoolerStatus.Foreground = (SolidColorBrush)Application.Current.Resources["WarningBrush"];
            TxtVirtualInstallResult.Text = "Chưa cài đặt máy in ảo vào Windows.";
            TxtVirtualInstallResult.Foreground = (SolidColorBrush)Application.Current.Resources["TextMutedBrush"];
        }

        TxtClientJobsCount.Text = $"{_clientService.PrintHistory.Count} Tài liệu";
        if (_clientService.PrintHistory.Count > 0)
        {
            var last = _clientService.PrintHistory[0];
            TxtClientLastPrintTime.Text = $"Lệnh in gần nhất: {last.DocumentName} ({last.Status})";
        }
    }

    private async void BtnConnectServer_Click(object sender, RoutedEventArgs e)
    {
        string code = TxtInputPairingCode.Text.Trim();
        if (string.IsNullOrWhiteSpace(code))
        {
            MessageBox.Show("Vui lòng nhập Mã Ghép Đôi gồm 6 ký tự do máy chủ cung cấp.", "Cảnh báo", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        _settings.PairingCode = code;
        _settings.RelayServerUrl = TxtClientRelayUrl.Text.Trim();
        ConfigManager.Save(_settings);

        TxtApprovalStatus.Text = "Đang gửi yêu cầu ghép đôi...";
        await _clientService.ConnectToServerAsync(code);
    }

    private async void BtnInstallVirtualPrinter_Click(object sender, RoutedEventArgs e)
    {
        string expectedPrinterName = _clientService.RemotePrinter != null 
            ? $"{_clientService.RemotePrinter.Name} (Remote)" 
            : "Remote Office Printer";

        TxtVirtualInstallResult.Text = "Đang đăng ký máy in ảo vào Windows, vui lòng đợi...";
        TxtVirtualInstallResult.Foreground = (SolidColorBrush)Application.Current.Resources["PrimaryBrush"];

        var (success, msg) = await VirtualPrinterManager.InstallVirtualPrinterAsync(expectedPrinterName, _settings.LocalIppPort);
        TxtVirtualInstallResult.Text = msg;
        TxtVirtualInstallResult.Foreground = success 
            ? (SolidColorBrush)Application.Current.Resources["SuccessBrush"] 
            : (SolidColorBrush)Application.Current.Resources["DangerBrush"];

        UpdateUI();
        if (success)
        {
            MessageBox.Show($"Cài đặt thành công máy in '{expectedPrinterName}' vào Windows!\nBây giờ bạn có thể mở Word, Excel, Chrome và bấm Ctrl + P để in.", "Thành công", MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }

    private async void BtnRemoveVirtualPrinter_Click(object sender, RoutedEventArgs e)
    {
        string expectedPrinterName = _clientService.RemotePrinter != null 
            ? $"{_clientService.RemotePrinter.Name} (Remote)" 
            : "Remote Office Printer";

        bool ok = await VirtualPrinterManager.RemoveVirtualPrinterAsync(expectedPrinterName);
        TxtVirtualInstallResult.Text = ok ? "Đã gỡ bỏ máy in ảo khỏi Windows." : "Không thể gỡ bỏ máy in.";
        UpdateUI();
    }

    private async void BtnTestPrint_Click(object sender, RoutedEventArgs e)
    {
        if (!_clientService.IsApproved)
        {
            MessageBox.Show("Máy con chưa được máy chủ phê duyệt kết nối. Vui lòng kết nối và nhờ máy chủ bấm Phê duyệt trước.", "Chưa được duyệt", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        bool ok = await _clientService.SendTestPrintAsync();
        if (ok)
        {
            MessageBox.Show("Đã gửi lệnh in trang kiểm tra tới Máy Chủ!\nTài liệu đang được chuyển tới máy in USB.", "Gửi thành công", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        else
        {
            MessageBox.Show("Không thể gửi lệnh in kiểm tra. Vui lòng kiểm tra lại kết nối mạng.", "Lỗi gửi", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void BtnClearClientHistory_Click(object sender, RoutedEventArgs e)
    {
        _clientService.PrintHistory.Clear();
        _settings.PrintHistory.Clear();
        ConfigManager.Save(_settings);
        UpdateUI();
    }

    private void BtnSaveClientSettings_Click(object sender, RoutedEventArgs e)
    {
        _settings.DeviceName = TxtClientDeviceName.Text.Trim();
        if (int.TryParse(TxtClientIppPort.Text.Trim(), out int port))
        {
            _settings.LocalIppPort = port;
        }
        _settings.AutoStartWithWindows = ChkClientAutoStart.IsChecked ?? true;
        _settings.MinimizeToSystemTray = ChkClientMinimizeTray.IsChecked ?? true;
        _settings.ShowDesktopNotifications = ChkClientNotifications.IsChecked ?? true;

        ConfigManager.Save(_settings);
        MessageBox.Show("Cài đặt máy con đã được lưu thành công!", "Thông báo", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void BtnSwitchToServer_Click(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show("Bạn có muốn chuyển máy tính này sang Chế Độ Máy Chủ không?", "Chuyển chế độ", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
        {
            _settings.Role = AppRole.Server;
            ConfigManager.Save(_settings);
            OnSwitchToServerModeRequested?.Invoke(this, EventArgs.Empty);
        }
    }

    private void BtnMinimize_Click(object sender, RoutedEventArgs e)
    {
        OnMinimizeRequested?.Invoke(this, EventArgs.Empty);
    }

    private void BtnOpenDiagnostics_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new CompatibilityDialog(_settings)
        {
            Owner = Window.GetWindow(this)
        };
        dlg.ShowDialog();
    }
}
