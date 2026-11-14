using System.Windows;
using RPM.Core.Config;
using RPM.Core.Models;

namespace RPM.App.Views;

public partial class SettingsDialog : Window
{
    private readonly AppSettings _settings;
    public event EventHandler? OnRoleChanged;
    public bool SettingsSaved { get; private set; }

    public SettingsDialog(AppSettings settings, Func<bool>? hasActivePrintJobsCheck = null)
    {
        _settings = settings;
        InitializeComponent();

        LoadSettings();
        DialogUpdateControl.Initialize(_settings);
        if (hasActivePrintJobsCheck != null)
        {
            DialogUpdateControl.HasActivePrintJobsCheck = hasActivePrintJobsCheck;
        }
    }

    private void LoadSettings()
    {
        TxtDeviceName.Text = _settings.DeviceName;
        TxtPairingCode.Text = _settings.PairingCode;
        TxtRelayUrl.Text = _settings.RelayServerUrl;
        ChkAutoStart.IsChecked = _settings.AutoStartWithWindows;
        ChkMinimizeTray.IsChecked = _settings.MinimizeToSystemTray;
        ChkNotifications.IsChecked = _settings.ShowDesktopNotifications;

        if (_settings.Role == AppRole.Server)
        {
            TxtRoleBadge.Text = "Chế độ: Máy Chủ";
            TxtSwitchRoleBtn.Text = "⇄ Đổi sang Chế độ Máy Con";
        }
        else
        {
            TxtRoleBadge.Text = "Chế độ: Máy Con";
            TxtSwitchRoleBtn.Text = "⇄ Đổi sang Chế độ Máy Chủ";
        }
    }

    private void BtnRegeneratePairingCode_Click(object sender, RoutedEventArgs e)
    {
        _settings.PairingCode = RPM.Core.Security.RPMCrypto.GeneratePairingCode();
        TxtPairingCode.Text = _settings.PairingCode;
        MessageBox.Show($"Đã sinh mã ghép đôi mới: '{_settings.PairingCode}'.\nNhớ nhấn 'Lưu cài đặt' để áp dụng.", "Thông báo", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void BtnSaveSettings_Click(object sender, RoutedEventArgs e)
    {
        _settings.DeviceName = TxtDeviceName.Text.Trim();
        _settings.RelayServerUrl = TxtRelayUrl.Text.Trim();
        _settings.AutoStartWithWindows = ChkAutoStart.IsChecked == true;
        _settings.MinimizeToSystemTray = ChkMinimizeTray.IsChecked == true;
        _settings.ShowDesktopNotifications = ChkNotifications.IsChecked == true;

        ConfigManager.Save(_settings);
        SettingsSaved = true;
        MessageBox.Show("Đã lưu toàn bộ thiết lập cài đặt thành công!", "Lưu thành công", MessageBoxButton.OK, MessageBoxImage.Information);
        Close();
    }

    private void BtnSwitchRole_Click(object sender, RoutedEventArgs e)
    {
        var targetRole = _settings.Role == AppRole.Server ? AppRole.Client : AppRole.Server;
        string targetRoleName = targetRole == AppRole.Server ? "MÁY CHỦ" : "MÁY CON";

        var confirm = MessageBox.Show(
            $"Bạn có chắc chắn muốn chuyển đổi vai trò phần mềm sang [{targetRoleName}]?\n\nỨng dụng sẽ khởi động lại chế độ làm việc tương ứng và bảo toàn toàn bộ cấu hình hiện tại.",
            "Xác nhận đổi vai trò",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question
        );

        if (confirm == MessageBoxResult.Yes)
        {
            _settings.Role = targetRole;
            ConfigManager.Save(_settings);
            OnRoleChanged?.Invoke(this, EventArgs.Empty);
            Close();
        }
    }

    private void BtnClose_Click(object sender, RoutedEventArgs e) => Close();
}
