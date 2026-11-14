using System.Text.Json;
using RPM.Core.Models;
using RPM.Core.Update;

namespace RPM.Core.Config;

public class AppSettings
{
    public string AppVersion { get; set; } = "1.0.0";
    public AppRole Role { get; set; } = AppRole.Unconfigured;
    public string DeviceId { get; set; } = Guid.NewGuid().ToString("N");
    public string DeviceName { get; set; } = Environment.MachineName;
    public string PairingCode { get; set; } = string.Empty;
    public string RelayServerUrl { get; set; } = "ws://127.0.0.1:9090"; // default local relay or public broker
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
    public string UpdateFeedUrl { get; set; } = "http://127.0.0.1:9090/version.json";
    public bool AutoCheckUpdates { get; set; } = true;
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

            string json = JsonSerializer.Serialize(settings, JsonOpts);
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
}
