using System.ComponentModel;
using System.Windows;
using System.Windows.Forms;
using RPM.App.Services;
using RPM.App.Views;
using RPM.Core.Config;
using RPM.Core.Models;
using RPM.Core.Update;

namespace RPM.App;

public partial class MainWindow : Window
{
    private AppSettings _settings;
    private TrayService? _trayService;
    private ServerService? _serverService;
    private ClientService? _clientService;
    private bool _isExplicitExit;
    private bool _isExitDialogOpen;
    private bool _servicesDisposed;
    private readonly SemaphoreSlim _roleNavigationLock = new(1, 1);
    private Task? _serverStartTask;
    private Task? _clientStartTask;

    public MainWindow()
    {
        InitializeComponent();
        _settings = ConfigManager.Load();
        _settings.AppVersion = UpdateService.GetInstalledVersion();
        UpdateService.CompletePendingUpdate(_settings);
        ConfigManager.Save(_settings);

        Loaded += MainWindow_Loaded;
        Closing += MainWindow_Closing;
    }

    private void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        _trayService = new TrayService(this);
        _ = NavigateToCurrentRoleAsync();

        // Check command-line flags after an update or rollback
        string[] args = Environment.GetCommandLineArgs();
        if (args.Contains("--updated"))
        {
            _trayService.ShowNotification(
                "Đã cập nhật",
                $"Đã cập nhật lên V{_settings.AppVersion}.",
                ToolTipIcon.Info
            );
        }
        else if (args.Contains("--rolled-back"))
        {
            _trayService.ShowNotification(
                "Đã khôi phục",
                $"Đã khôi phục bản V{_settings.AppVersion}.",
                ToolTipIcon.Info
            );
        }
    }

    private async Task NavigateToCurrentRoleAsync()
    {
        await _roleNavigationLock.WaitAsync();
        try
        {
            // Wait off the UI thread while background workers and sockets stop.
            if (_serverStartTask is not null)
            {
                try { await _serverStartTask; } catch { }
                _serverStartTask = null;
            }
            if (_clientStartTask is not null)
            {
                try { await _clientStartTask; } catch { }
                _clientStartTask = null;
            }
            try { if (_serverService is not null) await _serverService.DisposeAsync(); } catch { }
            _serverService = null;
            try { if (_clientService is not null) await _clientService.DisposeAsync(); } catch { }
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
        finally
        {
            _roleNavigationLock.Release();
        }
    }

    private void LoadModeSelection()
    {
        Title = "Remote Print Manager - Chọn cách dùng";
        var selectionView = new ModeSelectionView();
        selectionView.OnRoleSelected += (s, role) =>
        {
            _settings.Role = role;
            ConfigManager.Save(_settings);
            _ = NavigateToCurrentRoleAsync();
        };
        MainContentArea.Content = selectionView;
    }

    private void LoadServerMode()
    {
        Title = $"Remote Print Manager - Máy chủ - {_settings.DeviceName}";
        _serverService = new ServerService(_settings);

        _serverService.OnClientRequestedApproval += (s, client) =>
        {
            if (_settings.ShowDesktopNotifications)
            {
                _trayService?.ShowNotification(
                    "Yêu cầu kết nối",
                    $"Máy '{client.DeviceName}' muốn dùng máy in.",
                    ToolTipIcon.Info
                );
            }
        };

        var dashboard = new MainDashboardView(_settings, _serverService, null);
        dashboard.OnMinimizeRequested += (s, e) => _trayService?.HideMainWindow();
        dashboard.OnRoleChangeRequested += (s, e) => _ = NavigateToCurrentRoleAsync();

        MainContentArea.Content = dashboard;
        _serverStartTask = _serverService.StartAsync();
    }

    private void LoadClientMode()
    {
        Title = $"Remote Print Manager - Máy con - {_settings.DeviceName}";
        _clientService = new ClientService(_settings);

        var dashboard = new MainDashboardView(_settings, null, _clientService);
        dashboard.OnMinimizeRequested += (s, e) => _trayService?.HideMainWindow();
        dashboard.OnRoleChangeRequested += (s, e) => _ = NavigateToCurrentRoleAsync();

        MainContentArea.Content = dashboard;
        _clientStartTask = _clientService.StartAsync();
    }

    public void ExitApplication()
    {
        // Tray exit can start while the main window is hidden, so do not call Close()
        // first. Closing a hidden WPF window would re-enter the Closing event.
        if (_isExplicitExit || _isExitDialogOpen) return;

        bool wasHidden = !IsVisible;
        if (wasHidden)
        {
            Show();
            WindowState = WindowState.Normal;
            Activate();
        }

        _ = ShowExitConfirmationAsync(wasHidden);
    }

    private async void MainWindow_Closing(object? sender, CancelEventArgs e)
    {
        if (_isExplicitExit)
        {
            // Shutdown was already confirmed and services were disposed asynchronously.
            return;
        }

        // Cancel the native close first. The confirmation dialog must run outside
        // the current close operation; showing a window from Closing is invalid.
        e.Cancel = true;
        await ShowExitConfirmationAsync(wasHidden: false);
    }

    private async Task ShowExitConfirmationAsync(bool wasHidden)
    {
        if (_isExitDialogOpen) return;

        _isExitDialogOpen = true;
        try
        {
            var dialog = new ExitConfirmationDialog
            {
                Owner = this
            };

            dialog.ShowDialog();
            if (dialog.UserAction == ExitAction.Exit)
            {
                _isExplicitExit = true;
                await DisposeServicesAsync();
                Application.Current.Shutdown();
            }
            else if (dialog.UserAction == ExitAction.MinimizeToTray)
            {
                _trayService?.HideMainWindow();
                _trayService?.ShowNotification(
                    "Remote Print Manager",
                    "Ứng dụng vẫn đang chạy trong khay.",
                    ToolTipIcon.Info
                );
            }
            else if (wasHidden)
            {
                // Preserve the hidden state when the confirmation was opened
                // from the tray and the user cancelled.
                _trayService?.HideMainWindow();
            }
        }
        finally
        {
            _isExitDialogOpen = false;
        }
    }

    private async Task DisposeServicesAsync()
    {
        if (_servicesDisposed) return;
        _servicesDisposed = true;

        try { _trayService?.Dispose(); } catch { }
        try { if (_serverService is not null) await _serverService.DisposeAsync(); } catch { }
        try { if (_clientService is not null) await _clientService.DisposeAsync(); } catch { }
        _roleNavigationLock.Dispose();
    }
}
