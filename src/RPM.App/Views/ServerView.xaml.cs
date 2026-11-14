using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
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
    private readonly ObservableCollection<DeviceInfo> _filteredClients = new();
    private readonly ObservableCollection<string> _logs = new();

    public event EventHandler? OnSwitchToClientModeRequested;
    public event EventHandler? OnMinimizeRequested;

    public ServerView(ServerService serverService, AppSettings settings)
    {
        _serverService = serverService;
        _settings = settings;
        InitializeComponent();

        DgPrinters.ItemsSource = _printers;
        DgClients.ItemsSource = _filteredClients;
        DgQueue.ItemsSource = _serverService.PrintJobs;
        LstActivityLog.ItemsSource = _logs;

        _serverService.OnLog += ServerService_OnLog;
        _serverService.OnStatusChanged += (s, e) => Dispatcher.Invoke(() =>
        {
            ApplyClientFilters();
            UpdateDashboardUI();
        });

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

        InitDepartmentFilter();
        RefreshPrinters();
        ApplyClientFilters();
        UpdateDashboardUI();

        ServerUpdateControl.Initialize(_settings);
        ServerUpdateControl.HasActivePrintJobsCheck = () => 
            _serverService.PrintJobs.Any(j => j.Status == PrintJobStatus.Printing || j.Status == PrintJobStatus.Pending);
    }

    private void InitDepartmentFilter()
    {
        CmbDepartmentFilter.Items.Clear();
        CmbDepartmentFilter.Items.Add(new ComboBoxItem { Content = "Tất cả phòng ban", IsSelected = true });
        foreach (var dept in _settings.Departments)
        {
            CmbDepartmentFilter.Items.Add(new ComboBoxItem { Content = dept });
        }
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
        if (_serverService == null || TxtOverviewPrinterName == null) return;
        int sharedCount = _serverService.SharedPrinters.Count(p => p.IsShared);
        if (_serverService.ActiveSharedPrinter != null)
        {
            TxtOverviewPrinterName.Text = sharedCount > 1 
                ? $"{_serverService.ActiveSharedPrinter.Name} (+{sharedCount - 1} máy in khác)" 
                : _serverService.ActiveSharedPrinter.Name;

            TxtOverviewPrinterStatus.Text = _serverService.ActiveSharedPrinter.IsShared 
                ? $"Đang chia sẻ {sharedCount} máy in trực tuyến" 
                : "Trạng thái: Tạm dừng chia sẻ";
        }
        else
        {
            TxtOverviewPrinterName.Text = "Chưa chọn máy in";
            TxtOverviewPrinterStatus.Text = "Trạng thái: Đang chờ thiết lập trong tab Máy In";
        }

        int onlineClients = _serverService.ConnectedClients.Count(c => c.IsOnline && c.IsApproved);
        int totalApproved = _serverService.ConnectedClients.Count(c => c.IsApproved && !c.IsBlocked);
        int pendingClients = _serverService.ConnectedClients.Count(c => !c.IsApproved && !c.IsBlocked);
        int offlineClients = _serverService.ConnectedClients.Count(c => !c.IsOnline);

        TxtOverviewClientsCount.Text = $"{onlineClients} / {totalApproved} Trực tuyến";
        TxtOverviewPendingClients.Text = $"{pendingClients} máy con chờ phê duyệt | {offlineClients} ngoại tuyến";

        int completedJobs = _serverService.PrintJobs.Count(j => j.Status == PrintJobStatus.Completed);
        int pendingJobs = _serverService.PrintJobs.Count(j => j.Status == PrintJobStatus.Pending || j.Status == PrintJobStatus.Printing);
        TxtOverviewJobsCount.Text = $"{completedJobs} Hoàn tất";
        TxtOverviewQueueStatus.Text = $"Hàng đợi: {pendingJobs} đang chờ xử lý";

        TxtRelayStatus.Text = _serverService.Relay.IsConnected ? "Đã kết nối ổn định (WSS)" : "Máy chủ nội bộ (Sẵn sàng)";

        TxtClientsStats.Text = $"Tổng: {_serverService.ConnectedClients.Count} thiết bị | Online: {onlineClients} | Chờ duyệt: {pendingClients} | Offline: {offlineClients}";
    }

    private void ApplyClientFilters()
    {
        if (_serverService == null || _filteredClients == null) return;
        string searchText = TxtSearchClient?.Text?.Trim().ToLowerInvariant() ?? string.Empty;
        string statusFilter = (CmbStatusFilter?.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "Tất cả trạng thái";
        string deptFilter = (CmbDepartmentFilter?.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "Tất cả phòng ban";

        _filteredClients.Clear();
        foreach (var client in _serverService.ConnectedClients)
        {
            // Search text filter
            if (!string.IsNullOrEmpty(searchText))
            {
                bool matchName = client.DeviceName.ToLowerInvariant().Contains(searchText);
                bool matchId = client.DeviceId.ToLowerInvariant().Contains(searchText);
                bool matchDept = client.Department.ToLowerInvariant().Contains(searchText);
                if (!matchName && !matchId && !matchDept) continue;
            }

            // Status filter
            if (statusFilter == "Trực tuyến (Online)" && !client.IsOnline) continue;
            if (statusFilter == "Ngoại tuyến (Offline)" && client.IsOnline) continue;
            if (statusFilter == "Chờ phê duyệt" && (client.IsApproved || client.IsBlocked)) continue;

            // Department filter
            if (deptFilter != "Tất cả phòng ban" && !string.Equals(client.Department, deptFilter, StringComparison.OrdinalIgnoreCase)) continue;

            _filteredClients.Add(client);
        }
    }

    private void TxtSearchClient_TextChanged(object sender, TextChangedEventArgs e)
    {
        ApplyClientFilters();
    }

    private void CmbFilter_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        ApplyClientFilters();
    }

    private void BtnClearFilters_Click(object sender, RoutedEventArgs e)
    {
        TxtSearchClient.Text = string.Empty;
        if (CmbStatusFilter != null) CmbStatusFilter.SelectedIndex = 0;
        if (CmbDepartmentFilter != null) CmbDepartmentFilter.SelectedIndex = 0;
        ApplyClientFilters();
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

    private void BtnCopyPairingCode_Click(object sender, RoutedEventArgs e)
    {
        Clipboard.SetText(_settings.PairingCode);
        MessageBox.Show($"Đã sao chép mã ghép đôi '{_settings.PairingCode}' vào bộ nhớ tạm!\nBạn hãy gửi mã này cho các máy con để kết nối.", "Sao chép thành công", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void BtnRescanPrinters_Click(object sender, RoutedEventArgs e)
    {
        RefreshPrinters();
        MessageBox.Show($"Đã quét thấy {_printers.Count} máy in trên máy tính.", "Quét máy in", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void BtnToggleSharedPrinter_Click(object sender, RoutedEventArgs e)
    {
        if (DgPrinters.SelectedItem is SharedPrinterInfo selected)
        {
            _serverService.TogglePrinterSharing(selected.Name);
            RefreshPrinters();
            UpdateDashboardUI();
        }
        else
        {
            MessageBox.Show("Vui lòng chọn một máy in từ danh sách trước.", "Chưa chọn máy in", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void BtnSetDefaultPrinter_Click(object sender, RoutedEventArgs e)
    {
        if (DgPrinters.SelectedItem is SharedPrinterInfo selected)
        {
            _serverService.SetDefaultPrinter(selected.Name);
            RefreshPrinters();
            UpdateDashboardUI();
            MessageBox.Show($"Đã chọn '{selected.Name}' làm máy in chính mặc định!", "Thành công", MessageBoxButton.OK, MessageBoxImage.Information);
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
        foreach (var p in _serverService.SharedPrinters.Where(p => p.IsShared).ToList())
        {
            _serverService.TogglePrinterSharing(p.Name, false);
        }
        RefreshPrinters();
        UpdateDashboardUI();

        ServerUpdateControl.Initialize(_settings);
        ServerUpdateControl.HasActivePrintJobsCheck = () => 
            _serverService.PrintJobs.Any(j => j.Status == PrintJobStatus.Printing || j.Status == PrintJobStatus.Pending);
    }

    private async void BtnApproveClient_Click(object sender, RoutedEventArgs e)
    {
        if (DgClients.SelectedItem is DeviceInfo client)
        {
            await _serverService.ApproveClientAsync(client);
            ApplyClientFilters();
            UpdateDashboardUI();
            MessageBox.Show($"Đã phê duyệt máy con '{client.DeviceName}' (Phòng: {client.Department}).\nMáy con có thể in tài liệu ngay bây giờ!", "Phê duyệt thành công", MessageBoxButton.OK, MessageBoxImage.Information);
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
                ApplyClientFilters();
                UpdateDashboardUI();
            }
        }
        else
        {
            MessageBox.Show("Vui lòng chọn máy con muốn thu hồi quyền.", "Thông báo", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void BtnChangeDepartment_Click(object sender, RoutedEventArgs e)
    {
        if (DgClients.SelectedItem is DeviceInfo client)
        {
            var depts = _settings.Departments;
            int currentIndex = depts.IndexOf(client.Department);
            int nextIndex = (currentIndex + 1) % depts.Count;
            string nextDept = depts[nextIndex];

            _serverService.SetClientDepartment(client.DeviceId, nextDept);
            ApplyClientFilters();
            UpdateDashboardUI();
            MessageBox.Show($"Đã chuyển máy con '{client.DeviceName}' sang phòng ban: '{nextDept}'", "Cập nhật phòng ban", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        else
        {
            MessageBox.Show("Vui lòng chọn máy con muốn đổi phòng ban.", "Thông báo", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void BtnAssignPrinters_Click(object sender, RoutedEventArgs e)
    {
        if (DgClients.SelectedItem is DeviceInfo client)
        {
            var sharedPrinters = _serverService.SharedPrinters.Where(p => p.IsShared).Select(p => p.Name).ToList();
            if (sharedPrinters.Count == 0)
            {
                MessageBox.Show("Chưa có máy in nào đang được chia sẻ. Vui lòng vào tab Máy In để bật chia sẻ trước.", "Thông báo", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            // Cycle between All Printers -> First Printer -> Specific combination
            if (client.AllowedPrinters.Count == 0)
            {
                // Restrict to first shared printer
                _serverService.SetClientAllowedPrinters(client.DeviceId, new List<string> { sharedPrinters[0] });
                MessageBox.Show($"Đã phân quyền cho máy '{client.DeviceName}': CHỈ ĐƯỢC DÙNG '{sharedPrinters[0]}'.", "Phân quyền máy in", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            else
            {
                // Reset to all shared printers
                _serverService.SetClientAllowedPrinters(client.DeviceId, new List<string>());
                MessageBox.Show($"Đã phân quyền cho máy '{client.DeviceName}': ĐƯỢC DÙNG TẤT CẢ CÁC MÁY IN CHIA SẺ.", "Phân quyền máy in", MessageBoxButton.OK, MessageBoxImage.Information);
            }

            ApplyClientFilters();
            UpdateDashboardUI();
        }
        else
        {
            MessageBox.Show("Vui lòng chọn máy con muốn phân quyền máy in.", "Thông báo", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void BtnAddPreapprovedClient_Click(object sender, RoutedEventArgs e)
    {
        int nextNum = _serverService.ConnectedClients.Count + 1;
        string defaultName = $"Máy con {nextNum:D2}";
        string dept = _settings.Departments.FirstOrDefault() ?? "Văn phòng";

        _serverService.AddPreapprovedClient(defaultName, dept);
        ApplyClientFilters();
        UpdateDashboardUI();
        MessageBox.Show($"Đã đăng ký trước thiết bị '{defaultName}' thuộc phòng '{dept}'.\nKhi máy tính mới nhập mã ghép đôi {_settings.PairingCode}, máy con sẽ tự động được cấp quyền ngay lập tức!", "Đăng ký thành công", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void BtnDeleteClient_Click(object sender, RoutedEventArgs e)
    {
        if (DgClients.SelectedItem is DeviceInfo client)
        {
            if (MessageBox.Show($"Bạn có chắc chắn muốn xóa máy con '{client.DeviceName}' ({client.DeviceId}) khỏi danh sách quản lý?", "Xác nhận xóa", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes)
            {
                _serverService.DeleteClient(client.DeviceId);
                ApplyClientFilters();
                UpdateDashboardUI();
            }
        }
        else
        {
            MessageBox.Show("Vui lòng chọn máy con muốn xóa.", "Thông báo", MessageBoxButton.OK, MessageBoxImage.Warning);
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

    private void BtnOpenDiagnostics_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new CompatibilityDialog(_settings)
        {
            Owner = Window.GetWindow(this)
        };
        dlg.ShowDialog();
    }
}
