using System.Text.Json;
using System.Text.Json.Nodes;
using System.Security.Cryptography;
using System.Text;
using RPM.Core.Models;
using RPM.Core.Update;

namespace RPM.Core.Config;

public enum PrintTransportMode
{
    DirectOnly,
    Auto,
    LegacyCloud
}

public class AppSettings
{
    public const int CurrentConfigSchemaVersion = 2;

    public int ConfigSchemaVersion { get; set; } = CurrentConfigSchemaVersion;
    public string AppVersion { get; set; } = "1.0.1";
    public AppRole Role { get; set; } = AppRole.Unconfigured;
    public string DeviceId { get; set; } = Guid.NewGuid().ToString("N");
    public string DeviceName { get; set; } = Environment.MachineName;
    public string PairingCode { get; set; } = string.Empty;
    public string RelayServerUrl { get; set; } = "ws://127.0.0.1:9090"; // loopback is allowed; public relays must use wss://
    public bool RelayServerAutoDiscovered { get; set; }
    public string SelectedPrinterName { get; set; } = string.Empty;
    public List<string> SharedPrinterNames { get; set; } = new();
    public List<string> Departments { get; set; } = new() { "Chung", "Kế toán", "Kinh doanh", "Hành chính", "Kỹ thuật", "Ban Giám Đốc" };
    public string EncryptionSecret { get; set; } = string.Empty;
    public string CloudApiBaseUrl { get; set; } = "https://print-exe.vercel.app/";
    public string RealtimeGatewayUrl { get; set; } = string.Empty;
    public bool RealtimeEnabled { get; set; } = true;
    public long RealtimeEventCursor { get; set; }
    public List<long> RealtimeProcessedEventIds { get; set; } = new();
    public bool CloudTransportEnabled { get; set; } = false;
    // Kept separately from the customer-facing label so authorization cannot
    // be bypassed by choosing a label such as "Quản trị viên".
    public bool CloudUsesCustomerActivation { get; set; } = false;
    // Only the first five characters are retained for a safe activation hint.
    public string CloudEnrollmentKeyPrefix { get; set; } = string.Empty;
    public string CloudActivationLabel { get; set; } = string.Empty;
    public DateTime? CloudActivationExpiresAt { get; set; }
    public int? CloudActivationMaxDevices { get; set; }
    public int? CloudActivationUsedDevices { get; set; }
    public string CloudActivationGeofencePolicy { get; set; } = "LIMITED";
    public int? CloudActivationGeofenceRadiusMeters { get; set; }
    public string CloudHostId { get; set; } = string.Empty;
    public string CloudHostPublicKey { get; set; } = string.Empty;
    public string CloudHostPublicKeyFingerprint { get; set; } = string.Empty;
    public string CloudPairingCode { get; set; } = string.Empty;
    public DateTime? CloudPairingExpiresAt { get; set; }
    public string CloudPairingSessionId { get; set; } = string.Empty;
    public string CloudPairingId { get; set; } = string.Empty;
    public bool AutoStartWithWindows { get; set; } = true;
    public bool MinimizeToSystemTray { get; set; } = true;
    public bool ShowDesktopNotifications { get; set; } = true;
    public int LocalIppPort { get; set; } = 9199;
    public bool DirectPrintStreamingEnabled { get; set; } = true;
    // Nullable preserves the legacy boolean for existing config files while
    // making the active transport policy explicit for new installations.
    public PrintTransportMode? TransportMode { get; set; }
    public int DirectPrintChunkBytes { get; set; } = RPM.Core.Security.PacketSecurity.DirectPrintChunkBytes;
    public long DirectPrintMaxBytes { get; set; } = RPM.Core.Security.PacketSecurity.MaxDirectPrintJobBytes;
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

    public PrintTransportMode EffectiveTransportMode => TransportMode ??
        (DirectPrintStreamingEnabled ? PrintTransportMode.DirectOnly : PrintTransportMode.LegacyCloud);

    public bool UsesDirectTransport => EffectiveTransportMode != PrintTransportMode.LegacyCloud;
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
        if (TryLoadFile(ConfigFilePath, out var settings, out bool changed))
        {
            if (changed) Save(settings);
            return settings;
        }

        PreserveUnreadableConfig();
        string backupFolder = Path.Combine(AppDataFolder, "Updates", "config-backups");
        if (Directory.Exists(backupFolder))
        {
            foreach (string backupPath in Directory.EnumerateFiles(backupFolder, "config-*.json")
                         .OrderByDescending(File.GetLastWriteTimeUtc))
            {
                if (!TryLoadFile(backupPath, out settings, out _)) continue;
                Save(settings);
                return settings;
            }
        }

        var newSettings = new AppSettings();
        Save(newSettings);
        return newSettings;
    }

    public static AppSettings Migrate(AppSettings settings, int storedSchemaVersion)
    {
        // Migrations are additive and deliberately preserve all user-owned
        // fields. Missing collections are repaired without resetting identity,
        // activation, printer, geofence, or history data.
        settings.SharedPrinterNames ??= new();
        settings.Departments ??= new() { "Chung" };
        settings.ApprovedClients ??= new();
        settings.PrintHistory ??= new();
        settings.UpdateHistory ??= new();
        settings.RealtimeProcessedEventIds ??= new();

        if (storedSchemaVersion < 2)
        {
            settings.RealtimeEventCursor = Math.Max(0, settings.RealtimeEventCursor);
            settings.RealtimeProcessedEventIds = settings.RealtimeProcessedEventIds
                .Where(id => id > 0)
                .Distinct()
                .TakeLast(256)
                .ToList();
        }

        settings.ConfigSchemaVersion = AppSettings.CurrentConfigSchemaVersion;
        return settings;
    }

    public static void Save(AppSettings settings)
    {
        string? tempPath = null;
        try
        {
            settings.ConfigSchemaVersion = AppSettings.CurrentConfigSchemaVersion;
            if (!Directory.Exists(AppDataFolder))
            {
                Directory.CreateDirectory(AppDataFolder);
            }

            var root = JsonNode.Parse(JsonSerializer.Serialize(settings, JsonOpts))?.AsObject()
                ?? throw new InvalidOperationException("Unable to serialize application settings.");
            root["EncryptionSecret"] = Protect(settings.EncryptionSecret);
            string json = root.ToJsonString(JsonOpts);
            tempPath = ConfigFilePath + $".{Guid.NewGuid():N}.tmp";
            File.WriteAllText(tempPath, json);
            File.Move(tempPath, ConfigFilePath, true);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error saving config: {ex.Message}");
            if (tempPath is not null)
            {
                try { File.Delete(tempPath); } catch { }
            }
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

    public static string GetCloudJobStatePath() => Path.Combine(AppDataFolder, "cloud-job-state.json");

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

    private static bool TryLoadFile(string path, out AppSettings settings, out bool changed)
    {
        settings = new AppSettings();
        changed = false;
        try
        {
            if (!File.Exists(path)) return false;
            string json = File.ReadAllText(path);
            using var document = JsonDocument.Parse(json);
            int storedVersion = document.RootElement.TryGetProperty("ConfigSchemaVersion", out var version)
                && version.TryGetInt32(out int parsedVersion)
                ? parsedVersion
                : 1;
            var loaded = JsonSerializer.Deserialize<AppSettings>(json, JsonOpts);
            if (loaded is null) return false;
            bool wasPlaintext = !string.IsNullOrWhiteSpace(loaded.EncryptionSecret) &&
                !loaded.EncryptionSecret.StartsWith("dpapi:", StringComparison.Ordinal);
            loaded.EncryptionSecret = Unprotect(loaded.EncryptionSecret);
            settings = Migrate(loaded, storedVersion);
            changed = wasPlaintext || storedVersion != AppSettings.CurrentConfigSchemaVersion;
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static void PreserveUnreadableConfig()
    {
        if (!File.Exists(ConfigFilePath)) return;
        try
        {
            string preserved = ConfigFilePath + $".corrupt-{DateTime.UtcNow:yyyyMMddHHmmssfff}.json";
            File.Copy(ConfigFilePath, preserved, false);
        }
        catch { }
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
