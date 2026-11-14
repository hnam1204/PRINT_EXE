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
        TxtCurrentChannel.Text = _settings.UpdateChannel == "Beta" ? "Bản thử nghiệm" : "Bản chính thức";
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
        SetStatusState("Đang kiểm tra", PrimaryColor, LightPrimaryColor);

        var result = await UpdateService.CheckForUpdatesAsync(_settings.UpdateFeedUrl, _settings.AppVersion, _settings.UpdateChannel);

        BtnCheckUpdate.IsEnabled = true;

        // Update Rollback button visibility
        BtnRollback.Visibility = result.IsRollbackAvailable ? Visibility.Visible : Visibility.Collapsed;

        if (!string.IsNullOrEmpty(result.ErrorMessage))
        {
            SetStatusState("Không kiểm tra được", DangerColor, LightRedColor);
            TxtLatestVersion.Text = "Không xác định";
            if (!silent)
            {
                MessageBox.Show(
                    $"Không kiểm tra được bản mới:\n{result.ErrorMessage}\n\n" +
                    "Hãy kiểm tra địa chỉ cập nhật.", 
                    "Lỗi cập nhật", 
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
            string promptText = $"Có bản mới V{result.LatestVersion}.\n\n" +
                               (string.IsNullOrWhiteSpace(result.Manifest?.ReleaseNotes) 
                                   ? "Bản mới sửa lỗi và kết nối tốt hơn." 
                                   : result.Manifest.ReleaseNotes) +
                               "\n\nCập nhật ngay?";

            var choice = MessageBox.Show(promptText, "Có bản mới", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (choice == MessageBoxResult.Yes)
            {
                BtnUpdateNow_Click(this, new RoutedEventArgs());
            }
        }
        else
        {
            SetStatusState("Đang dùng bản mới nhất", SuccessColor, LightGreenColor);
            BtnUpdateNow.Visibility = Visibility.Collapsed;
            BtnToggleChangelog.Visibility = Visibility.Collapsed;
            BorderChangelog.Visibility = Visibility.Collapsed;

            if (!silent)
            {
                MessageBox.Show(
                    $"Bạn đang dùng bản mới nhất V{_settings.AppVersion}.\n" +
                    "Không cần cập nhật.", 
                    "Cập nhật", 
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
                "Đang có lệnh in.\n\n" +
                "Bạn muốn chờ in xong không?\n\n" +
                "Chọn Có để chờ hoặc Không để cập nhật ngay.",
                "Đang in",
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
        SetStatusState("Đang tải bản mới", PrimaryColor, LightPrimaryColor);

        var progress = new Progress<int>(percent =>
        {
            PbDownload.Value = percent;
            TxtProgressPercentage.Text = $"{percent}%";
            TxtProgressStatus.Text = $"Đang tải... {percent}%";
        });

        var (success, msg, targetFile) = await UpdateService.DownloadUpdateAsync(_availableManifest, progress);

        if (!success)
        {
            PanelProgress.Visibility = Visibility.Collapsed;
            BtnUpdateNow.IsEnabled = true;
            BtnCheckUpdate.IsEnabled = true;
            BtnRollback.IsEnabled = true;
            SetStatusState("Cập nhật lỗi", DangerColor, LightRedColor);
            MessageBox.Show($"Không tải được bản mới:\n{msg}", "Lỗi cập nhật", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        // 3. Apply Update and Restart
        SetStatusState("Đang cài đặt", SuccessColor, LightGreenColor);
        TxtProgressStatus.Text = "Đang cài đặt và khởi động lại...";

        // Record history before restarting
        var historyItem = new UpdateHistoryItem
        {
            FromVersion = _settings.AppVersion,
            ToVersion = _availableManifest.Version,
            Channel = _availableManifest.Channel,
            InstalledAt = DateTime.UtcNow,
            Status = "Thành công",
            Notes = $"Cập nhật lên V{_availableManifest.Version} ({_availableManifest.Channel})"
        };

        _settings.AppVersion = _availableManifest.Version;
        _settings.UpdateHistory.Insert(0, historyItem);
        ConfigManager.Save(_settings);

        await Task.Delay(800);

        bool restarted = UpdateService.ApplyUpdateAndRestart(targetFile, out string err);
        if (!restarted)
        {
            SetStatusState("Cập nhật lỗi", DangerColor, LightRedColor);
            MessageBox.Show($"Không cài được bản mới: {err}", "Lỗi cập nhật", MessageBoxButton.OK, MessageBoxImage.Error);
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
            "Khôi phục bản cũ?\n\nCài đặt và máy in vẫn được giữ nguyên.",
            "Khôi phục bản cũ",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question
        );

        if (choice == MessageBoxResult.Yes)
        {
            bool ok = UpdateService.RollbackAndRestart(out string err);
            if (!ok)
            {
                MessageBox.Show($"Không khôi phục được: {err}", "Lỗi khôi phục", MessageBoxButton.OK, MessageBoxImage.Error);
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
            TxtCurrentChannel.Text = channel == "Beta" ? "Bản thử nghiệm" : "Bản chính thức";
            ConfigManager.Save(_settings);
        }
    }

    private void BtnSaveFeedUrl_Click(object sender, RoutedEventArgs e)
    {
        if (_settings != null)
        {
            _settings.UpdateFeedUrl = TxtFeedUrl.Text.Trim();
            ConfigManager.Save(_settings);
            MessageBox.Show("Đã lưu địa chỉ cập nhật.", "Đã lưu", MessageBoxButton.OK, MessageBoxImage.Information);
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
