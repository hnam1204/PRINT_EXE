using System.Collections.ObjectModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Button = System.Windows.Controls.Button;
using RPM.App.Services;
using RPM.Core.Config;
using RPM.Core.Diagnostics;
using RPM.Core.Models;
using RPM.Core.Security;
using RPM.Printing;

namespace RPM.App.Views;

public enum SettingsTab
{
    General,
    Printers,
    Devices,
    Queue
}

public partial class MainDashboardView : UserControl
{
    private readonly AppSettings _settings;
    private readonly ServerService? _serverService;
    private readonly ClientService? _clientService;

    // Collections for in-page views
    private readonly ObservableCollection<PrintJob> _recentJobs = new();
    private ObservableCollection<SharedPrinterInfo> _sharedPrinters = new();
    private readonly ObservableCollection<DeviceInfo> _filteredClients = new();
    private readonly ObservableCollection<PrintJob> _allJobs = new();

    private bool _isViewLoaded = false;
    private bool _isSettingsOpen = false;
    private SettingsTab _currentSettingsTab = SettingsTab.General;
    private bool _restoringPrinterSelection;
    private int _printerLoadVersion;

    public event EventHandler? OnMinimizeRequested;
    public event EventHandler? OnRoleChangeRequested;

    public MainDashboardView(AppSettings settings, ServerService? serverService, ClientService? clientService)
    {
        _settings = settings;
        _serverService = serverService;
        _clientService = clientService;

        InitializeComponent();

        // Bind in-page controls
        IcRecentJobs.ItemsSource = _recentJobs;
        CmbPrinterSelection.ItemsSource = _sharedPrinters;
        DgClients.ItemsSource = _filteredClients;
        DgQueue.ItemsSource = _allJobs;

        // Register background sync events
        if (_serverService != null)
        {
            _serverService.OnStatusChanged += (s, e) => Dispatcher.Invoke(UpdateServerUI);
            _serverService.PrintJobs.CollectionChanged += (s, e) => Dispatcher.Invoke(() =>
            {
                RefreshRecentJobs();
                if (_isSettingsOpen && _currentSettingsTab == SettingsTab.Queue) LoadQueueView();
            });
            _serverService.ConnectedClients.CollectionChanged += (s, e) => Dispatcher.Invoke(() =>
            {
                if (_isSettingsOpen && _currentSettingsTab == SettingsTab.Devices) ApplyClientFilters();
                UpdateServerUI();
            });
        }

        if (_clientService != null)
        {
            _clientService.OnStateChanged += (s, e) => Dispatcher.Invoke(UpdateClientUI);
            _clientService.PrintHistory.CollectionChanged += (s, e) => Dispatcher.Invoke(() =>
            {
                RefreshRecentJobs();
                if (_isSettingsOpen && _currentSettingsTab == SettingsTab.Queue) LoadQueueView();
            });
        }

        Loaded += MainDashboardView_Loaded;
    }

    private void MainDashboardView_Loaded(object sender, RoutedEventArgs e)
    {
        _isViewLoaded = true;

        if (_settings.Role == AppRole.Server)
        {
            PanelServerMode.Visibility = Visibility.Visible;
            PanelClientMode.Visibility = Visibility.Collapsed;
            BtnTabDevices.Visibility = Visibility.Visible;

            TxtServerPairingCode.Text = _settings.PairingCode;

            UpdateServerUI();
        }
        else
        {
            PanelServerMode.Visibility = Visibility.Collapsed;
            PanelClientMode.Visibility = Visibility.Visible;
            BtnTabDevices.Visibility = Visibility.Collapsed;

            TxtClientCodeInput.Text = _settings.PairingCode;

            UpdateClientUI();
        }

        RefreshRecentJobs();
        CloseSettings();
    }

    // =========================================================
    // NAVIGATION: HOME <-> SETTINGS CENTER
    // =========================================================

    public void OpenSettings(SettingsTab tab = SettingsTab.General)
    {
        _isSettingsOpen = true;
        ViewHome.Visibility = Visibility.Collapsed;
        ViewSettings.Visibility = Visibility.Visible;
        BtnHeaderSettingsIcon.Data = (Geometry)Application.Current.Resources["GeoArrowLeft"];
        BtnHeaderSettingsText.Text = "Quay lại";

        SelectSettingsTab(tab);
    }

    public void CloseSettings()
    {
        _isSettingsOpen = false;
        ViewHome.Visibility = Visibility.Visible;
        ViewSettings.Visibility = Visibility.Collapsed;
        BtnHeaderSettingsIcon.Data = (Geometry)Application.Current.Resources["GeoSettings"];
        BtnHeaderSettingsText.Text = "Cài đặt";

        TxtRoleSubtitle.Text = _settings.Role == AppRole.Server ? "MÁY CHỦ" : "MÁY CON";

        if (_settings.Role == AppRole.Server) UpdateServerUI();
        else UpdateClientUI();
        RefreshRecentJobs();
    }

    private void SelectSettingsTab(SettingsTab tab)
    {
        _currentSettingsTab = tab;
        ViewSettingsContentRow.Height = tab == SettingsTab.General ? GridLength.Auto : new GridLength(1, GridUnitType.Star);

        // Toggle content containers
        TabContentGeneral.Visibility = (tab == SettingsTab.General) ? Visibility.Visible : Visibility.Collapsed;
        TabContentPrinters.Visibility = (tab == SettingsTab.Printers) ? Visibility.Visible : Visibility.Collapsed;
        TabContentDevices.Visibility = (tab == SettingsTab.Devices) ? Visibility.Visible : Visibility.Collapsed;
        // Queue management is intentionally grouped into the printer screen.

        // Update Tab button active states with ZERO template recreation (Ultra-fast & butter smooth)
        BtnTabGeneral.Tag = (tab == SettingsTab.General) ? "Active" : "Inactive";
        BtnTabPrinters.Tag = (tab == SettingsTab.Printers) ? "Active" : "Inactive";
        BtnTabDevices.Tag = (tab == SettingsTab.Devices) ? "Active" : "Inactive";

        // Update Subtitle and trigger non-blocking asynchronous loads
        switch (tab)
        {
            case SettingsTab.General:
                TxtRoleSubtitle.Text = "CÀI ĐẶT: CHUNG";
                LoadGeneralSettingsView();
                break;
            case SettingsTab.Printers:
                TxtRoleSubtitle.Text = "CÀI ĐẶT: MÁY IN";
                LoadPrintersViewAsync();
                break;
            case SettingsTab.Devices:
                TxtRoleSubtitle.Text = "CÀI ĐẶT: THIẾT BỊ";
                LoadDevicesView();
                break;
            case SettingsTab.Queue:
                SelectSettingsTab(SettingsTab.Printers);
                break;
        }
    }

    // =========================================================
    // VIEW 0: HOME LOGIC
    // =========================================================

    private void UpdateServerUI()
    {
        if (_serverService == null) return;

        bool isOnline = _serverService.Relay.IsConnected;
        TxtHeaderStatus.Text = isOnline ? "Đang hoạt động" : "Sẵn sàng";
        DotStatus.Fill = (SolidColorBrush)Application.Current.Resources["SuccessBrush"];
        PillStatus.Background = (SolidColorBrush)Application.Current.Resources["SuccessLightBrush"];
        TxtHeaderStatus.Foreground = (SolidColorBrush)Application.Current.Resources["SuccessBrush"];

        TxtServerConnectionStatus.Text = isOnline ? "Đã kết nối" : "Đang chạy cục bộ";
        if (_isSettingsOpen && _currentSettingsTab == SettingsTab.General)
            TxtSettingsConnectionStatus.Text = isOnline ? "Đã kết nối" : "Chưa kết nối";

        int sharedCount = _serverService.SharedPrinters.Count(p => p.IsShared);
        if (_serverService.ActiveSharedPrinter != null)
        {
            TxtSharedPrinterName.Text = _serverService.ActiveSharedPrinter.Name;
            TxtSharedPrinterPort.Text = $"Cổng: {_serverService.ActiveSharedPrinter.PortName} • {sharedCount} máy dùng chung";
            TxtActivePrinterSummary.Text = $"Máy in chính: {_serverService.ActiveSharedPrinter.Name}";
        }
        else
        {
            TxtSharedPrinterName.Text = "Chưa chọn máy in";
            TxtSharedPrinterPort.Text = "Chọn máy in để dùng chung";
            TxtActivePrinterSummary.Text = "Chưa chọn máy in";
        }

        int onlineClients = _serverService.ConnectedClients.Count(c => c.IsOnline && c.IsApproved);
        int pendingClients = _serverService.ConnectedClients.Count(c => !c.IsApproved && !c.IsBlocked);
        TxtServerClientsSummary.Text = $"{_serverService.ConnectedClients.Count} máy ({onlineClients} đang dùng • {pendingClients} chờ duyệt)";

        RefreshRecentJobs();
    }

    private void UpdateClientUI()
    {
        if (_clientService == null) return;

        bool isApproved = _clientService.IsApproved;
        TxtHeaderStatus.Text = isApproved ? "Đã kết nối" : _clientService.ConnectionStatus;
        if (_isSettingsOpen && _currentSettingsTab == SettingsTab.General)
            TxtSettingsConnectionStatus.Text = isApproved && _clientService.Relay.IsConnected ? "Đã kết nối" : "Chưa kết nối";

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

            TxtClientConnectBtnText.Text = "Kết nối";
            BtnClientConnectToggle.Style = (Style)Application.Current.Resources["PrimaryBtn"];
        }

        if (_clientService.RemotePrinter != null)
        {
            TxtClientPrinterName.Text = _clientService.RemotePrinter.Name;
            TxtClientPrinterStatus.Text = $"Cổng: 127.0.0.1:{_settings.LocalIppPort}";
            TxtActivePrinterSummary.Text = $"Máy in: {_clientService.RemotePrinter.Name}";
        }
        else
        {
            TxtClientPrinterName.Text = "Chưa kết nối máy in";
            TxtClientPrinterStatus.Text = "Vui lòng kết nối tới máy chủ";
            TxtActivePrinterSummary.Text = "Đang chờ kết nối";
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
    // TAB: GENERAL SETTINGS
    // =========================================================

    private void LoadGeneralSettingsView()
    {
        TxtSettingsDeviceName.Text = _settings.DeviceName;
        TxtSettingsRole.Text = _settings.Role == AppRole.Server ? "Máy chủ" : "Máy con";
        TxtSettingsConnectionStatus.Text = GetActualConnectionStatus();
        TxtSettingsVersion.Text = $"V{_settings.AppVersion}";
        TxtSettingsUpdateVersion.Text = $"V{_settings.AppVersion}";
        ChkSettingsAutoStart.IsChecked = _settings.AutoStartWithWindows;
        ChkSettingsMinimizeTray.IsChecked = _settings.MinimizeToSystemTray;
        ChkSettingsNotifications.IsChecked = _settings.ShowDesktopNotifications;

        TxtSwitchRoleBtn.Text = _settings.Role == AppRole.Server ? "Dùng làm máy con" : "Dùng làm máy chủ";
        ConfigureUpdateControl();
    }

    private string GetActualConnectionStatus()
    {
        if (_serverService != null)
        {
            return _serverService.Relay.IsConnected ? "Đã kết nối" : "Chưa kết nối";
        }

        if (_clientService != null)
        {
            return _clientService.IsApproved && _clientService.Relay.IsConnected
                ? "Đã kết nối"
                : "Chưa kết nối";
        }

        return "Chưa kết nối";
    }

    private void BtnSaveSettings_Click(object sender, RoutedEventArgs e)
    {
        _settings.DeviceName = TxtSettingsDeviceName.Text.Trim();
        _settings.AutoStartWithWindows = ChkSettingsAutoStart.IsChecked == true;
        _settings.MinimizeToSystemTray = ChkSettingsMinimizeTray.IsChecked == true;
        _settings.ShowDesktopNotifications = ChkSettingsNotifications.IsChecked == true;

        ConfigManager.Save(_settings);

        if (_settings.Role == AppRole.Server)
        {
            TxtServerPairingCode.Text = _settings.PairingCode;
            UpdateServerUI();
        }
        else
        {
            TxtClientCodeInput.Text = _settings.PairingCode;
            UpdateClientUI();
        }

        MessageBox.Show("Đã lưu cài đặt.", "Đã lưu", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void BtnSwitchRole_Click(object sender, RoutedEventArgs e)
    {
        var targetRole = _settings.Role == AppRole.Server ? AppRole.Client : AppRole.Server;
        string targetRoleName = targetRole == AppRole.Server ? "máy chủ" : "máy con";

        var confirm = MessageBox.Show(
            $"Đổi sang {targetRoleName}?\n\nCài đặt hiện tại vẫn được giữ nguyên.",
            "Đổi vai trò",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question
        );

        if (confirm == MessageBoxResult.Yes)
        {
            _settings.Role = targetRole;
            ConfigManager.Save(_settings);
            OnRoleChangeRequested?.Invoke(this, EventArgs.Empty);
        }
    }

    // =========================================================
    // TAB: PRINTERS (NON-BLOCKING BACKGROUND SCAN)
    // =========================================================

    private async void LoadPrintersViewAsync()
    {
        int requestVersion = Interlocked.Increment(ref _printerLoadVersion);
        string? selectedName = (CmbPrinterSelection.SelectedItem as SharedPrinterInfo)?.Name ?? _settings.SelectedPrinterName;

        var refreshed = new List<SharedPrinterInfo>();
        if (_serverService != null)
        {
            refreshed.AddRange(await Task.Run(_serverService.ScanPrinters));
        }
        else
        {
            var printerService = new PrinterService();
            var list = await Task.Run(() => printerService.GetInstalledPrinters());
            refreshed.AddRange(list);
        }
        if (requestVersion != _printerLoadVersion || !_isSettingsOpen || _currentSettingsTab != SettingsTab.Printers) return;
        selectedName = (CmbPrinterSelection.SelectedItem as SharedPrinterInfo)?.Name ?? selectedName;
        _serverService?.ApplyPrinterScan(refreshed);
        ApplyPrinterList(refreshed, selectedName);
    }

    private async void BtnPrintersRescan_Click(object sender, RoutedEventArgs e)
    {
        int requestVersion = Interlocked.Increment(ref _printerLoadVersion);
        var btn = sender as Button;
        string? selectedName = (CmbPrinterSelection.SelectedItem as SharedPrinterInfo)?.Name ?? _settings.SelectedPrinterName;
        if (btn != null) btn.IsEnabled = false;

        var refreshed = _serverService == null
            ? new List<SharedPrinterInfo>()
            : await Task.Run(_serverService.ScanPrinters);
        if (requestVersion != _printerLoadVersion)
        {
            if (btn != null) btn.IsEnabled = true;
            return;
        }
        _serverService?.ApplyPrinterScan(refreshed);
        ApplyPrinterList(refreshed, selectedName);
        if (btn != null) btn.IsEnabled = true;
        MessageBox.Show($"Đã tìm thấy {_sharedPrinters.Count} máy in.", "Máy in", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void ApplyPrinterList(IEnumerable<SharedPrinterInfo> printers, string? selectedName)
    {
        var next = new ObservableCollection<SharedPrinterInfo>(printers);
        _restoringPrinterSelection = true;
        _sharedPrinters = next;
        CmbPrinterSelection.ItemsSource = next;
        CmbPrinterSelection.SelectedItem = next.FirstOrDefault(p => string.Equals(p.Name, selectedName, StringComparison.OrdinalIgnoreCase))
            ?? next.FirstOrDefault(p => p.IsDefault)
            ?? next.FirstOrDefault();
        _restoringPrinterSelection = false;

        if (CmbPrinterSelection.SelectedItem is SharedPrinterInfo selected)
            UpdateSelectedPrinterText(selected);
    }

    private void CmbPrinterSelection_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (CmbPrinterSelection.SelectedItem is not SharedPrinterInfo printer)
        {
            TxtSelectedPrinterInfo.Text = "Chưa chọn máy in";
            TxtSelectedPrinterStatus.Text = "Chọn một máy in trong danh sách";
            return;
        }

        UpdateSelectedPrinterText(printer);
        if (!_restoringPrinterSelection && _serverService != null && !string.Equals(_serverService.ActiveSharedPrinter?.Name, printer.Name, StringComparison.OrdinalIgnoreCase))
        {
            _serverService.SetDefaultPrinter(printer.Name);
        }
    }

    private void UpdateSelectedPrinterText(SharedPrinterInfo printer)
    {
        TxtSelectedPrinterInfo.Text = printer.Name;
        TxtSelectedPrinterStatus.Text = $"{printer.DriverName} • {printer.PortName} • {(printer.IsShared ? "Đang chia sẻ" : "Chưa chia sẻ")}";
    }

    private void BtnPrinterToggleSelected_Click(object sender, RoutedEventArgs e)
    {
        if (_serverService == null || CmbPrinterSelection.SelectedItem is not SharedPrinterInfo printer) return;
        _serverService.TogglePrinterSharing(printer.Name);
        LoadPrintersViewAsync();
        UpdateServerUI();
    }

    private async void BtnPrintersTestPrint_Click(object sender, RoutedEventArgs e)
    {
        if (CmbPrinterSelection.SelectedItem is SharedPrinterInfo selected)
        {
            var btn = sender as Button;
            if (btn != null) btn.IsEnabled = false;

            string printerName = selected.Name;
            string err = string.Empty;
            bool ok = await Task.Run(() =>
            {
                var printerService = _serverService?.PrinterService ?? new PrinterService();
                return printerService.PrintTestPage(printerName, "Máy Cục Bộ", Environment.MachineName, out err);
            });

            if (btn != null) btn.IsEnabled = true;

            if (ok)
            {
                MessageBox.Show($"Đã gửi trang in thử tới '{printerName}'.", "In thử", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            else
            {
                MessageBox.Show($"In thử thất bại: {err}", "Lỗi in", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
        else
        {
            MessageBox.Show("Hãy chọn máy in trước.", "Chưa chọn máy in", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    // =========================================================
    // TAB: DEVICES
    // =========================================================

    private void LoadDevicesView()
    {
        if (CmbStatusFilter != null && CmbStatusFilter.SelectedIndex < 0) CmbStatusFilter.SelectedIndex = 0;
        ApplyClientFilters();
    }

    private void ApplyClientFilters()
    {
        if (!_isViewLoaded || _serverService == null) return;

        string searchText = TxtSearchClient?.Text?.Trim().ToLowerInvariant() ?? string.Empty;
        string statusFilter = (CmbStatusFilter?.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "Tất cả";

        _filteredClients.Clear();
        int onlineCount = 0;
        int pendingCount = 0;
        int offlineCount = 0;

        foreach (var client in _serverService.ConnectedClients)
        {
            if (client.IsOnline) onlineCount++;
            else offlineCount++;

            if (!client.IsApproved && !client.IsBlocked) pendingCount++;

            if (!string.IsNullOrEmpty(searchText) && !client.DeviceName.Contains(searchText, StringComparison.OrdinalIgnoreCase)) continue;

            if (statusFilter == "Trực tuyến" && (!client.IsApproved || !client.IsOnline)) continue;
            if (statusFilter == "Ngoại tuyến" && (!client.IsApproved || client.IsOnline)) continue;
            if (statusFilter == "Chờ phê duyệt" && (client.IsApproved || client.IsBlocked)) continue;

            _filteredClients.Add(client);
        }

        if (TxtClientStats != null)
        {
            TxtClientStats.Text = $"{_serverService.ConnectedClients.Count} thiết bị · {onlineCount} trực tuyến · {pendingCount} chờ duyệt";
        }
        if (PanelClientsEmpty != null) PanelClientsEmpty.Visibility = _filteredClients.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void TxtSearchClient_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_isViewLoaded) ApplyClientFilters();
    }

    private void CmbFilter_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isViewLoaded) ApplyClientFilters();
    }

    private void BtnClearFilters_Click(object sender, RoutedEventArgs e)
    {
        if (TxtSearchClient != null) TxtSearchClient.Text = string.Empty;
        if (CmbStatusFilter != null) CmbStatusFilter.SelectedIndex = 0;
        ApplyClientFilters();
    }

    private void BtnManageClient_Click(object sender, RoutedEventArgs e)
    {
        if (_serverService == null || (sender as Button)?.DataContext is not DeviceInfo client) return;
        var dialog = new DeviceManageDialog(client, _serverService.SharedPrinters.Where(p => p.IsShared).Select(p => p.Name)) { Owner = Window.GetWindow(this) };
        if (dialog.ShowDialog() != true) return;

        switch (dialog.Action)
        {
            case DeviceManageAction.Approve:
                _ = ManageApproveAsync(client);
                break;
            case DeviceManageAction.Revoke:
                _ = ManageRevokeAsync(client);
                break;
            case DeviceManageAction.SavePrinters:
                _serverService.SetClientAllowedPrinters(client.DeviceId, dialog.SelectedPrinters);
                ApplyClientFilters();
                break;
        }
    }

    private async Task ManageApproveAsync(DeviceInfo client)
    {
        await _serverService!.ApproveClientAsync(client);
        ApplyClientFilters();
        UpdateServerUI();
    }

    private async Task ManageRevokeAsync(DeviceInfo client)
    {
        await _serverService!.RevokeClientAsync(client);
        ApplyClientFilters();
        UpdateServerUI();
    }

    private async void BtnApproveClient_Click(object sender, RoutedEventArgs e)
    {
        if (_serverService == null) return;
        if (DgClients.SelectedItem is DeviceInfo client)
        {
            await _serverService.ApproveClientAsync(client);
            ApplyClientFilters();
            UpdateServerUI();
            MessageBox.Show($"Đã cho phép máy '{client.DeviceName}'.", "Đã cho phép", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        else
        {
            MessageBox.Show("Hãy chọn máy cần cho phép.", "Chưa chọn máy", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void BtnChangeDepartment_Click(object sender, RoutedEventArgs e)
    {
        if (_serverService == null) return;
        if (DgClients.SelectedItem is DeviceInfo client)
        {
            var depts = _settings.Departments;
            int currentIndex = depts.IndexOf(client.Department);
            int nextIndex = (currentIndex + 1) % depts.Count;
            string nextDept = depts[nextIndex];

            _serverService.SetClientDepartment(client.DeviceId, nextDept);
            ApplyClientFilters();
            MessageBox.Show($"Đã chuyển máy '{client.DeviceName}' sang phòng '{nextDept}'.", "Đổi phòng", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        else
        {
            MessageBox.Show("Hãy chọn máy cần đổi phòng.", "Chưa chọn máy", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void BtnAssignPrinters_Click(object sender, RoutedEventArgs e)
    {
        if (_serverService == null) return;
        if (DgClients.SelectedItem is DeviceInfo client)
        {
            var sharedPrinters = _serverService.SharedPrinters.Where(p => p.IsShared).Select(p => p.Name).ToList();
            if (sharedPrinters.Count == 0)
            {
                MessageBox.Show("Chưa có máy in dùng chung.", "Chưa có máy in", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (client.AllowedPrinters.Count == 0)
            {
                _serverService.SetClientAllowedPrinters(client.DeviceId, new List<string> { sharedPrinters[0] });
                MessageBox.Show($"Máy '{client.DeviceName}' chỉ được in trên '{sharedPrinters[0]}'.", "Đã lưu", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            else
            {
                _serverService.SetClientAllowedPrinters(client.DeviceId, new List<string>());
                MessageBox.Show($"Máy '{client.DeviceName}' được dùng tất cả máy in chung.", "Đã lưu", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            ApplyClientFilters();
        }
        else
        {
            MessageBox.Show("Hãy chọn máy trước.", "Chưa chọn máy", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private async void BtnRevokeClient_Click(object sender, RoutedEventArgs e)
    {
        if (_serverService == null) return;
        if (DgClients.SelectedItem is DeviceInfo client)
        {
            if (MessageBox.Show($"Bỏ quyền của máy '{client.DeviceName}'?", "Xác nhận", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
            {
                await _serverService.RevokeClientAsync(client);
                ApplyClientFilters();
                UpdateServerUI();
            }
        }
        else
        {
            MessageBox.Show("Hãy chọn máy trước.", "Chưa chọn máy", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void BtnDeleteClient_Click(object sender, RoutedEventArgs e)
    {
        if (_serverService == null) return;
        if (DgClients.SelectedItem is DeviceInfo client)
        {
            if (MessageBox.Show($"Xóa máy '{client.DeviceName}' khỏi danh sách?", "Xác nhận xóa", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes)
            {
                _serverService.DeleteClient(client.DeviceId);
                ApplyClientFilters();
                UpdateServerUI();
            }
        }
        else
        {
            MessageBox.Show("Hãy chọn máy cần xóa.", "Chưa chọn máy", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    // =========================================================
    // TAB: QUEUE
    // =========================================================

    private void LoadQueueView()
    {
        _allJobs.Clear();
        IEnumerable<PrintJob> source = _serverService != null 
            ? _serverService.PrintJobs 
            : (_clientService != null ? _clientService.PrintHistory : Enumerable.Empty<PrintJob>());

        foreach (var job in source.Reverse())
        {
            _allJobs.Add(job);
        }

        int total = _allJobs.Count;
        int completed = _allJobs.Count(j => j.Status == PrintJobStatus.Completed);
        int pending = _allJobs.Count(j => j.Status == PrintJobStatus.Pending || j.Status == PrintJobStatus.Printing);
        int failed = _allJobs.Count(j => j.Status == PrintJobStatus.Failed);

        TxtQueueSummary.Text = $"Tổng số: {total} lệnh in • Đang chờ/xử lý: {pending} • Hoàn tất: {completed} • Lỗi: {failed}";
    }

    private void BtnQueueRefresh_Click(object sender, RoutedEventArgs e)
    {
        LoadQueueView();
    }

    private async void BtnRestartSpooler_Click(object sender, RoutedEventArgs e)
    {
        var btn = sender as Button;
        if (btn != null) btn.IsEnabled = false;

        string err = string.Empty;
        bool restarted = await Task.Run(() => CompatibilityChecker.RestartSpoolerService(out err));

        if (btn != null) btn.IsEnabled = true;

        if (restarted)
        {
            MessageBox.Show("Đã khởi động lại dịch vụ in.", "Đã xong", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        else
        {
            MessageBox.Show($"Không khởi động được dịch vụ in: {err}\nHãy mở ứng dụng bằng quyền quản trị.", "Lỗi dịch vụ in", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void BtnClearCompletedJobs_Click(object sender, RoutedEventArgs e)
    {
        if (_serverService != null)
        {
            _serverService.ClearCompletedJobs();
        }
        else if (_clientService != null)
        {
            var completed = _clientService.PrintHistory.Where(j => j.Status == PrintJobStatus.Completed || j.Status == PrintJobStatus.Failed).ToList();
            foreach (var j in completed) _clientService.PrintHistory.Remove(j);
        }
        LoadQueueView();
        RefreshRecentJobs();
    }

    // =========================================================
    // UPDATE CONTROL EMBEDDED IN GENERAL SETTINGS
    // =========================================================

    private void ConfigureUpdateControl()
    {
        SettingsUpdateControl.Initialize(_settings);
        SettingsUpdateControl.StatusChanged -= SettingsUpdateControl_StatusChanged;
        SettingsUpdateControl.StatusChanged += SettingsUpdateControl_StatusChanged;
        SettingsUpdateControl.HasActivePrintJobsCheck = () =>
        {
            if (_serverService != null) return _serverService.PrintJobs.Any(j => j.Status == PrintJobStatus.Printing || j.Status == PrintJobStatus.Pending);
            if (_clientService != null) return _clientService.PrintHistory.Any(j => j.Status == PrintJobStatus.Printing || j.Status == PrintJobStatus.Pending);
            return false;
        };
    }

    private void SettingsUpdateControl_StatusChanged(object? sender, string status)
    {
        TxtSettingsUpdateStatus.Text = status;
    }

    private async void BtnSettingsCheckUpdate_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new UpdateDetailsDialog(_settings, SettingsUpdateControl.HasActivePrintJobsCheck)
        {
            Owner = Window.GetWindow(this)
        };
        dialog.Show();
        await dialog.CheckNowAsync();
    }

    // =========================================================
    // TAB BAR BUTTON CLICKS
    // =========================================================

    private void BtnTabGeneral_Click(object sender, RoutedEventArgs e) => SelectSettingsTab(SettingsTab.General);
    private void BtnTabPrinters_Click(object sender, RoutedEventArgs e) => SelectSettingsTab(SettingsTab.Printers);
    private void BtnTabDevices_Click(object sender, RoutedEventArgs e) => SelectSettingsTab(SettingsTab.Devices);

    // =========================================================
    // GLOBAL BUTTON HANDLERS
    // =========================================================

    private void BtnOpenSettings_Click(object sender, RoutedEventArgs e)
    {
        if (_isSettingsOpen)
        {
            CloseSettings();
        }
        else
        {
            OpenSettings(SettingsTab.General);
        }
    }

    private void BtnOpenPrinterDialog_Click(object sender, RoutedEventArgs e) => OpenSettings(SettingsTab.Printers);
    private void BtnOpenDevices_Click(object sender, RoutedEventArgs e) => OpenSettings(SettingsTab.Devices);
    private void BtnOpenQueueDialog_Click(object sender, RoutedEventArgs e) => OpenSettings(SettingsTab.Queue);
    private void BtnOpenUpdate_Click(object sender, RoutedEventArgs e) => OpenSettings(SettingsTab.General);
    private void BtnOpenDiagnostics_Click(object sender, RoutedEventArgs e) => OpenDiagnosticsDialog();

    private void OpenDiagnosticsDialog()
    {
        var dialog = new MachineDiagnosticsDialog(_settings, _serverService, _clientService)
        {
            Owner = Window.GetWindow(this)
        };
        dialog.ShowDialog();
    }

    private void BtnMinimize_Click(object sender, RoutedEventArgs e) => OnMinimizeRequested?.Invoke(this, EventArgs.Empty);

    private async void BtnCopyPairingCode_Click(object sender, RoutedEventArgs e)
    {
        bool copied = false;
        const int maxAttempts = 5;

        for (int attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                Clipboard.SetText(_settings.PairingCode);
                copied = true;
                break;
            }
            catch (ExternalException ex) when ((uint)ex.HResult == 0x800401D0u)
            {
                // Another process may briefly own the Windows clipboard.
                if (attempt < maxAttempts) await Task.Delay(80 * attempt);
            }
        }

        if (!copied)
        {
            MessageBox.Show(
                "Không thể sao chép lúc này vì bộ nhớ tạm đang được ứng dụng khác sử dụng. Hãy thử lại sau ít giây.",
                "Không thể sao chép",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        // Tactile micro-feedback directly on button
        if (TxtCopyPairingCode != null)
        {
            TxtCopyPairingCode.Text = "✓ Đã chép";
            if (BtnCopyPairingCode != null)
            {
                BtnCopyPairingCode.Background = (SolidColorBrush)Application.Current.Resources["SuccessBrush"];
            }

            await Task.Delay(1500);

            TxtCopyPairingCode.Text = "Chép mã";
            if (BtnCopyPairingCode != null)
            {
                BtnCopyPairingCode.ClearValue(BackgroundProperty);
            }
        }
    }

    private async void BtnClientConnectToggle_Click(object sender, RoutedEventArgs e)
    {
        if (_clientService == null) return;

        if (_clientService.IsApproved)
        {
            if (MessageBox.Show("Ngắt kết nối máy chủ?", "Xác nhận", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
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
                MessageBox.Show("Hãy nhập mã kết nối.", "Thiếu mã", MessageBoxButton.OK, MessageBoxImage.Warning);
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
            MessageBox.Show("Máy chủ chưa cho phép kết nối.", "Chưa được phép", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        bool ok = await _clientService.SendTestPrintAsync();
        if (ok)
        {
            MessageBox.Show("Đã gửi lệnh in thử.", "In thử", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        else
        {
            MessageBox.Show("Không gửi được lệnh in. Hãy kiểm tra máy chủ và mạng.", "Lỗi in", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }
}
