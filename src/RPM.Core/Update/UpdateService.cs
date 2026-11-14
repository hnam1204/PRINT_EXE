using System.Diagnostics;
using System.Net.Http.Headers;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using RPM.Core.Config;
using RPM.Core.Security;

namespace RPM.Core.Update;

public static class UpdateService
{
    private const long MaxUpdateBytes = 200L * 1024 * 1024;
    private static readonly HttpClient HttpClient = CreateHttpClient();

    public static string GetInstalledVersion()
    {
        var version = Assembly.GetEntryAssembly()?.GetName().Version;
        return version is null ? "1.0.0" : $"{version.Major}.{version.Minor}.{version.Build}";
    }

    public static async Task<UpdateCheckResult> CheckForUpdatesAsync(string feedUrl, string currentVersion, string targetChannel = "Stable")
    {
        var result = new UpdateCheckResult { CurrentVersion = currentVersion, IsRollbackAvailable = CanRollback(), CheckedAtUtc = DateTime.UtcNow };
        try
        {
            UpdateManifest? manifest;
            if (TryParseGitHubRepository(feedUrl, out var repository)) manifest = await GetLatestGitHubManifestAsync(repository, targetChannel);
            else if (Uri.TryCreate(feedUrl, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps && TryParseGitHubRepository(feedUrl, out _)) manifest = await ReadManifestAsync(uri);
            else if (File.Exists(feedUrl)) manifest = JsonSerializer.Deserialize<UpdateManifest>(await File.ReadAllTextAsync(feedUrl), JsonOptions);
            else { result.ErrorMessage = "Nguồn cập nhật không hợp lệ. Hãy dùng GitHub Releases hoặc tệp nội bộ đã xác minh."; return result; }

            if (manifest is null || string.IsNullOrWhiteSpace(manifest.Version)) { result.ErrorMessage = "Nguồn cập nhật không có manifest hợp lệ."; return result; }
            result.LatestVersion = manifest.Version; result.Channel = manifest.Channel; result.Manifest = manifest;
            if (!UpdateSecurity.VerifyManifest(manifest)) { result.ErrorMessage = "Manifest cập nhật không có chữ ký số hợp lệ."; return result; }
            if (!IsCompatibleVersion(currentVersion, manifest.MinSupportedVersion)) { result.ErrorMessage = $"Phiên bản hiện tại không tương thích với bản {manifest.Version}."; return result; }
            if (!IsTrustedDownloadUri(manifest.DownloadUrl)) { result.ErrorMessage = "Bản cập nhật không trỏ tới gói GitHub HTTPS hợp lệ."; return result; }
            result.HasUpdate = IsNewerVersion(currentVersion, manifest.Version) && IsChannelAllowed(targetChannel, manifest.Channel);
            return result;
        }
        catch (OperationCanceledException) { result.ErrorMessage = "Đã hủy kiểm tra cập nhật."; return result; }
        catch (Exception ex) { result.ErrorMessage = $"Không thể kiểm tra cập nhật: {ex.Message}"; return result; }
    }

    public static async Task<(bool Success, string Message, string FilePath)> DownloadUpdateAsync(UpdateManifest manifest, IProgress<int>? progress = null, CancellationToken ct = default)
    {
        string targetFile = string.Empty;
        try
        {
            if (!UpdateSecurity.VerifyManifest(manifest)) return (false, "Từ chối cập nhật: manifest chưa được xác minh chữ ký số.", targetFile);
            if (!TryParseVersion(manifest.Version, out _)) return (false, "Từ chối cập nhật: phiên bản không hợp lệ.", targetFile);
            if (!IsTrustedDownloadUri(manifest.DownloadUrl)) return (false, "Từ chối cập nhật: gói không đến từ nguồn HTTPS đáng tin cậy.", targetFile);
            string updatesFolder = ConfigManager.GetUpdatesFolder();
            targetFile = Path.Combine(updatesFolder, $"RemotePrintManager_v{manifest.Version}.new.exe");
            if (File.Exists(targetFile)) File.Delete(targetFile);
            if (!Uri.TryCreate(manifest.DownloadUrl, UriKind.Absolute, out var uri)) return (false, "Không tìm thấy gói cập nhật.", string.Empty);
            using var response = await HttpClient.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, ct);
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength is > MaxUpdateBytes) return (false, "Từ chối cập nhật: gói tải xuống quá lớn.", string.Empty);
            await using var input = await response.Content.ReadAsStreamAsync(ct);
            await using var output = new FileStream(targetFile, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true);
            await CopyBoundedAsync(input, output, response.Content.Headers.ContentLength, progress, ct);
            if (!IsPeExecutable(targetFile)) { TryDelete(targetFile); return (false, "Từ chối cập nhật: gói không phải EXE Windows hợp lệ.", string.Empty); }
            string actualHash = await ComputeSha256Async(targetFile, ct);
            if (!actualHash.Equals(manifest.Sha256.Trim(), StringComparison.OrdinalIgnoreCase)) { TryDelete(targetFile); return (false, "Xác minh gói thất bại: SHA-256 không khớp.", string.Empty); }
            return (true, "Đã tải và xác minh bản cập nhật.", targetFile);
        }
        catch (OperationCanceledException) { TryDelete(targetFile); return (false, "Đã hủy tải cập nhật.", string.Empty); }
        catch (Exception ex) { TryDelete(targetFile); return (false, $"Không thể tải bản cập nhật: {ex.Message}", string.Empty); }
    }

    public static bool ApplyUpdateAndRestart(string newExePath, out string errorMessage)
    {
        errorMessage = string.Empty;
        try
        {
            string? currentExePath = Environment.ProcessPath;
            string? updaterPath = EnsureUpdaterAvailable();
            if (string.IsNullOrWhiteSpace(currentExePath) || !File.Exists(currentExePath) || !File.Exists(newExePath)) { errorMessage = "Không tìm thấy tệp ứng dụng hoặc gói cập nhật."; return false; }
            if (string.IsNullOrWhiteSpace(updaterPath) || !File.Exists(updaterPath)) { errorMessage = "Không tìm thấy trình cập nhật tích hợp."; return false; }
            var psi = new ProcessStartInfo(updaterPath) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = AppContext.BaseDirectory };
            psi.ArgumentList.Add("--pid"); psi.ArgumentList.Add(Environment.ProcessId.ToString()); psi.ArgumentList.Add("--current"); psi.ArgumentList.Add(currentExePath);
            psi.ArgumentList.Add("--update"); psi.ArgumentList.Add(Path.GetFullPath(newExePath)); psi.ArgumentList.Add("--restart-args"); psi.ArgumentList.Add("--updated");
            Process.Start(psi); return true;
        }
        catch (Exception ex) { errorMessage = ex.Message; return false; }
    }

    public static bool CanRollback() => TryGetBackupPath(out var path) && File.Exists(path);

    public static bool CanUpdateInstallation(out string errorMessage)
    {
        errorMessage = string.Empty;
        string? current = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(current) || !File.Exists(current)) { errorMessage = "Không xác định được tệp ứng dụng hiện tại."; return false; }
        string directory = Path.GetDirectoryName(current) ?? AppContext.BaseDirectory;
        string probe = Path.Combine(directory, $".rpm-update-{Guid.NewGuid():N}.tmp");
        try
        {
            using (new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { }
            File.Delete(probe);
            if (string.IsNullOrWhiteSpace(EnsureUpdaterAvailable())) { errorMessage = "Không tìm thấy trình cập nhật tích hợp."; return false; }
            return true;
        }
        catch (UnauthorizedAccessException) { TryDelete(probe); errorMessage = "Không có quyền ghi vào thư mục cài đặt."; return false; }
        catch (Exception ex) { TryDelete(probe); errorMessage = $"Không thể kiểm tra quyền cập nhật: {ex.Message}"; return false; }
    }

    public static bool RollbackAndRestart(out string errorMessage)
    {
        errorMessage = string.Empty;
        try
        {
            string? current = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(current) || !TryGetBackupPath(out var backup) || !File.Exists(backup)) { errorMessage = "Không tìm thấy bản sao lưu để khôi phục."; return false; }
            string? updater = EnsureUpdaterAvailable();
            if (string.IsNullOrWhiteSpace(updater) || !File.Exists(updater)) { errorMessage = "Không tìm thấy trình cập nhật tích hợp."; return false; }
            var psi = new ProcessStartInfo(updater) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = AppContext.BaseDirectory };
            psi.ArgumentList.Add("--pid"); psi.ArgumentList.Add(Environment.ProcessId.ToString()); psi.ArgumentList.Add("--current"); psi.ArgumentList.Add(current);
            psi.ArgumentList.Add("--rollback"); psi.ArgumentList.Add(backup); psi.ArgumentList.Add("--restart-args"); psi.ArgumentList.Add("--rolled-back"); Process.Start(psi); return true;
        }
        catch (Exception ex) { errorMessage = ex.Message; return false; }
    }

    public static bool IsNewerVersion(string currentVersionStr, string newVersionStr) => TryParseVersion(currentVersionStr, out var current) && TryParseVersion(newVersionStr, out var newer) && newer > current;

    public static bool IsCompatibleVersion(string currentVersion, string minimumVersion)
    {
        return TryParseVersion(currentVersion, out var current) && TryParseVersion(minimumVersion, out var minimum) && current >= minimum;
    }

    public static void WritePendingUpdate(string fromVersion, UpdateManifest manifest)
    {
        var pending = new { FromVersion = fromVersion, ToVersion = manifest.Version, Channel = manifest.Channel, InstalledAt = DateTime.UtcNow };
        File.WriteAllText(Path.Combine(ConfigManager.GetUpdatesFolder(), "pending-update.json"), JsonSerializer.Serialize(pending));
    }

    public static void CompletePendingUpdate(AppSettings settings)
    {
        string path = Path.Combine(ConfigManager.GetUpdatesFolder(), "pending-update.json");
        if (!File.Exists(path)) return;
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            var root = document.RootElement;
            string toVersion = root.GetProperty("ToVersion").GetString() ?? GetInstalledVersion();
            string fromVersion = root.GetProperty("FromVersion").GetString() ?? settings.AppVersion;
            string channel = root.GetProperty("Channel").GetString() ?? "Stable";
            string installedVersion = GetInstalledVersion();
            bool succeeded = string.Equals(installedVersion, toVersion, StringComparison.OrdinalIgnoreCase);
            settings.AppVersion = installedVersion;
            settings.UpdateHistory.Insert(0, new UpdateHistoryItem { FromVersion = fromVersion, ToVersion = toVersion, Channel = channel, InstalledAt = DateTime.UtcNow, Status = succeeded ? "Thành công" : "Thất bại", Notes = succeeded ? "Cài đặt bởi trình cập nhật riêng." : $"Phiên bản thực tế {installedVersion} không khớp bản yêu cầu." });
            ConfigManager.Save(settings);
            File.Delete(path);
        }
        catch { }
    }

    private static async Task<UpdateManifest?> GetLatestGitHubManifestAsync(string repository, string targetChannel)
    {
        using var response = await HttpClient.GetAsync($"https://api.github.com/repos/{repository}/releases?per_page=30"); response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync()); UpdateManifest? best = null; Version bestVersion = new(0, 0, 0);
        foreach (var release in document.RootElement.EnumerateArray())
        {
            bool isBeta = release.GetProperty("prerelease").GetBoolean(); if (!IsChannelAllowed(targetChannel, isBeta ? "Beta" : "Stable")) continue;
            string manifestName = isBeta ? "version_beta.json" : "version.json";
            var asset = release.GetProperty("assets").EnumerateArray().FirstOrDefault(a => a.GetProperty("name").GetString() == manifestName); if (asset.ValueKind == JsonValueKind.Undefined) continue;
            string? assetUrl = asset.GetProperty("url").GetString(); if (string.IsNullOrWhiteSpace(assetUrl)) continue;
            var manifest = await ReadManifestAsync(new Uri(assetUrl), "application/octet-stream");
            string tag = release.GetProperty("tag_name").GetString() ?? string.Empty;
            bool isReleaseAsset = release.GetProperty("assets").EnumerateArray().Any(a => string.Equals(a.GetProperty("browser_download_url").GetString(), manifest?.DownloadUrl, StringComparison.Ordinal));
            if (manifest is null || !UpdateSecurity.VerifyManifest(manifest) || !IsChannelAllowed(targetChannel, manifest.Channel) || !IsTrustedDownloadUri(manifest.DownloadUrl, repository) || !isReleaseAsset) continue;
            if (!TryParseVersion(manifest.Version, out var parsed) || parsed <= bestVersion) continue;
            manifest.ReleaseTag = tag; best = manifest; bestVersion = parsed;
        }
        return best;
    }

    private static async Task<UpdateManifest?> ReadManifestAsync(Uri uri, string? accept = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, uri); if (!string.IsNullOrWhiteSpace(accept)) request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(accept));
        using var response = await HttpClient.SendAsync(request); response.EnsureSuccessStatusCode(); return JsonSerializer.Deserialize<UpdateManifest>(await response.Content.ReadAsStringAsync(), JsonOptions);
    }

    private static async Task CopyBoundedAsync(Stream input, Stream output, long? expected, IProgress<int>? progress, CancellationToken ct)
    {
        byte[] buffer = new byte[81920]; long total = 0; int read;
        while ((read = await input.ReadAsync(buffer, ct)) > 0) { total += read; if (total > MaxUpdateBytes) throw new InvalidDataException("Gói tải xuống quá lớn."); await output.WriteAsync(buffer.AsMemory(0, read), ct); if (expected is > 0) progress?.Report((int)Math.Min(100, total * 100 / expected.Value)); }
        progress?.Report(100);
    }

    private static HttpClient CreateHttpClient() { var client = new HttpClient { Timeout = TimeSpan.FromSeconds(45) }; client.DefaultRequestHeaders.UserAgent.ParseAdd("RemotePrintManager-Updater/1.0"); return client; }
    private static bool TryParseGitHubRepository(string value, out string repository)
    {
        repository = value.StartsWith("github://", StringComparison.OrdinalIgnoreCase) ? value[9..].Trim('/') : string.Empty;
        if (string.IsNullOrEmpty(repository) && Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase)) repository = uri.AbsolutePath.Trim('/');
        return repository.Split('/') is { Length: 2 } && repository.Split('/').All(part => !string.IsNullOrWhiteSpace(part));
    }
    private static bool IsTrustedDownloadUri(string value, string? repository = null)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps || !uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase)) return false;
        if (!uri.AbsolutePath.Contains("/releases/download/", StringComparison.OrdinalIgnoreCase)) return false; return repository is null || uri.AbsolutePath.Contains($"/{repository}/releases/", StringComparison.OrdinalIgnoreCase);
    }
    private static bool IsChannelAllowed(string target, string actual) => string.Equals(target, "Beta", StringComparison.OrdinalIgnoreCase) || string.Equals(actual, "Stable", StringComparison.OrdinalIgnoreCase);
    private static bool TryParseVersion(string value, out Version version)
    {
        version = new Version();
        string clean = value.Trim().TrimStart('v', 'V').Split('-', '+')[0];
        string[] parts = clean.Split('.');
        if (parts.Length != 3 || parts.Any(part => !int.TryParse(part, out _)) || !Version.TryParse(clean, out var parsed)) return false;
        version = parsed;
        return true;
    }
    private static async Task<string> ComputeSha256Async(string path, CancellationToken ct) { await using var stream = File.OpenRead(path); return Convert.ToHexString(await SHA256.HashDataAsync(stream, ct)).ToLowerInvariant(); }
    private static bool IsPeExecutable(string path)
    {
        using var stream = File.OpenRead(path); using var reader = new BinaryReader(stream); if (stream.Length < 0x40 || reader.ReadUInt16() != 0x5A4D) return false;
        stream.Position = 0x3C; int peOffset = reader.ReadInt32(); if (peOffset < 0 || peOffset + 4 > stream.Length) return false; stream.Position = peOffset; return reader.ReadUInt32() == 0x00004550;
    }
    private static bool TryGetBackupPath(out string path) { path = string.Empty; string? current = Environment.ProcessPath; if (string.IsNullOrWhiteSpace(current)) return false; path = current + ".bak"; return true; }
    private static void TryDelete(string path) { try { if (!string.IsNullOrEmpty(path) && File.Exists(path)) File.Delete(path); } catch { } }

    private static string? EnsureUpdaterAvailable()
    {
        string externalPath = Path.Combine(AppContext.BaseDirectory, "RPM.Updater.exe");
        if (File.Exists(externalPath)) return externalPath;

        try
        {
            using Stream? source = Assembly.GetEntryAssembly()?.GetManifestResourceStream("RPM.Updater.exe");
            if (source == null) return null;
            string updatesFolder = ConfigManager.GetUpdatesFolder();
            Directory.CreateDirectory(updatesFolder);
            string extractedPath = Path.Combine(updatesFolder, "RPM.Updater.exe");
            using var target = new FileStream(extractedPath, FileMode.Create, FileAccess.Write, FileShare.Read);
            source.CopyTo(target);
            return extractedPath;
        }
        catch
        {
            return null;
        }
    }

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };
}
