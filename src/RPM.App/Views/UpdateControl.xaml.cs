using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using RPM.Core.Config;
using RPM.Core.Models;
using RPM.Core.Update;

namespace RPM.App.Views;

public partial class UpdateControl : UserControl
{
    private AppSettings _settings = null!;
    private UpdateManifest? _availableManifest;
    private readonly ObservableCollection<UpdateHistoryItem> _history = new();
    private readonly DispatcherTimer _periodicTimer = new();
    
    public Func<bool>? HasActivePrintJobsCheck { get; set; }

    public UpdateControl()
    {
        InitializeComponent();
        DgUpdateHistory.ItemsSource = _history;
        Loaded += UpdateControl_Loaded;

        _periodicTimer.Tick += PeriodicTimer_Tick;
    }

    public void Initialize(AppSettings settings)
    {
        _settings = settings;
        LoadSettingsUI();
        ConfigurePeriodicTimer();
    }

    private void UpdateControl_Loaded(object sender, RoutedEventArgs e)
    {
        if (_settings == null)
        {
            _settings = ConfigManager.Load();
        }
        LoadSettingsUI();
        ConfigurePeriodicTimer();

        if (_settings.AutoCheckUpdates)
        {
            _ = CheckForUpdatesInternalAsync(silent: true);
        }
    }

    private void ConfigurePeriodicTimer()
    {
        _periodicTimer.Stop();
        if (_settings != null && _settings.AutoCheckUpdates)
        {
            int interval = _settings.CheckIntervalMinutes > 0 ? _settings.CheckIntervalMinutes : 60;
            _periodicTimer.Interval = TimeSpan.FromMinutes(interval);
            _periodicTimer.Start();
        }
    }

    private async void PeriodicTimer_Tick(object? sender, EventArgs e)
    {
        if (_settings != null && _settings.AutoCheckUpdates)
        {
            await CheckForUpdatesInternalAsync(silent: true);
        }
    }

    private void LoadSettingsUI()
    {
        TxtCurrentVersion.Text = $"V{_settings.AppVersion}";
        TxtCurrentChannel.Text = _settings.UpdateChannel == "Beta" ? "Thử nghiệm (Beta)" : "Chính thức (Stable)";
        TxtFeedUrl.Text = _settings.UpdateFeedUrl;
        ChkAutoCheck.IsChecked = _settings.AutoCheckUpdates;
        ChkAutoInstall.IsChecked = _settings.AutoInstallUpdates;

        // Channel ComboBox
        if (CmbChannel != null)
        {
            CmbChannel.SelectedIndex = string.Equals(_settings.UpdateChannel, "Beta", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
        }

        // Check if rollback backup exists
        bool canRollback = UpdateService.CanRollback();
        BtnRollback.Visibility = canRollback ? Visibility.Visible : Visibility.Collapsed;

        // History list
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
        SetStatusState("Đang kiểm tra...", PrimaryColor, LightPrimaryColor);

        var result = await UpdateService.CheckForUpdatesAsync(_settings.UpdateFeedUrl, _settings.AppVersion, _settings.UpdateChannel);

        BtnCheckUpdate.IsEnabled = true;

        // Update Rollback button visibility
        BtnRollback.Visibility = result.IsRollbackAvailable ? Visibility.Visible : Visibility.Collapsed;

        if (!string.IsNullOrEmpty(result.ErrorMessage))
        {
            SetStatusState("Kiểm tra thất bại", DangerColor, LightRedColor);
            TxtLatestVersion.Text = "Không xác định";
            if (!silent)
            {
                MessageBox.Show(
                    $"Không thể kiểm tra bản cập nhật mới:\n{result.ErrorMessage}\n\n" +
                    "Vui lòng kiểm tra lại địa chỉ máy chủ cập nhật trong phần cấu hình nâng cao.", 
                    "Lỗi kiểm tra cập nhật", 
                    MessageBoxButton.OK, 
                    MessageBoxImage.Warning);
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
            string channelLabel = result.Channel == "Beta" ? " [Beta]" : "";
            SetStatusState($"Có bản mới V{result.LatestVersion}{channelLabel}", WarningColor, LightYellowColor);
            BtnUpdateNow.Visibility = Visibility.Visible;
            BtnToggleChangelog.Visibility = Visibility.Visible;
            BorderChangelog.Visibility = Visibility.Visible;

            // Automatic installation if enabled and no active print jobs
            if (_settings.AutoInstallUpdates)
            {
                bool hasActiveJobs = HasActivePrintJobsCheck != null && HasActivePrintJobsCheck.Invoke();
                if (!hasActiveJobs)
                {
                    // Safe to auto-update
                    BtnUpdateNow_Click(this, new RoutedEventArgs());
                    return;
                }
            }

            // Prompt user with exact office-friendly message
            string promptText = $"Đã có phiên bản Remote Print Manager V{result.LatestVersion}.\n\n" +
                               (string.IsNullOrWhiteSpace(result.Manifest?.ReleaseNotes) 
                                   ? "Phiên bản mới cải thiện khả năng kết nối và khắc phục lỗi hoạt động." 
                                   : result.Manifest.ReleaseNotes) +
                               "\n\nBạn có muốn cập nhật & nâng cấp ngay bây giờ không?";

            var choice = MessageBox.Show(promptText, "Cập nhật Remote Print Manager", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (choice == MessageBoxResult.Yes)
            {
                BtnUpdateNow_Click(this, new RoutedEventArgs());
            }
        }
        else
        {
            SetStatusState("Bạn đang sử dụng phiên bản mới nhất", SuccessColor, LightGreenColor);
            BtnUpdateNow.Visibility = Visibility.Collapsed;
            BtnToggleChangelog.Visibility = Visibility.Collapsed;
            BorderChangelog.Visibility = Visibility.Collapsed;

            if (!silent)
            {
                MessageBox.Show(
                    $"Bạn đang sử dụng phiên bản Remote Print Manager mới nhất (V{_settings.AppVersion}).\n" +
                    $"Kênh phát hành hiện tại: {_settings.UpdateChannel}.\nKhông cần nâng cấp.", 
                    "Thông báo phiên bản", 
                    MessageBoxButton.OK, 
                    MessageBoxImage.Information);
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
        BtnRollback.IsEnabled = false;
        PanelProgress.Visibility = Visibility.Visible;
        SetStatusState("Đang tải bản cập nhật...", PrimaryColor, LightPrimaryColor);

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
            BtnRollback.IsEnabled = true;
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
            Channel = _availableManifest.Channel,
            InstalledAt = DateTime.UtcNow,
            Status = "Thành công",
            Notes = $"Nâng cấp trực tiếp lên V{_availableManifest.Version} ({_availableManifest.Channel})"
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
            BtnRollback.IsEnabled = true;
        }
        else
        {
            Application.Current?.Shutdown();
        }
    }

    private void BtnRollback_Click(object sender, RoutedEventArgs e)
    {
        var choice = MessageBox.Show(
            "Bạn có chắc chắn muốn khôi phục lại phiên bản Remote Print Manager trước đó không?\n\n" +
            "Hệ thống sẽ giữ nguyên toàn bộ cấu hình, danh sách máy in đã ghép nối và lịch sử của bạn.",
            "Xác nhận khôi phục phiên bản (Rollback)",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question
        );

        if (choice == MessageBoxResult.Yes)
        {
            bool ok = UpdateService.RollbackAndRestart(out string err);
            if (!ok)
            {
                MessageBox.Show($"Không thể khôi phục phiên bản trước: {err}", "Lỗi khôi phục", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            else
            {
                Application.Current?.Shutdown();
            }
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
            ConfigurePeriodicTimer();
        }
    }

    private void ChkAutoInstall_Changed(object sender, RoutedEventArgs e)
    {
        if (_settings != null)
        {
            _settings.AutoInstallUpdates = ChkAutoInstall.IsChecked ?? false;
            ConfigManager.Save(_settings);
        }
    }

    private void CmbChannel_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_settings != null && CmbChannel.SelectedItem is ComboBoxItem item)
        {
            string channel = item.Tag?.ToString() ?? "Stable";
            _settings.UpdateChannel = channel;
            TxtCurrentChannel.Text = channel == "Beta" ? "Thử nghiệm (Beta)" : "Chính thức (Stable)";
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

    private static readonly SolidColorBrush PrimaryColor = new(Color.FromArgb(255, 255, 121, 0)); // #FF7900 Cam chính
    private static readonly SolidColorBrush SuccessColor = new(Color.FromArgb(255, 21, 184, 74));  // #15B84A Xanh lá thành công
    private static readonly SolidColorBrush WarningColor = new(Color.FromArgb(255, 242, 169, 0));  // #F2A900 Vàng cảnh báo
    private static readonly SolidColorBrush DangerColor = new(Color.FromArgb(255, 197, 61, 50));   // #C53D32 Đỏ báo lỗi

    private static readonly SolidColorBrush LightPrimaryColor = new(Color.FromArgb(255, 255, 242, 232)); // #FFF2E8
    private static readonly SolidColorBrush LightGreenColor = new(Color.FromArgb(255, 232, 248, 238));   // #E8F8EE
    private static readonly SolidColorBrush LightYellowColor = new(Color.FromArgb(255, 254, 246, 230));  // #FEF6E6
    private static readonly SolidColorBrush LightRedColor = new(Color.FromArgb(255, 250, 236, 235));     // #FAECEB
}
