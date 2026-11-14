using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using RPM.App.Services;
using RPM.Core.Config;
using RPM.Core.Models;

namespace RPM.App.Views;

public partial class DeviceManagementDialog : Window
{
    private readonly ServerService _serverService;
    private readonly AppSettings _settings;
    private readonly ObservableCollection<DeviceInfo> _filteredClients = new();

    public DeviceManagementDialog(ServerService serverService, AppSettings settings)
    {
        _serverService = serverService;
        _settings = settings;
        InitializeComponent();

        DgClients.ItemsSource = _filteredClients;
        InitDepartmentFilter();
        ApplyFilters();
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

    private void ApplyFilters()
    {
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

            // Search filter
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

            // Dept filter
            if (deptFilter != "Tất cả phòng ban" && !string.Equals(client.Department, deptFilter, StringComparison.OrdinalIgnoreCase)) continue;

            _filteredClients.Add(client);
        }

        TxtClientStats.Text = $"Tổng: {_serverService.ConnectedClients.Count} thiết bị | Online: {onlineCount} | Chờ duyệt: {pendingCount} | Offline: {offlineCount}";
    }

    private void TxtSearchClient_TextChanged(object sender, TextChangedEventArgs e) => ApplyFilters();
    private void CmbFilter_SelectionChanged(object sender, SelectionChangedEventArgs e) => ApplyFilters();

    private void BtnClearFilters_Click(object sender, RoutedEventArgs e)
    {
        TxtSearchClient.Text = string.Empty;
        if (CmbStatusFilter != null) CmbStatusFilter.SelectedIndex = 0;
        if (CmbDepartmentFilter != null) CmbDepartmentFilter.SelectedIndex = 0;
        ApplyFilters();
    }

    private void BtnRefresh_Click(object sender, RoutedEventArgs e) => ApplyFilters();

    private async void BtnApproveClient_Click(object sender, RoutedEventArgs e)
    {
        if (DgClients.SelectedItem is DeviceInfo client)
        {
            await _serverService.ApproveClientAsync(client);
            ApplyFilters();
            MessageBox.Show($"Đã phê duyệt máy con '{client.DeviceName}'. Máy có thể in ấn ngay bây giờ!", "Phê duyệt thành công", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        else
        {
            MessageBox.Show("Vui lòng chọn một máy con cần phê duyệt.", "Thông báo", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private async void BtnRevokeClient_Click(object sender, RoutedEventArgs e)
    {
        if (DgClients.SelectedItem is DeviceInfo client)
        {
            if (MessageBox.Show($"Bạn có chắc chắn muốn thu hồi quyền in của '{client.DeviceName}'?", "Xác nhận", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
            {
                await _serverService.RevokeClientAsync(client);
                ApplyFilters();
            }
        }
        else
        {
            MessageBox.Show("Vui lòng chọn máy con để thu hồi quyền.", "Thông báo", MessageBoxButton.OK, MessageBoxImage.Warning);
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
            ApplyFilters();
            MessageBox.Show($"Đã chuyển máy con '{client.DeviceName}' sang phòng: '{nextDept}'", "Thành công", MessageBoxButton.OK, MessageBoxImage.Information);
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
            ApplyFilters();
        }
        else
        {
            MessageBox.Show("Vui lòng chọn máy con để phân quyền.", "Thông báo", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void BtnDeleteClient_Click(object sender, RoutedEventArgs e)
    {
        if (DgClients.SelectedItem is DeviceInfo client)
        {
            if (MessageBox.Show($"Bạn có chắc muốn xóa vĩnh viễn thiết bị '{client.DeviceName}' khỏi danh sách?", "Xác nhận xóa", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes)
            {
                _serverService.DeleteClient(client.DeviceId);
                ApplyFilters();
            }
        }
        else
        {
            MessageBox.Show("Vui lòng chọn máy con muốn xóa.", "Thông báo", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void BtnClose_Click(object sender, RoutedEventArgs e) => Close();
}
