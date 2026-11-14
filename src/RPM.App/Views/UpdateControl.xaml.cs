using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Media;
using RPM.App.Services;
using RPM.Core.Config;
using RPM.Core.Models;
using RPM.Core.Update;

namespace RPM.App.Views;

public partial class UpdateControl : UserControl
{
    private AppSettings _settings = null!;
    private UpdateManifest? _availableManifest;
    private readonly ObservableCollection<UpdateHistoryItem> _history = new();
    public Func<bool>? HasActivePrintJobsCheck { get; set; }

    public UpdateControl()
    {
        InitializeComponent();
        DgUpdateHistory.ItemsSource = _history;
        Loaded += UpdateControl_Loaded;
    }

    public void Initialize(AppSettings settings)
    {
        _settings = settings;
        LoadSettingsUI();
    }

    private void UpdateControl_Loaded(object sender, RoutedEventArgs e)
    {
        if (_settings == null)
        {
            _settings = ConfigManager.Load();
        }
        LoadSettingsUI();

        if (_settings.AutoCheckUpdates)
        {
            _ = CheckForUpdatesInternalAsync(silent: true);
        }
    }

    private void LoadSettingsUI()
    {
        TxtCurrentVersion.Text = $"V{_settings.AppVersion}";
        TxtFeedUrl.Text = _settings.UpdateFeedUrl;
        ChkAutoCheck.IsChecked = _settings.AutoCheckUpdates;

        _history.Clear();
        foreach (var h in _settings.UpdateHistory.OrderByDescending(x => x.InstalledAt))
        {
            _history.Add(h);
        }
    }

    private async void BtnCheckUpdate_Click(object sender, RoutedEventArgs e)
    {
        await CheckForUpdatesInternalAsync(silent: false);
    }

    private async Task CheckForUpdatesInternalAsync(bool silent)
    {
        BtnCheckUpdate.IsEnabled = false;
        SetStatusState("Đang kiểm tra...", PrimaryColor, LightBlueColor);

        var result = await UpdateService.CheckForUpdatesAsync(_settings.UpdateFeedUrl, _settings.AppVersion);

        BtnCheckUpdate.IsEnabled = true;

        if (!string.IsNullOrEmpty(result.ErrorMessage))
        {
            SetStatusState("Kiểm tra thất bại", DangerColor, LightRedColor);
            TxtLatestVersion.Text = "Không xác định";
            if (!silent)
            {
                MessageBox.Show($"Không thể kiểm tra bản cập nhật mới:\n{result.ErrorMessage}\n\nVui lòng kiểm tra lại địa chỉ máy chủ cập nhật trong phần cấu hình nâng cao.", "Lỗi cập nhật", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            return;
        }

        TxtLatestVersion.Text = $"V{result.LatestVersion}";
        _availableManifest = result.Manifest;

        if (result.Manifest != null)
        {
            TxtReleaseDate.Text = result.Manifest.ReleaseDate.ToString("dd/MM/yyyy");
            IcChangelog.ItemsSource = result.Manifest.Changelog;
        }

        if (result.HasUpdate)
        {
            SetStatusState($"Có bản mới V{result.LatestVersion}", WarningColor, LightYellowColor);
            BtnUpdateNow.Visibility = Visibility.Visible;
            BtnToggleChangelog.Visibility = Visibility.Visible;
            BorderChangelog.Visibility = Visibility.Visible;

            if (!silent)
            {
                MessageBox.Show($"Đã phát hiện phiên bản mới: V{result.LatestVersion}!\nBạn có thể nhấn nút 'Cập nhật ngay' để nâng cấp phần mềm.", "Có bản cập nhật mới", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }
        else
        {
            SetStatusState("Đã là phiên bản mới nhất", SuccessColor, LightGreenColor);
            BtnUpdateNow.Visibility = Visibility.Collapsed;
            BtnToggleChangelog.Visibility = Visibility.Collapsed;
            BorderChangelog.Visibility = Visibility.Collapsed;

            if (!silent)
            {
                MessageBox.Show($"Phần mềm đang ở phiên bản mới nhất (V{_settings.AppVersion}).\nKhông cần nâng cấp.", "Thông báo", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }
    }

    private async void BtnUpdateNow_Click(object sender, RoutedEventArgs e)
    {
        if (_availableManifest == null) return;

        // 1. Check if there are active print jobs in progress
        if (HasActivePrintJobsCheck != null && HasActivePrintJobsCheck.Invoke())
        {
            var choice = MessageBox.Show(
                "Hệ thống đang có lệnh in tài liệu đang xử lý trong hàng đợi.\n\n" +
                "Bạn có muốn chờ tài liệu in xong rồi mới nâng cấp để tránh làm gián đoạn bản in không?\n\n" +
                "[Yes] Chờ in xong rồi mới nâng cấp.\n" +
                "[No] Nâng cấp ngay bây giờ.",
                "Cảnh báo gián đoạn in ấn",
                MessageBoxButton.YesNoCancel,
                MessageBoxImage.Warning
            );

            if (choice == MessageBoxResult.Yes || choice == MessageBoxResult.Cancel)
            {
                return;
            }
        }

        // 2. Begin download
        BtnUpdateNow.IsEnabled = false;
        BtnCheckUpdate.IsEnabled = false;
        PanelProgress.Visibility = Visibility.Visible;
        SetStatusState("Đang tải bản cập nhật...", PrimaryColor, LightBlueColor);

        var progress = new Progress<int>(percent =>
        {
            PbDownload.Value = percent;
            TxtProgressPercentage.Text = $"{percent}%";
            TxtProgressStatus.Text = $"Đang tải xuống gói nâng cấp... {percent}%";
        });

        var (success, msg, targetFile) = await UpdateService.DownloadUpdateAsync(_availableManifest, progress);

        if (!success)
        {
            PanelProgress.Visibility = Visibility.Collapsed;
            BtnUpdateNow.IsEnabled = true;
            BtnCheckUpdate.IsEnabled = true;
            SetStatusState("Cập nhật thất bại", DangerColor, LightRedColor);
            MessageBox.Show($"Quá trình tải bản nâng cấp thất bại:\n{msg}", "Lỗi tải cập nhật", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        // 3. Apply Update and Restart
        SetStatusState("Đang cài đặt & khởi động lại...", SuccessColor, LightGreenColor);
        TxtProgressStatus.Text = "Đang áp dụng bản cập nhật và khởi động lại phần mềm...";

        // Record history before restarting
        var historyItem = new UpdateHistoryItem
        {
            FromVersion = _settings.AppVersion,
            ToVersion = _availableManifest.Version,
            InstalledAt = DateTime.UtcNow,
            Status = "Thành công",
            Notes = $"Nâng cấp trực tiếp lên V{_availableManifest.Version}"
        };

        _settings.AppVersion = _availableManifest.Version;
        _settings.UpdateHistory.Insert(0, historyItem);
        ConfigManager.Save(_settings);

        await Task.Delay(800);

        bool restarted = UpdateService.ApplyUpdateAndRestart(targetFile, out string err);
        if (!restarted)
        {
            SetStatusState("Cập nhật thất bại", DangerColor, LightRedColor);
            MessageBox.Show($"Không thể tự động áp dụng bản nâng cấp: {err}", "Lỗi áp dụng", MessageBoxButton.OK, MessageBoxImage.Error);
            BtnUpdateNow.IsEnabled = true;
            BtnCheckUpdate.IsEnabled = true;
        }
        else
        {
            Application.Current?.Shutdown();
        }
    }

    private void BtnToggleChangelog_Click(object sender, RoutedEventArgs e)
    {
        BorderChangelog.Visibility = BorderChangelog.Visibility == Visibility.Visible 
            ? Visibility.Collapsed 
            : Visibility.Visible;
    }

    private void ChkAutoCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (_settings != null)
        {
            _settings.AutoCheckUpdates = ChkAutoCheck.IsChecked ?? true;
            ConfigManager.Save(_settings);
        }
    }

    private void BtnSaveFeedUrl_Click(object sender, RoutedEventArgs e)
    {
        if (_settings != null)
        {
            _settings.UpdateFeedUrl = TxtFeedUrl.Text.Trim();
            ConfigManager.Save(_settings);
            MessageBox.Show("Đã lưu địa chỉ máy chủ cập nhật!", "Thông báo", MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }

    private void BtnViewHistory_Click(object sender, RoutedEventArgs e)
    {
        ExpHistory.IsExpanded = !ExpHistory.IsExpanded;
    }

    private void SetStatusState(string text, SolidColorBrush textColor, SolidColorBrush bgColor)
    {
        TxtStatusBadge.Text = text;
        TxtStatusBadge.Foreground = textColor;
        DotUpdateStatus.Fill = textColor;
        BorderStatusBadge.Background = bgColor;
    }

    private static readonly SolidColorBrush PrimaryColor = new(Color.FromArgb(255, 0, 120, 212));
    private static readonly SolidColorBrush SuccessColor = new(Color.FromArgb(255, 16, 124, 65));
    private static readonly SolidColorBrush WarningColor = new(Color.FromArgb(255, 216, 59, 1));
    private static readonly SolidColorBrush DangerColor = new(Color.FromArgb(255, 168, 0, 0));

    private static readonly SolidColorBrush LightBlueColor = new(Color.FromArgb(255, 240, 246, 255));
    private static readonly SolidColorBrush LightGreenColor = new(Color.FromArgb(255, 223, 246, 221));
    private static readonly SolidColorBrush LightYellowColor = new(Color.FromArgb(255, 255, 244, 206));
    private static readonly SolidColorBrush LightRedColor = new(Color.FromArgb(255, 253, 231, 233));
}

