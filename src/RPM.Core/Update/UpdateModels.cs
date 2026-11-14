namespace RPM.Core.Update;

public class UpdateHistoryItem
{
    public string FromVersion { get; set; } = string.Empty;
    public string ToVersion { get; set; } = string.Empty;
    public string Channel { get; set; } = "Stable";
    public DateTime InstalledAt { get; set; } = DateTime.UtcNow;
    public string Status { get; set; } = "Thành công";
    public string Notes { get; set; } = string.Empty;
}

public class UpdateManifest
{
    public string Version { get; set; } = "1.0.0";
    public string Channel { get; set; } = "Stable"; // "Stable" (Chính thức) hoặc "Beta" (Thử nghiệm)
    public DateTime ReleaseDate { get; set; } = DateTime.UtcNow;
    public string DownloadUrl { get; set; } = string.Empty;
    public string Sha256 { get; set; } = string.Empty;
    public string SignatureBase64 { get; set; } = string.Empty;
    public string MinSupportedVersion { get; set; } = "1.0.0";
    public bool IsCritical { get; set; } = false;
    public List<string> Changelog { get; set; } = new();
    public string ReleaseNotes { get; set; } = string.Empty;
    public string ReleaseTag { get; set; } = string.Empty;
}

public class UpdateCheckResult
{
    public bool HasUpdate { get; set; }
    public string CurrentVersion { get; set; } = "1.0.0";
    public string LatestVersion { get; set; } = "1.0.0";
    public string Channel { get; set; } = "Stable";
    public UpdateManifest? Manifest { get; set; }
    public string? ErrorMessage { get; set; }
    public bool IsRollbackAvailable { get; set; }
    public DateTime? CheckedAtUtc { get; set; }
}
