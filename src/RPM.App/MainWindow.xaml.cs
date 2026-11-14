using System.ComponentModel;
using System.Windows;
using System.Windows.Forms;
using RPM.App.Services;
using RPM.App.Views;
using RPM.Core.Config;
using RPM.Core.Models;

namespace RPM.App;

public partial class MainWindow : Window
{
    private AppSettings _settings;
    private TrayService? _trayService;
    private ServerService? _serverService;
    private ClientService? _clientService;
    private bool _isExplicitExit;

    public MainWindow()
    {
        InitializeComponent();
        _settings = ConfigManager.Load();

        Loaded += MainWindow_Loaded;
        Closing += MainWindow_Closing;
    }

    private void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        _trayService = new TrayService(this);
        NavigateToCurrentRole();

        // Check command-line flags after an update or rollback
        string[] args = Environment.GetCommandLineArgs();
        if (args.Contains("--updated"))
        {
            _trayService.ShowNotification(
                "Nâng cấp thành công!",
                $"Remote Print Manager đã nâng cấp thành công lên phiên bản V{_settings.AppVersion}. Mọi cấu hình và máy in được bảo toàn.",
                ToolTipIcon.Info
            );
        }
        else if (args.Contains("--rolled-back"))
        {
            _trayService.ShowNotification(
                "Khôi phục thành công!",
                $"Đã khôi phục thành công phiên bản Remote Print Manager trước đó (V{_settings.AppVersion}).",
                ToolTipIcon.Info
            );
        }
    }

    private void NavigateToCurrentRole()
    {
        // Cleanup old services safely
        try { _serverService?.Dispose(); } catch { }
        _serverService = null;
        try { _clientService?.Dispose(); } catch { }
        _clientService = null;

        switch (_settings.Role)
        {
            case AppRole.Server:
                LoadServerMode();
                break;
            case AppRole.Client:
                LoadClientMode();
                break;
            default:
                LoadModeSelection();
                break;
        }
    }

    private void LoadModeSelection()
    {
        Title = "Remote Print Manager - Chọn Vai Trò Thiết Bị";
        var selectionView = new ModeSelectionView();
        selectionView.OnRoleSelected += (s, role) =>
        {
            _settings.Role = role;
            ConfigManager.Save(_settings);
            NavigateToCurrentRole();
        };
        MainContentArea.Content = selectionView;
    }

    private void LoadServerMode()
    {
        Title = $"Remote Print Manager [MÁY CHỦ] - {_settings.DeviceName}";
        _serverService = new ServerService(_settings);

        _serverService.OnClientRequestedApproval += (s, client) =>
        {
            if (_settings.ShowDesktopNotifications)
            {
                _trayService?.ShowNotification(
                    "Yêu cầu kết nối in ấn mới",
                    $"Máy con '{client.DeviceName}' đang xin phép sử dụng máy in.",
                    ToolTipIcon.Info
                );
            }
        };

        var dashboard = new MainDashboardView(_settings, _serverService, null);
        dashboard.OnMinimizeRequested += (s, e) => _trayService?.HideMainWindow();
        dashboard.OnRoleChangeRequested += (s, e) => NavigateToCurrentRole();

        MainContentArea.Content = dashboard;
        _ = _serverService.StartAsync();
    }

    private void LoadClientMode()
    {
        Title = $"Remote Print Manager [MÁY CON] - {_settings.DeviceName}";
        _clientService = new ClientService(_settings);

        var dashboard = new MainDashboardView(_settings, null, _clientService);
        dashboard.OnMinimizeRequested += (s, e) => _trayService?.HideMainWindow();
        dashboard.OnRoleChangeRequested += (s, e) => NavigateToCurrentRole();

        MainContentArea.Content = dashboard;
        _ = _clientService.StartAsync();
    }

    public void ExitApplication()
    {
        _isExplicitExit = true;
        Close();
    }

    private void MainWindow_Closing(object? sender, CancelEventArgs e)
    {
        if (!_isExplicitExit && _settings.MinimizeToSystemTray)
        {
            e.Cancel = true;
            _trayService?.HideMainWindow();
            _trayService?.ShowNotification(
                "Remote Print Manager",
                "Phần mềm đang chạy ngầm trong khay hệ thống để duy trì kết nối in ấn.",
                ToolTipIcon.Info
            );
        }
        else
        {
            try { _trayService?.Dispose(); } catch { }
            try { _serverService?.Dispose(); } catch { }
            try { _clientService?.Dispose(); } catch { }
        }
    }
}
