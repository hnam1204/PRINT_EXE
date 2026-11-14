using System.Diagnostics;
using System.Net.Http;
using System.Text.Json;
using RPM.Core.Config;
using RPM.Core.Security;

namespace RPM.Core.Update;

public class UpdateService
{
    private static readonly HttpClient HttpClient = new() { Timeout = TimeSpan.FromSeconds(30) };

    public static async Task<UpdateCheckResult> CheckForUpdatesAsync(string feedUrl, string currentVersion)
    {
        var result = new UpdateCheckResult
        {
            CurrentVersion = currentVersion
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
                // Local file path support for local testing/deployment
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
            result.Manifest = manifest;

            // Semantic comparison
            if (IsNewerVersion(currentVersion, manifest.Version))
            {
                result.HasUpdate = true;
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
            string updatesFolder = ConfigManager.GetUpdatesFolder();
            string targetFile = Path.Combine(updatesFolder, $"RemotePrintManager_v{manifest.Version}.new.exe");

            if (File.Exists(targetFile))
            {
                File.Delete(targetFile);
            }

            if (manifest.DownloadUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || 
                manifest.DownloadUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                using var response = await HttpClient.GetAsync(manifest.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, ct);
                response.EnsureSuccessStatusCode();

                long? totalBytes = response.Content.Headers.ContentLength;

                await using var contentStream = await response.Content.ReadAsStreamAsync(ct);
                await using var fileStream = new FileStream(targetFile, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true);

                var buffer = new byte[81920];
                long totalRead = 0;
                int bytesRead;

                while ((bytesRead = await contentStream.ReadAsync(buffer, 0, buffer.Length, ct)) != 0)
                {
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
            // Windows allows moving/renaming running files, but replacing running file requires short delay
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
