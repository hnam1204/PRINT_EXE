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
    private readonly ObservableCollection<SharedPrinterInfo> _sharedPrinters = new();
    private readonly ObservableCollection<DeviceInfo> _filteredClients = new();
    private readonly ObservableCollection<PrintJob> _allJobs = new();

    private bool _isViewLoaded = false;
    private bool _isSettingsOpen = false;
    private SettingsTab _currentSettingsTab = SettingsTab.General;

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
        DgPrinters.ItemsSource = _sharedPrinters;
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

        // Toggle content containers
        TabContentGeneral.Visibility = (tab == SettingsTab.General) ? Visibility.Visible : Visibility.Collapsed;
        TabContentPrinters.Visibility = (tab == SettingsTab.Printers) ? Visibility.Visible : Visibility.Collapsed;
        TabContentDevices.Visibility = (tab == SettingsTab.Devices) ? Visibility.Visible : Visibility.Collapsed;
        TabContentQueue.Visibility = (tab == SettingsTab.Queue) ? Visibility.Visible : Visibility.Collapsed;

        // Update Tab button active states with ZERO template recreation (Ultra-fast & butter smooth)
        BtnTabGeneral.Tag = (tab == SettingsTab.General) ? "Active" : "Inactive";
        BtnTabPrinters.Tag = (tab == SettingsTab.Printers) ? "Active" : "Inactive";
        BtnTabDevices.Tag = (tab == SettingsTab.Devices) ? "Active" : "Inactive";
        BtnTabQueue.Tag = (tab == SettingsTab.Queue) ? "Active" : "Inactive";

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
                TxtRoleSubtitle.Text = "CÀI ĐẶT: LỆNH IN";
                LoadQueueView();
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
        TxtSettingsPairingCode.Text = _settings.PairingCode;
        TxtSettingsRelayUrl.Text = _settings.RelayServerUrl;
        ChkSettingsAutoStart.IsChecked = _settings.AutoStartWithWindows;
        ChkSettingsMinimizeTray.IsChecked = _settings.MinimizeToSystemTray;
        ChkSettingsNotifications.IsChecked = _settings.ShowDesktopNotifications;

        TxtSwitchRoleBtn.Text = _settings.Role == AppRole.Server ? "Dùng làm máy con" : "Dùng làm máy chủ";
        ConfigureUpdateControl();
    }

    private void BtnSettingsRegenerateCode_Click(object sender, RoutedEventArgs e)
    {
        _settings.PairingCode = RPMCrypto.GeneratePairingCode();
        TxtSettingsPairingCode.Text = _settings.PairingCode;
        MessageBox.Show($"Mã mới: {_settings.PairingCode}.\nBấm Lưu để dùng mã này.", "Mã mới", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void BtnSaveSettings_Click(object sender, RoutedEventArgs e)
    {
        _settings.DeviceName = TxtSettingsDeviceName.Text.Trim();
        _settings.PairingCode = TxtSettingsPairingCode.Text.Trim();
        _settings.RelayServerUrl = TxtSettingsRelayUrl.Text.Trim();
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
        // 1. Instantly display existing list so tab switch has zero visual lag
        if (_serverService != null && _serverService.SharedPrinters.Count > 0)
        {
            _sharedPrinters.Clear();
            foreach (var p in _serverService.SharedPrinters)
            {
                _sharedPrinters.Add(p);
            }
        }

        // 2. Refresh from Windows in background thread without blocking UI
        await Task.Run(() =>
        {
            if (_serverService != null)
            {
                _serverService.LoadPrinters();
            }
        });

        // 3. Update UI
        _sharedPrinters.Clear();
        if (_serverService != null)
        {
            foreach (var p in _serverService.SharedPrinters)
            {
                _sharedPrinters.Add(p);
            }
        }
        else
        {
            var printerService = new PrinterService();
            var list = await Task.Run(() => printerService.GetInstalledPrinters());
            foreach (var p in list)
            {
                _sharedPrinters.Add(p);
            }
        }
    }

    private async void BtnPrintersRescan_Click(object sender, RoutedEventArgs e)
    {
        var btn = sender as Button;
        if (btn != null) btn.IsEnabled = false;

        await Task.Run(() =>
        {
            if (_serverService != null)
            {
                _serverService.LoadPrinters();
            }
        });

        _sharedPrinters.Clear();
        if (_serverService != null)
        {
            foreach (var p in _serverService.SharedPrinters)
            {
                _sharedPrinters.Add(p);
            }
        }

        if (btn != null) btn.IsEnabled = true;
        MessageBox.Show($"Đã tìm thấy {_sharedPrinters.Count} máy in.", "Máy in", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void BtnPrinterToggleRowShare_Click(object sender, RoutedEventArgs e)
    {
        if (_serverService == null || (sender as Button)?.DataContext is not SharedPrinterInfo printer) return;

        _serverService.TogglePrinterSharing(printer.Name);
        LoadPrintersViewAsync();
        UpdateServerUI();
    }

    private void BtnPrinterSetRowDefault_Click(object sender, RoutedEventArgs e)
    {
        if (_serverService == null || (sender as Button)?.DataContext is not SharedPrinterInfo printer) return;

        _serverService.SetDefaultPrinter(printer.Name);
        LoadPrintersViewAsync();
        UpdateServerUI();
    }

    private async void BtnPrintersTestPrint_Click(object sender, RoutedEventArgs e)
    {
        if (DgPrinters.SelectedItem is SharedPrinterInfo selected)
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
        if (CmbDepartmentFilter != null && CmbDepartmentFilter.Items.Count <= 1)
        {
            CmbDepartmentFilter.Items.Clear();
            CmbDepartmentFilter.Items.Add(new ComboBoxItem { Content = "Tất cả phòng ban", IsSelected = true });
            foreach (var dept in _settings.Departments)
            {
                CmbDepartmentFilter.Items.Add(new ComboBoxItem { Content = dept });
            }
        }
        if (CmbStatusFilter != null && CmbStatusFilter.SelectedIndex < 0) CmbStatusFilter.SelectedIndex = 0;
        if (CmbDepartmentFilter != null && CmbDepartmentFilter.SelectedIndex < 0) CmbDepartmentFilter.SelectedIndex = 0;
        ApplyClientFilters();
    }

    private void ApplyClientFilters()
    {
        if (!_isViewLoaded || _serverService == null) return;

        string searchText = TxtSearchClient?.Text?.Trim().ToLowerInvariant() ?? string.Empty;
        string statusFilter = (CmbStatusFilter?.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "Tất cả";
        string deptFilter = (CmbDepartmentFilter?.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "Tất cả phòng ban";

        _filteredClients.Clear();
        int onlineCount = 0;
        int pendingCount = 0;
        int offlineCount = 0;

        foreach (var client in _serverService.ConnectedClients)
        {
            if (client.IsOnline) onlineCount++;
            else offlineCount++;

            if (!client.IsApproved && !client.IsBlocked) pendingCount++;

            if (!string.IsNullOrEmpty(searchText))
            {
                bool matchName = client.DeviceName.ToLowerInvariant().Contains(searchText);
                bool matchId = client.DeviceId.ToLowerInvariant().Contains(searchText);
                bool matchDept = client.Department.ToLowerInvariant().Contains(searchText);
                if (!matchName && !matchId && !matchDept) continue;
            }

            if (statusFilter == "Đang kết nối" && !client.IsOnline) continue;
            if (statusFilter == "Mất kết nối" && client.IsOnline) continue;
            if (statusFilter == "Chờ phê duyệt" && (client.IsApproved || client.IsBlocked)) continue;

            if (deptFilter != "Tất cả phòng ban" && !string.Equals(client.Department, deptFilter, StringComparison.OrdinalIgnoreCase)) continue;

            _filteredClients.Add(client);
        }

        if (TxtClientStats != null)
        {
            TxtClientStats.Text = $"Tổng: {_serverService.ConnectedClients.Count} | Đang kết nối: {onlineCount} | Chờ duyệt: {pendingCount} | Mất kết nối: {offlineCount}";
        }
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
        if (CmbDepartmentFilter != null) CmbDepartmentFilter.SelectedIndex = 0;
        ApplyClientFilters();
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
        SettingsUpdateControl.HasActivePrintJobsCheck = () =>
        {
            if (_serverService != null) return _serverService.PrintJobs.Any(j => j.Status == PrintJobStatus.Printing || j.Status == PrintJobStatus.Pending);
            if (_clientService != null) return _clientService.PrintHistory.Any(j => j.Status == PrintJobStatus.Printing || j.Status == PrintJobStatus.Pending);
            return false;
        };
    }

    // =========================================================
    // TAB BAR BUTTON CLICKS
    // =========================================================

    private void BtnTabGeneral_Click(object sender, RoutedEventArgs e) => SelectSettingsTab(SettingsTab.General);
    private void BtnTabPrinters_Click(object sender, RoutedEventArgs e) => SelectSettingsTab(SettingsTab.Printers);
    private void BtnTabDevices_Click(object sender, RoutedEventArgs e) => SelectSettingsTab(SettingsTab.Devices);
    private void BtnTabQueue_Click(object sender, RoutedEventArgs e) => SelectSettingsTab(SettingsTab.Queue);

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
