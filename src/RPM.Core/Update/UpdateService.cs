using System.Diagnostics;
using System.Net.Http;
using System.Text.Json;
using RPM.Core.Config;
using RPM.Core.Security;
using System.Text.RegularExpressions;

namespace RPM.Core.Update;

public class UpdateService
{
    private const long MaxUpdateBytes = 200L * 1024 * 1024;
    private static readonly HttpClient HttpClient = new() { Timeout = TimeSpan.FromSeconds(30) };

    public static async Task<UpdateCheckResult> CheckForUpdatesAsync(string feedUrl, string currentVersion, string targetChannel = "Stable")
    {
        var result = new UpdateCheckResult
        {
            CurrentVersion = currentVersion,
            IsRollbackAvailable = CanRollback()
        };

        try
        {
            if (string.IsNullOrWhiteSpace(feedUrl))
            {
                result.ErrorMessage = "Chưa cấu hình địa chỉ máy chủ cập nhật (Feed URL).";
                return result;
            }

            string json;
            if (feedUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || 
                feedUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                var response = await HttpClient.GetAsync(feedUrl);
                if (!response.IsSuccessStatusCode)
                {
                    result.ErrorMessage = $"Máy chủ cập nhật phản hồi mã lỗi: {response.StatusCode}";
                    return result;
                }
                json = await response.Content.ReadAsStringAsync();
            }
            else if (File.Exists(feedUrl))
            {
                // Local file path support for offline / corporate file share testing
                json = await File.ReadAllTextAsync(feedUrl);
            }
            else
            {
                result.ErrorMessage = "Không tìm thấy máy chủ cập nhật hoặc đường dẫn không hợp lệ.";
                return result;
            }

            var manifest = JsonSerializer.Deserialize<UpdateManifest>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });

            if (manifest == null || string.IsNullOrWhiteSpace(manifest.Version))
            {
                result.ErrorMessage = "Dữ liệu phiên bản từ máy chủ không hợp lệ.";
                return result;
            }

            result.LatestVersion = manifest.Version;
            result.Channel = manifest.Channel ?? "Stable";
            result.Manifest = manifest;

            if (!UpdateSecurity.VerifyManifest(manifest))
            {
                result.ErrorMessage = "Manifest cập nhật không có chữ ký số hợp lệ.";
                return result;
            }

            // Semantic comparison
            bool isNewer = IsNewerVersion(currentVersion, manifest.Version);
            if (isNewer)
            {
                // Channel check:
                // A machine on "Stable" ignores "Beta" updates until they are officially released to Stable.
                // A machine on "Beta" (thử nghiệm) accepts both Stable and Beta updates.
                bool channelAllowed = true;
                if (string.Equals(targetChannel, "Stable", StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(manifest.Channel, "Beta", StringComparison.OrdinalIgnoreCase))
                {
                    channelAllowed = false;
                }

                result.HasUpdate = channelAllowed;
            }
            else
            {
                result.HasUpdate = false;
            }

            return result;
        }
        catch (Exception ex)
        {
            result.ErrorMessage = $"Lỗi kiểm tra cập nhật: {ex.Message}";
            return result;
        }
    }

    public static async Task<(bool Success, string Message, string FilePath)> DownloadUpdateAsync(
        UpdateManifest manifest, 
        IProgress<int>? progress = null, 
        CancellationToken ct = default)
    {
        try
        {
            if (!UpdateSecurity.VerifyManifest(manifest))
            {
                return (false, "Từ chối cập nhật: manifest không có chữ ký số hợp lệ.", string.Empty);
            }

            string updatesFolder = ConfigManager.GetUpdatesFolder();
            if (!Regex.IsMatch(manifest.Version, @"^\d+\.\d+\.\d+(?:[-+][0-9A-Za-z.-]+)?$"))
            {
                return (false, "Từ chối cập nhật: version không hợp lệ.", string.Empty);
            }
            string targetFile = Path.Combine(updatesFolder, $"RemotePrintManager_v{manifest.Version}.new.exe");

            if (File.Exists(targetFile))
            {
                File.Delete(targetFile);
            }

            if (manifest.DownloadUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || 
                manifest.DownloadUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                if (!Uri.TryCreate(manifest.DownloadUrl, UriKind.Absolute, out var downloadUri) ||
                    (downloadUri.Scheme == "http" && !IsLoopback(downloadUri.Host)))
                {
                    return (false, "Từ chối tải cập nhật qua HTTP không an toàn.", string.Empty);
                }

                using var response = await HttpClient.GetAsync(manifest.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, ct);
                response.EnsureSuccessStatusCode();

                if (response.Content.Headers.ContentLength is > MaxUpdateBytes)
                {
                    return (false, "Từ chối cập nhật: gói tải xuống quá lớn.", string.Empty);
                }

                long? totalBytes = response.Content.Headers.ContentLength;

                await using var contentStream = await response.Content.ReadAsStreamAsync(ct);
                await using var fileStream = new FileStream(targetFile, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true);

                var buffer = new byte[81920];
                long totalRead = 0;
                int bytesRead;

                while ((bytesRead = await contentStream.ReadAsync(buffer, 0, buffer.Length, ct)) != 0)
                {
                    if (totalRead + bytesRead > MaxUpdateBytes)
                    {
                        await fileStream.DisposeAsync();
                        File.Delete(targetFile);
                        return (false, "Từ chối cập nhật: gói tải xuống quá lớn.", string.Empty);
                    }
                    await fileStream.WriteAsync(buffer, 0, bytesRead, ct);
                    totalRead += bytesRead;

                    if (totalBytes.HasValue && totalBytes.Value > 0)
                    {
                        int progressPercentage = (int)((double)totalRead / totalBytes.Value * 100);
                        progress?.Report(progressPercentage);
                    }
                }
            }
            else if (File.Exists(manifest.DownloadUrl))
            {
                // Local copy
                File.Copy(manifest.DownloadUrl, targetFile, true);
                progress?.Report(100);
            }
            else
            {
                return (false, "Không thể tìm thấy file cài đặt tại đường dẫn tải xuống.", string.Empty);
            }

            // Verify integrity (SHA-256)
            if (!string.IsNullOrWhiteSpace(manifest.Sha256))
            {
                byte[] downloadedBytes = await File.ReadAllBytesAsync(targetFile, ct);
                string computedHash = RPMCrypto.ComputeSha256(downloadedBytes);

                if (!string.Equals(computedHash, manifest.Sha256.Trim(), StringComparison.OrdinalIgnoreCase))
                {
                    File.Delete(targetFile);
                    return (false, "Xác thực gói cập nhật thất bại: Mã SHA-256 không khớp, tệp có thể bị hỏng trong quá trình tải.", string.Empty);
                }
            }

            return (true, "Tải và xác thực bản cập nhật thành công!", targetFile);
        }
        catch (Exception ex)
        {
            return (false, $"Lỗi tải bản cập nhật: {ex.Message}", string.Empty);
        }
    }

    public static bool ApplyUpdateAndRestart(string newExePath, out string errorMessage)
    {
        errorMessage = string.Empty;
        try
        {
            string? currentExePath = Environment.ProcessPath;
            if (string.IsNullOrEmpty(currentExePath) || !File.Exists(newExePath))
            {
                errorMessage = "Không tìm thấy đường dẫn tệp thực thi hiện tại hoặc tệp cập nhật mới.";
                return false;
            }

            string backupPath = currentExePath + ".bak";

            // Detached updater script that waits for current process to exit, moves new exe over, and restarts
            // Preserves all user configs in %AppData%\RemotePrintManager\config.json
            string batchScript = $@"
@echo off
timeout /t 1 /nobreak >nul
if exist ""{backupPath}"" del /f /q ""{backupPath}""
if exist ""{currentExePath}"" move /y ""{currentExePath}"" ""{backupPath}""
move /y ""{newExePath}"" ""{currentExePath}""
start """" ""{currentExePath}"" --updated
";

            string scriptFile = Path.Combine(ConfigManager.GetUpdatesFolder(), "apply_update.cmd");
            File.WriteAllText(scriptFile, batchScript);

            var psi = new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = $"/c \"{scriptFile}\"",
                CreateNoWindow = true,
                UseShellExecute = false
            };

            Process.Start(psi);
            return true;
        }
        catch (Exception ex)
        {
            errorMessage = ex.Message;
            return false;
        }
    }

    public static bool CanRollback()
    {
        try
        {
            string? currentExePath = Environment.ProcessPath;
            if (string.IsNullOrEmpty(currentExePath)) return false;
            string backupPath = currentExePath + ".bak";
            return File.Exists(backupPath);
        }
        catch
        {
            return false;
        }
    }

    private static bool IsLoopback(string host) =>
        string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase) ||
        System.Net.IPAddress.TryParse(host, out var address) && System.Net.IPAddress.IsLoopback(address);

    public static bool RollbackAndRestart(out string errorMessage)
    {
        errorMessage = string.Empty;
        try
        {
            string? currentExePath = Environment.ProcessPath;
            if (string.IsNullOrEmpty(currentExePath))
            {
                errorMessage = "Không tìm thấy đường dẫn tệp thực thi hiện tại.";
                return false;
            }

            string backupPath = currentExePath + ".bak";
            if (!File.Exists(backupPath))
            {
                errorMessage = "Không tìm thấy tệp sao lưu phiên bản trước (.bak) để khôi phục.";
                return false;
            }

            string tempCurrent = currentExePath + ".failed";
            string batchScript = $@"
@echo off
timeout /t 1 /nobreak >nul
if exist ""{tempCurrent}"" del /f /q ""{tempCurrent}""
if exist ""{currentExePath}"" move /y ""{currentExePath}"" ""{tempCurrent}""
move /y ""{backupPath}"" ""{currentExePath}""
start """" ""{currentExePath}"" --rolled-back
";

            string scriptFile = Path.Combine(ConfigManager.GetUpdatesFolder(), "apply_rollback.cmd");
            File.WriteAllText(scriptFile, batchScript);

            var psi = new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = $"/c \"{scriptFile}\"",
                CreateNoWindow = true,
                UseShellExecute = false
            };

            Process.Start(psi);
            return true;
        }
        catch (Exception ex)
        {
            errorMessage = ex.Message;
            return false;
        }
    }

    public static bool IsNewerVersion(string currentVersionStr, string newVersionStr)
    {
        string CleanVer(string v)
        {
            v = v.Trim().TrimStart('v', 'V');
            int dashIdx = v.IndexOf('-');
            if (dashIdx > 0) v = v.Substring(0, dashIdx);
            return v;
        }

        string vCurrent = CleanVer(currentVersionStr);
        string vNew = CleanVer(newVersionStr);

        if (Version.TryParse(vNew, out var vN) && Version.TryParse(vCurrent, out var vC))
        {
            return vN > vC;
        }

        return string.Compare(vNew, vCurrent, StringComparison.OrdinalIgnoreCase) > 0;
    }
}
