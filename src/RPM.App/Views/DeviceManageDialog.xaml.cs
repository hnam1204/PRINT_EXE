using System.Windows;
using System.Windows.Controls;
using RPM.Core.Models;
using WpfCheckBox = System.Windows.Controls.CheckBox;

namespace RPM.App.Views;

public enum DeviceManageAction
{
    None,
    Approve,
    Revoke,
    AllowReconnect,
    SavePrinters
}

public partial class DeviceManageDialog : Window
{
    private readonly DeviceInfo _device;
    public DeviceManageAction Action { get; private set; }
    public List<string> SelectedPrinters { get; } = new();

    public DeviceManageDialog(DeviceInfo device, IEnumerable<string> sharedPrinters)
    {
        InitializeComponent();
        _device = device;
        TxtDeviceName.Text = device.DeviceName;
        TxtDeviceDetails.Text = $"Mã thiết bị: {device.DeviceId}";
        TxtDeviceStatus.Text = $"{device.PermissionStatus} • {device.ConnectionStatus}";
        TxtLastSeen.Text = $"Hoạt động gần nhất: {device.LastSeen.ToLocalTime():dd/MM/yyyy HH:mm:ss}";
        ReconnectPanel.Visibility = device.IsBlocked || device.ReconnectAllowed ? Visibility.Visible : Visibility.Collapsed;
        BtnApprove.Visibility = device.IsApproved || device.IsBlocked || device.ReconnectAllowed ? Visibility.Collapsed : Visibility.Visible;
        BtnRevoke.Visibility = device.IsApproved && !device.IsBlocked ? Visibility.Visible : Visibility.Collapsed;
        BtnAllowReconnect.Visibility = device.IsBlocked ? Visibility.Visible : Visibility.Collapsed;
        BtnSave.IsEnabled = device.IsApproved;
        if (device.ReconnectAllowed)
        {
            TxtReconnectStatus.Text = "Đã cho phép máy gửi yêu cầu kết nối mới. Máy con cần nhập mã kết nối hiện tại và chờ bạn chấp nhận.";
        }

        foreach (string printer in sharedPrinters.OrderBy(x => x))
        {
            LstPrinters.Items.Add(new WpfCheckBox { Content = printer, IsChecked = device.AllowedPrinters.Contains(printer, StringComparer.OrdinalIgnoreCase), Padding = new Thickness(6, 4, 4, 4) });
        }
    }

    private void BtnApprove_Click(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show($"Chấp nhận kết nối của máy '{_device.DeviceName}'?", "Chấp nhận kết nối", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        Action = DeviceManageAction.Approve; DialogResult = true;
    }

    private void BtnRevoke_Click(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show($"Thu hồi quyền kết nối của máy '{_device.DeviceName}'?", "Thu hồi quyền kết nối", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        Action = DeviceManageAction.Revoke; DialogResult = true;
    }

    private void BtnAllowReconnect_Click(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show(
                $"Cho phép máy '{_device.DeviceName}' gửi yêu cầu kết nối mới?\n\nMáy con vẫn phải nhập mã kết nối hiện tại và chờ bạn chấp nhận.",
                "Cho phép kết nối lại",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question) != MessageBoxResult.Yes) return;

        Action = DeviceManageAction.AllowReconnect;
        DialogResult = true;
    }

    private void BtnSave_Click(object sender, RoutedEventArgs e)
    {
        SelectedPrinters.AddRange(LstPrinters.Items.OfType<WpfCheckBox>().Where(x => x.IsChecked == true).Select(x => x.Content?.ToString()).OfType<string>());
        Action = DeviceManageAction.SavePrinters; DialogResult = true;
    }

    private void BtnCancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
