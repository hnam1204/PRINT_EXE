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
        TxtDeviceDetails.Text = $"ID: {device.DeviceId}\nPhê duyệt: {(device.IsApproved ? "Đã phê duyệt" : device.IsBlocked ? "Đã từ chối" : "Chờ phê duyệt")}";
        TxtDeviceStatus.Text = device.DeviceStatus;
        TxtLastSeen.Text = $"Hoạt động gần nhất: {device.LastSeen.ToLocalTime():dd/MM/yyyy HH:mm:ss}";
        BtnApprove.Visibility = device.IsApproved || device.IsBlocked ? Visibility.Collapsed : Visibility.Visible;
        BtnRevoke.Visibility = device.IsApproved && !device.IsBlocked ? Visibility.Visible : Visibility.Collapsed;

        foreach (string printer in sharedPrinters.OrderBy(x => x))
        {
            LstPrinters.Items.Add(new WpfCheckBox { Content = printer, IsChecked = device.AllowedPrinters.Contains(printer, StringComparer.OrdinalIgnoreCase), Padding = new Thickness(6, 4, 4, 4) });
        }
    }

    private void BtnApprove_Click(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show($"Phê duyệt thiết bị '{_device.DeviceName}'?", "Xác nhận", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        Action = DeviceManageAction.Approve; DialogResult = true;
    }

    private void BtnRevoke_Click(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show($"Thu hồi quyền của '{_device.DeviceName}'?", "Xác nhận", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        Action = DeviceManageAction.Revoke; DialogResult = true;
    }

    private void BtnSave_Click(object sender, RoutedEventArgs e)
    {
        SelectedPrinters.AddRange(LstPrinters.Items.OfType<WpfCheckBox>().Where(x => x.IsChecked == true).Select(x => x.Content?.ToString()).OfType<string>());
        Action = DeviceManageAction.SavePrinters; DialogResult = true;
    }

    private void BtnCancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
