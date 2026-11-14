using System.Text.Json;
using System.Text.Json.Nodes;
using System.Security.Cryptography;
using System.Text;
using RPM.Core.Models;
using RPM.Core.Update;

namespace RPM.Core.Config;

public class AppSettings
{
    public string AppVersion { get; set; } = "1.0.1";
    public AppRole Role { get; set; } = AppRole.Unconfigured;
    public string DeviceId { get; set; } = Guid.NewGuid().ToString("N");
    public string DeviceName { get; set; } = Environment.MachineName;
    public string PairingCode { get; set; } = string.Empty;
    public string RelayServerUrl { get; set; } = "ws://127.0.0.1:9090"; // loopback is allowed; public relays must use wss://
    public string SelectedPrinterName { get; set; } = string.Empty;
    public List<string> SharedPrinterNames { get; set; } = new();
    public List<string> Departments { get; set; } = new() { "Chung", "Kế toán", "Kinh doanh", "Hành chính", "Kỹ thuật", "Ban Giám Đốc" };
    public string EncryptionSecret { get; set; } = string.Empty;
    public bool AutoStartWithWindows { get; set; } = true;
    public bool MinimizeToSystemTray { get; set; } = true;
    public bool ShowDesktopNotifications { get; set; } = true;
    public int LocalIppPort { get; set; } = 9199;
    public List<DeviceInfo> ApprovedClients { get; set; } = new();
    public List<PrintJob> PrintHistory { get; set; } = new();
    
    // Auto-update & upgrade configuration
    // Public GitHub Releases is the default distribution source. Feed URL remains
    // for backward-compatible local-file/corporate testing, but is not shown by default.
    public string UpdateFeedUrl { get; set; } = "github://hnam1204/PRINT_EXE";
    public string UpdateRepositoryUrl { get; set; } = "https://github.com/hnam1204/PRINT_EXE";
    public bool AutoCheckUpdates { get; set; } = true;
    public string UpdateChannel { get; set; } = "Beta"; // "Stable" (Chính thức) hoặc "Beta" (Thử nghiệm trước)
    public bool AutoInstallUpdates { get; set; } = false; // Tự động cài đặt hoặc hiển thị nút cho người dùng bấm
    public int CheckIntervalMinutes { get; set; } = 60;   // Chu kỳ kiểm tra định kỳ (phút)
    public List<UpdateHistoryItem> UpdateHistory { get; set; } = new();
}

public static class ConfigManager
{
    private static readonly string AppDataFolder = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "RemotePrintManager"
    );

    private static readonly string ConfigFilePath = Path.Combine(AppDataFolder, "config.json");

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true
    };

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(ConfigFilePath))
            {
                string json = File.ReadAllText(ConfigFilePath);
                var settings = JsonSerializer.Deserialize<AppSettings>(json, JsonOpts);
                if (settings != null)
                {
                    bool wasPlaintext = !string.IsNullOrWhiteSpace(settings.EncryptionSecret) &&
                        !settings.EncryptionSecret.StartsWith("dpapi:", StringComparison.Ordinal);
                    settings.EncryptionSecret = Unprotect(settings.EncryptionSecret);
                    if (wasPlaintext) Save(settings);
                    return settings;
                }
            }
        }
        catch
        {
            // Fallback to default
        }

        var newSettings = new AppSettings();
        Save(newSettings);
        return newSettings;
    }

    public static void Save(AppSettings settings)
    {
        try
        {
            if (!Directory.Exists(AppDataFolder))
            {
                Directory.CreateDirectory(AppDataFolder);
            }

            var root = JsonNode.Parse(JsonSerializer.Serialize(settings, JsonOpts))?.AsObject()
                ?? throw new InvalidOperationException("Unable to serialize application settings.");
            root["EncryptionSecret"] = Protect(settings.EncryptionSecret);
            string json = root.ToJsonString(JsonOpts);
            File.WriteAllText(ConfigFilePath, json);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error saving config: {ex.Message}");
        }
    }

    public static string GetTempJobsFolder()
    {
        string folder = Path.Combine(AppDataFolder, "PrintJobs");
        if (!Directory.Exists(folder))
        {
            Directory.CreateDirectory(folder);
        }
        return folder;
    }

    public static string GetUpdatesFolder()
    {
        string folder = Path.Combine(AppDataFolder, "Updates");
        if (!Directory.Exists(folder))
        {
            Directory.CreateDirectory(folder);
        }
        return folder;
    }

    public static string BackupForUpdate()
    {
        if (!File.Exists(ConfigFilePath)) return string.Empty;
        string backupFolder = Path.Combine(GetUpdatesFolder(), "config-backups");
        Directory.CreateDirectory(backupFolder);
        string backupPath = Path.Combine(backupFolder, $"config-{DateTime.UtcNow:yyyyMMddHHmmssfff}.json");
        File.Copy(ConfigFilePath, backupPath, false);
        return backupPath;
    }

    private static string Protect(string value)
    {
#pragma warning disable CA1416
        if (string.IsNullOrEmpty(value)) return string.Empty;
        if (value.StartsWith("dpapi:", StringComparison.Ordinal)) return value;
        byte[] protectedBytes = ProtectedData.Protect(Encoding.UTF8.GetBytes(value), null, DataProtectionScope.CurrentUser);
        return "dpapi:" + Convert.ToBase64String(protectedBytes);
#pragma warning restore CA1416
    }

    private static string Unprotect(string value)
    {
#pragma warning disable CA1416
        if (string.IsNullOrEmpty(value) || !value.StartsWith("dpapi:", StringComparison.Ordinal)) return value;
        try
        {
            byte[] plaintext = ProtectedData.Unprotect(Convert.FromBase64String(value[6..]), null, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(plaintext);
        }
        catch (CryptographicException) { return string.Empty; }
        catch (FormatException) { return string.Empty; }
#pragma warning restore CA1416
    }
}
