using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using RPM.App.Services;
using RPM.Core.Config;
using RPM.Core.Diagnostics;
using RPM.Core.Models;
using RPM.Core.Security;
using RPM.Printing;

namespace RPM.App.Views;

public enum DashboardSubView
{
    Home,
    Printers,
    Devices,
    Queue,
    Diagnostics,
    Settings
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
    private readonly ObservableCollection<AuditCheckItem> _auditItems = new();

    private DashboardSubView _currentSubView = DashboardSubView.Home;
    private bool _isViewLoaded = false;

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
        DgAuditItems.ItemsSource = _auditItems;

        // Register background sync events
        if (_serverService != null)
        {
            _serverService.OnStatusChanged += (s, e) => Dispatcher.Invoke(UpdateServerUI);
            _serverService.PrintJobs.CollectionChanged += (s, e) => Dispatcher.Invoke(() =>
            {
                RefreshRecentJobs();
                if (_currentSubView == DashboardSubView.Queue) LoadQueueView();
            });
            _serverService.ConnectedClients.CollectionChanged += (s, e) => Dispatcher.Invoke(() =>
            {
                if (_currentSubView == DashboardSubView.Devices) ApplyClientFilters();
                UpdateServerUI();
            });
        }

        if (_clientService != null)
        {
            _clientService.OnStateChanged += (s, e) => Dispatcher.Invoke(UpdateClientUI);
            _clientService.PrintHistory.CollectionChanged += (s, e) => Dispatcher.Invoke(() =>
            {
                RefreshRecentJobs();
                if (_currentSubView == DashboardSubView.Queue) LoadQueueView();
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
            ToolbarServer.Visibility = Visibility.Visible;
            ToolbarClient.Visibility = Visibility.Collapsed;

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

            TxtClientCodeInput.Text = _settings.PairingCode;

            UpdateClientUI();
        }

        RefreshRecentJobs();
        SetActiveView(DashboardSubView.Home);
    }

    // =========================================================
    // IN-PAGE NAVIGATION SYSTEM (SINGLE-WINDOW / ZERO POPUPS)
    // =========================================================

    public void SetActiveView(DashboardSubView view)
    {
        _currentSubView = view;

        // 1. Toggle visibility of sub-views
        ViewHome.Visibility = (view == DashboardSubView.Home) ? Visibility.Visible : Visibility.Collapsed;
        ViewPrinters.Visibility = (view == DashboardSubView.Printers) ? Visibility.Visible : Visibility.Collapsed;
        ViewDevices.Visibility = (view == DashboardSubView.Devices) ? Visibility.Visible : Visibility.Collapsed;
        ViewQueue.Visibility = (view == DashboardSubView.Queue) ? Visibility.Visible : Visibility.Collapsed;
        ViewDiagnostics.Visibility = (view == DashboardSubView.Diagnostics) ? Visibility.Visible : Visibility.Collapsed;
        ViewSettings.Visibility = (view == DashboardSubView.Settings) ? Visibility.Visible : Visibility.Collapsed;

        // 2. Header Back Button
        BtnHeaderBackToHome.Visibility = (view == DashboardSubView.Home) ? Visibility.Collapsed : Visibility.Visible;

        // 3. Update Subtitle
        switch (view)
        {
            case DashboardSubView.Home:
                TxtRoleSubtitle.Text = _settings.Role == AppRole.Server ? "MÁY CHỦ IN ẤN (SERVER)" : "MÁY CON IN TỪ XA (CLIENT)";
                break;
            case DashboardSubView.Printers:
                TxtRoleSubtitle.Text = "QUẢN LÝ VÀ CHIA SẺ MÁY IN";
                break;
            case DashboardSubView.Devices:
                TxtRoleSubtitle.Text = "QUẢN LÝ THIẾT BỊ MÁY CON";
                break;
            case DashboardSubView.Queue:
                TxtRoleSubtitle.Text = "HÀNG ĐỢI LỆNH IN & LỊCH SỬ";
                break;
            case DashboardSubView.Diagnostics:
                TxtRoleSubtitle.Text = "CHẨN ĐOÁN HỆ THỐNG & ĐỘ TƯƠNG THÍCH";
                break;
            case DashboardSubView.Settings:
                TxtRoleSubtitle.Text = "CÀI ĐẶT & NÂNG CẤP PHẦN MỀM";
                break;
        }

        // 4. Update Navigation Toolbar Button Active States
        UpdateToolbarStyles(view);

        // 5. Trigger view-specific data loader
        switch (view)
        {
            case DashboardSubView.Home:
                if (_settings.Role == AppRole.Server) UpdateServerUI();
                else UpdateClientUI();
                RefreshRecentJobs();
                break;
            case DashboardSubView.Printers:
                LoadPrintersView();
                break;
            case DashboardSubView.Devices:
                LoadDevicesView();
                break;
            case DashboardSubView.Queue:
                LoadQueueView();
                break;
            case DashboardSubView.Diagnostics:
                LoadDiagnosticsView();
                break;
            case DashboardSubView.Settings:
                LoadSettingsView();
                break;
        }
    }

    private void UpdateToolbarStyles(DashboardSubView view)
    {
        var primary = (Style)Application.Current.Resources["PrimaryBtn"];
        var secondary = (Style)Application.Current.Resources["SecondaryBtn"];

        if (_settings.Role == AppRole.Server)
        {
            BtnNavHome.Style = (view == DashboardSubView.Home) ? primary : secondary;
            BtnNavPrinters.Style = (view == DashboardSubView.Printers) ? primary : secondary;
            BtnNavDevices.Style = (view == DashboardSubView.Devices) ? primary : secondary;
            BtnNavQueue.Style = (view == DashboardSubView.Queue) ? primary : secondary;
            BtnNavDiagnostics.Style = (view == DashboardSubView.Diagnostics) ? primary : secondary;
            BtnNavSettings.Style = (view == DashboardSubView.Settings) ? primary : secondary;
        }
        else
        {
            BtnNavHomeClient.Style = (view == DashboardSubView.Home) ? primary : secondary;
            BtnNavQueueClient.Style = (view == DashboardSubView.Queue) ? primary : secondary;
            BtnNavDiagnosticsClient.Style = (view == DashboardSubView.Diagnostics) ? primary : secondary;
            BtnNavSettingsClient.Style = (view == DashboardSubView.Settings) ? primary : secondary;
        }
    }

    // =========================================================
    // VIEW 0: HOME LOGIC
    // =========================================================

    private void UpdateServerUI()
    {
        if (_serverService == null) return;

        bool isOnline = _serverService.Relay.IsConnected;
        TxtHeaderStatus.Text = isOnline ? "Máy chủ đang hoạt động (Online)" : "Máy chủ nội bộ (Sẵn sàng)";
        DotStatus.Fill = (SolidColorBrush)Application.Current.Resources["SuccessBrush"];
        PillStatus.Background = (SolidColorBrush)Application.Current.Resources["SuccessLightBrush"];
        TxtHeaderStatus.Foreground = (SolidColorBrush)Application.Current.Resources["SuccessBrush"];

        TxtServerConnectionStatus.Text = isOnline ? "Đã kết nối Relay Broker (WSS)" : "Chạy máy chủ nội bộ trên cổng 9090";

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
    // VIEW 1: PRINTERS LOGIC
    // =========================================================

    private void LoadPrintersView()
    {
        _sharedPrinters.Clear();
        if (_serverService != null)
        {
            _serverService.LoadPrinters();
            foreach (var p in _serverService.SharedPrinters)
            {
                _sharedPrinters.Add(p);
            }
        }
        else
        {
            var printerService = new PrinterService();
            foreach (var p in printerService.GetInstalledPrinters())
            {
                _sharedPrinters.Add(p);
            }
        }
    }

    private void BtnPrintersRescan_Click(object sender, RoutedEventArgs e)
    {
        LoadPrintersView();
        MessageBox.Show($"Đã quét thấy {_sharedPrinters.Count} máy in trên hệ thống Windows.", "Quét máy in", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void BtnPrintersToggleShare_Click(object sender, RoutedEventArgs e)
    {
        if (_serverService == null) return;
        if (DgPrinters.SelectedItem is SharedPrinterInfo selected)
        {
            _serverService.TogglePrinterSharing(selected.Name);
            LoadPrintersView();
            UpdateServerUI();
        }
        else
        {
            MessageBox.Show("Vui lòng chọn một máy in từ danh sách để bật/tắt chia sẻ.", "Thông báo", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void BtnPrintersSetDefault_Click(object sender, RoutedEventArgs e)
    {
        if (_serverService == null) return;
        if (DgPrinters.SelectedItem is SharedPrinterInfo selected)
        {
            _serverService.SetDefaultPrinter(selected.Name);
            LoadPrintersView();
            UpdateServerUI();
            MessageBox.Show($"Đã đặt '{selected.Name}' làm máy in chính để chia sẻ qua mạng!", "Thành công", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        else
        {
            MessageBox.Show("Vui lòng chọn một máy in để đặt làm máy in chính.", "Thông báo", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void BtnPrintersTestPrint_Click(object sender, RoutedEventArgs e)
    {
        if (DgPrinters.SelectedItem is SharedPrinterInfo selected)
        {
            var printerService = _serverService?.PrinterService ?? new PrinterService();
            bool ok = printerService.PrintTestPage(selected.Name, "Máy Cục Bộ", Environment.MachineName, out string err);
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

    // =========================================================
    // VIEW 2: DEVICES LOGIC
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
        string statusFilter = (CmbStatusFilter?.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "Tất cả trạng thái";
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

            if (statusFilter == "Trực tuyến (Online)" && !client.IsOnline) continue;
            if (statusFilter == "Ngoại tuyến (Offline)" && client.IsOnline) continue;
            if (statusFilter == "Chờ phê duyệt" && (client.IsApproved || client.IsBlocked)) continue;

            if (deptFilter != "Tất cả phòng ban" && !string.Equals(client.Department, deptFilter, StringComparison.OrdinalIgnoreCase)) continue;

            _filteredClients.Add(client);
        }

        if (TxtClientStats != null)
        {
            TxtClientStats.Text = $"Tổng: {_serverService.ConnectedClients.Count} thiết bị | Online: {onlineCount} | Chờ duyệt: {pendingCount} | Offline: {offlineCount}";
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
            MessageBox.Show($"Đã phê duyệt máy con '{client.DeviceName}'. Máy có thể in ấn ngay bây giờ!", "Phê duyệt thành công", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        else
        {
            MessageBox.Show("Vui lòng chọn máy con từ danh sách để phê duyệt.", "Thông báo", MessageBoxButton.OK, MessageBoxImage.Warning);
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
            MessageBox.Show($"Đã chuyển máy con '{client.DeviceName}' sang phòng: '{nextDept}'.", "Đổi phòng ban", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        else
        {
            MessageBox.Show("Vui lòng chọn máy con muốn đổi phòng ban.", "Thông báo", MessageBoxButton.OK, MessageBoxImage.Warning);
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
                MessageBox.Show("Chưa có máy in nào đang được chia sẻ.", "Thông báo", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (client.AllowedPrinters.Count == 0)
            {
                _serverService.SetClientAllowedPrinters(client.DeviceId, new List<string> { sharedPrinters[0] });
                MessageBox.Show($"Đã phân quyền cho '{client.DeviceName}': CHỈ ĐƯỢC IN TRÊN '{sharedPrinters[0]}'.", "Phân quyền", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            else
            {
                _serverService.SetClientAllowedPrinters(client.DeviceId, new List<string>());
                MessageBox.Show($"Đã phân quyền cho '{client.DeviceName}': ĐƯỢC PHÉP DÙNG TẤT CẢ MÁY IN CHIA SẺ.", "Phân quyền", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            ApplyClientFilters();
        }
        else
        {
            MessageBox.Show("Vui lòng chọn máy con để phân quyền máy in.", "Thông báo", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private async void BtnRevokeClient_Click(object sender, RoutedEventArgs e)
    {
        if (_serverService == null) return;
        if (DgClients.SelectedItem is DeviceInfo client)
        {
            if (MessageBox.Show($"Bạn có chắc chắn muốn thu hồi quyền truy cập của máy '{client.DeviceName}'?", "Xác nhận", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
            {
                await _serverService.RevokeClientAsync(client);
                ApplyClientFilters();
                UpdateServerUI();
            }
        }
        else
        {
            MessageBox.Show("Vui lòng chọn máy con để thu hồi quyền.", "Thông báo", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void BtnDeleteClient_Click(object sender, RoutedEventArgs e)
    {
        if (_serverService == null) return;
        if (DgClients.SelectedItem is DeviceInfo client)
        {
            if (MessageBox.Show($"Bạn có chắc chắn muốn xóa máy '{client.DeviceName}' khỏi hệ thống?", "Xác nhận xóa", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes)
            {
                _serverService.DeleteClient(client.DeviceId);
                ApplyClientFilters();
                UpdateServerUI();
            }
        }
        else
        {
            MessageBox.Show("Vui lòng chọn máy con cần xóa.", "Thông báo", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    // =========================================================
    // VIEW 3: QUEUE LOGIC
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

    private void BtnRestartSpooler_Click(object sender, RoutedEventArgs e)
    {
        bool restarted = CompatibilityChecker.RestartSpoolerService(out string err);
        if (restarted)
        {
            MessageBox.Show("Đã khởi động lại dịch vụ Windows Print Spooler thành công! Hàng đợi in ấn đã được giải phóng.", "Thành công", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        else
        {
            MessageBox.Show($"Không thể tự động khởi động dịch vụ Spooler: {err}\nVui lòng chạy ứng dụng với quyền 'Run as administrator'.", "Thông báo", MessageBoxButton.OK, MessageBoxImage.Warning);
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
    // VIEW 4: DIAGNOSTICS LOGIC
    // =========================================================

    private void LoadDiagnosticsView()
    {
        List<SharedPrinterInfo>? printers = null;
        try
        {
            var printerService = _serverService?.PrinterService ?? new PrinterService();
            printers = printerService.GetInstalledPrinters();
        }
        catch { }

        var audit = LiveSystemAuditor.RunAudit(_settings, printers);
        _auditItems.Clear();
        foreach (var item in audit.Items)
        {
            _auditItems.Add(item);
        }

        TxtDiagScore.Text = $"{audit.VerifiedOperationalPercent:F1}% HOẠT ĐỘNG";
        TxtDiagSummary.Text = $"{audit.PassCount} PASS • {audit.BlockedCount} BLOCKED • {audit.RealDataCount + audit.CachedDataCount} REAL";
        TxtDiagSpecs.Text = $"Máy tính: {audit.MachineName} • {audit.OperatingSystem}";
    }

    private void BtnRerunDiagnostics_Click(object sender, RoutedEventArgs e)
    {
        LoadDiagnosticsView();
        MessageBox.Show("Đã hoàn tất kiểm toán hệ thống trên máy thật!", "Chẩn đoán", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    // =========================================================
    // VIEW 5: SETTINGS LOGIC
    // =========================================================

    private void LoadSettingsView()
    {
        TxtSettingsDeviceName.Text = _settings.DeviceName;
        TxtSettingsPairingCode.Text = _settings.PairingCode;
        TxtSettingsRelayUrl.Text = _settings.RelayServerUrl;
        ChkSettingsAutoStart.IsChecked = _settings.AutoStartWithWindows;
        ChkSettingsMinimizeTray.IsChecked = _settings.MinimizeToSystemTray;
        ChkSettingsNotifications.IsChecked = _settings.ShowDesktopNotifications;

        TxtSwitchRoleBtn.Text = _settings.Role == AppRole.Server ? "⇄ Đổi sang Chế độ Máy Con" : "⇄ Đổi sang Chế độ Máy Chủ";

        SettingsUpdateControl.Initialize(_settings);
        SettingsUpdateControl.HasActivePrintJobsCheck = () =>
        {
            if (_serverService != null) return _serverService.PrintJobs.Any(j => j.Status == PrintJobStatus.Printing || j.Status == PrintJobStatus.Pending);
            if (_clientService != null) return _clientService.PrintHistory.Any(j => j.Status == PrintJobStatus.Printing || j.Status == PrintJobStatus.Pending);
            return false;
        };
    }

    private void BtnSettingsRegenerateCode_Click(object sender, RoutedEventArgs e)
    {
        _settings.PairingCode = RPMCrypto.GeneratePairingCode();
        TxtSettingsPairingCode.Text = _settings.PairingCode;
        MessageBox.Show($"Đã sinh mã ghép đôi mới: '{_settings.PairingCode}'.\nNhớ nhấn 'Lưu cấu hình cài đặt' để áp dụng.", "Mã mới", MessageBoxButton.OK, MessageBoxImage.Information);
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
            TxtServerHostName.Text = _settings.DeviceName;
            TxtServerPairingCode.Text = _settings.PairingCode;
            UpdateServerUI();
        }
        else
        {
            TxtClientCodeInput.Text = _settings.PairingCode;
            UpdateClientUI();
        }

        MessageBox.Show("Đã lưu toàn bộ cấu hình thiết lập thành công!", "Lưu thành công", MessageBoxButton.OK, MessageBoxImage.Information);
        SetActiveView(DashboardSubView.Home);
    }

    private void BtnSwitchRole_Click(object sender, RoutedEventArgs e)
    {
        var targetRole = _settings.Role == AppRole.Server ? AppRole.Client : AppRole.Server;
        string targetRoleName = targetRole == AppRole.Server ? "MÁY CHỦ" : "MÁY CON";

        var confirm = MessageBox.Show(
            $"Bạn có chắc chắn muốn chuyển đổi vai trò phần mềm sang [{targetRoleName}]?\n\nỨng dụng sẽ áp dụng vai trò mới và bảo toàn 100% cấu hình hiện tại.",
            "Xác nhận đổi vai trò",
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
    // GLOBAL BUTTON HANDLERS
    // =========================================================

    private void BtnBackToHome_Click(object sender, RoutedEventArgs e) => SetActiveView(DashboardSubView.Home);
    private void BtnNavHome_Click(object sender, RoutedEventArgs e) => SetActiveView(DashboardSubView.Home);
    private void BtnNavPrinters_Click(object sender, RoutedEventArgs e) => SetActiveView(DashboardSubView.Printers);
    private void BtnNavDevices_Click(object sender, RoutedEventArgs e) => SetActiveView(DashboardSubView.Devices);
    private void BtnNavQueue_Click(object sender, RoutedEventArgs e) => SetActiveView(DashboardSubView.Queue);
    private void BtnNavDiagnostics_Click(object sender, RoutedEventArgs e) => SetActiveView(DashboardSubView.Diagnostics);
    private void BtnNavSettings_Click(object sender, RoutedEventArgs e) => SetActiveView(DashboardSubView.Settings);

    private void BtnOpenPrinterDialog_Click(object sender, RoutedEventArgs e) => SetActiveView(DashboardSubView.Printers);
    private void BtnOpenQueueDialog_Click(object sender, RoutedEventArgs e) => SetActiveView(DashboardSubView.Queue);
    private void BtnOpenSettings_Click(object sender, RoutedEventArgs e) => SetActiveView(DashboardSubView.Settings);

    private void BtnMinimize_Click(object sender, RoutedEventArgs e) => OnMinimizeRequested?.Invoke(this, EventArgs.Empty);

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
            if (MessageBox.Show("Bạn có muốn ngắt kết nối với máy chủ hiện tại?", "Xác nhận", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
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
}
