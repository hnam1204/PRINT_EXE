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
    public PrinterAccessMode PrinterAccessMode { get; private set; } = PrinterAccessMode.AllShared;

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

        var availablePrinters = sharedPrinters.OrderBy(x => x).ToList();
        CmbAccessMode.Items.Add("Tất cả máy in được chia sẻ");
        CmbAccessMode.Items.Add("Chỉ những máy in đã chọn");
        CmbAccessMode.Items.Add("Không cho phép in");
        CmbAccessMode.SelectedIndex = device.EffectivePrinterPermissionMode switch
        {
            PrinterAccessMode.SelectedOnly => 1,
            PrinterAccessMode.None => 2,
            _ => 0
        };
        TxtAllPrintersHint.Text = availablePrinters.Count == 0
            ? "Chưa có máy in nào được chia sẻ. Vui lòng bật chia sẻ trong Cài đặt → Máy in."
            : "Chọn máy in mà thiết bị được phép sử dụng. Bỏ chọn tất cả để chặn quyền in.";
        LstPrinters.Visibility = availablePrinters.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        foreach (string printer in availablePrinters)
        {
            bool isChecked = device.EffectivePrinterPermissionMode == PrinterAccessMode.AllShared
                || device.AllowedPrinters.Contains(printer, StringComparer.OrdinalIgnoreCase);
            LstPrinters.Items.Add(new WpfCheckBox { Content = printer, IsChecked = isChecked, Padding = new Thickness(6, 4, 4, 4) });
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
        PrinterAccessMode = CmbAccessMode.SelectedIndex switch
        {
            1 when SelectedPrinters.Count > 0 => PrinterAccessMode.SelectedOnly,
            0 => PrinterAccessMode.AllShared,
            _ => PrinterAccessMode.None
        };
        Action = DeviceManageAction.SavePrinters; DialogResult = true;
    }

    private void BtnCancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
