using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using static OLock.Logging;
using static OLock.Program;

namespace OLock
{
    internal enum UpdateCheckStatus
    {
        UpToDate,
        UpdateAvailable,
        Failed
    }

    internal sealed class UpdateCheckResult
    {
        public UpdateCheckStatus Status { get; init; }
        public string CurrentVersion { get; init; } = "";
        public string LatestVersion { get; init; } = "";
        public string TagName { get; init; } = "";
        public string PackageVariant { get; init; } = "Lite";
        public string? AssetName { get; init; }
        public string? DownloadUrl { get; init; }
        public long AssetSize { get; init; }
        public string HtmlUrl { get; init; } = UpdateService.ReleasesPageUrl;
        public string ReleaseNotes { get; init; } = "";
        public string? ErrorMessage { get; init; }
    }

    internal readonly struct UpdateDownloadProgress
    {
        public long BytesReceived { get; }
        public long TotalBytes { get; }
        public int Percentage => TotalBytes > 0
            ? (int)Math.Clamp(BytesReceived * 100L / TotalBytes, 0L, 100L)
            : -1;

        public UpdateDownloadProgress(long bytesReceived, long totalBytes)
        {
            BytesReceived = bytesReceived;
            TotalBytes = totalBytes;
        }
    }

    // 版本检查、包体识别、压缩包流式下载校验与原路径自替换逻辑
    internal static class UpdateService
    {
        internal const string LatestReleaseApiUrl = "https://api.github.com/repos/sLingli/O-Lock/releases/latest";
        internal const string ReleasesPageUrl = "https://github.com/sLingli/O-Lock/releases";

        private static readonly HttpClient httpClient = CreateHttpClient();

        private static HttpClient CreateHttpClient()
        {
            var handler = new SocketsHttpHandler
            {
                AutomaticDecompression = DecompressionMethods.All,
                ConnectTimeout = TimeSpan.FromSeconds(10)
            };
            var client = new HttpClient(handler)
            {
                Timeout = Timeout.InfiniteTimeSpan // 具体超时由各请求的 CancellationToken 控制
            };
            string uaVersion = string.IsNullOrWhiteSpace(AppVersion) ? "1.0" : AppVersion;
            client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue(APP_NAME, uaVersion));
            client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
            return client;
        }

        // 识别当前运行的是 Lite (框架依赖) 还是 Full (独立单文件)
        internal static string GetCurrentPackageVariant()
        {
            try
            {
                var meta = typeof(Program).Assembly
                    .GetCustomAttributes<AssemblyMetadataAttribute>()
                    .FirstOrDefault(a => string.Equals(a.Key, "PackageVariant", StringComparison.OrdinalIgnoreCase));
                if (meta != null && string.Equals(meta.Value, "Full", StringComparison.OrdinalIgnoreCase))
                    return "Full";
                if (meta != null && string.Equals(meta.Value, "Lite", StringComparison.OrdinalIgnoreCase))
                    return "Lite";
            }
            catch { }

            try
            {
                string? exePath = GetCurrentExecutablePath();
                if (!string.IsNullOrEmpty(exePath) && File.Exists(exePath) && new FileInfo(exePath).Length > 10L * 1024 * 1024)
                    return "Full";
            }
            catch { }

            return "Lite";
        }

        internal static string? GetCurrentExecutablePath()
        {
            if (!string.IsNullOrWhiteSpace(Environment.ProcessPath))
                return Environment.ProcessPath;
            try
            {
                return Process.GetCurrentProcess().MainModule?.FileName;
            }
            catch
            {
                return null;
            }
        }

        // 解析版本号字符串（兼容 "v1.4.1"、"1.4.1+sha"、"1.5" 等），统一归一化为 4 段式以便比较
        internal static bool TryParseVersion(string? raw, out Version version)
        {
            version = new Version(0, 0, 0, 0);
            if (string.IsNullOrWhiteSpace(raw))
                return false;

            string cleaned = raw.Trim();
            if (cleaned.StartsWith("v", StringComparison.OrdinalIgnoreCase))
                cleaned = cleaned.Substring(1).Trim();

            int plusIdx = cleaned.IndexOf('+');
            if (plusIdx >= 0)
                cleaned = cleaned.Substring(0, plusIdx);

            int dashIdx = cleaned.IndexOf('-');
            if (dashIdx >= 0)
                cleaned = cleaned.Substring(0, dashIdx);

            if (!Version.TryParse(cleaned, out Version? parsed) || parsed == null)
                return false;

            version = new Version(
                parsed.Major,
                parsed.Minor,
                parsed.Build < 0 ? 0 : parsed.Build,
                parsed.Revision < 0 ? 0 : parsed.Revision);
            return true;
        }

        internal static bool IsNewerVersion(string currentVersion, string latestTag)
        {
            if (!TryParseVersion(currentVersion, out Version current))
                return false;
            if (!TryParseVersion(latestTag, out Version latest))
                return false;
            return latest > current;
        }

        // 纯函数：解析 GitHub Releases latest JSON 并匹配对应包体 (Lite / Full)
        internal static UpdateCheckResult ParseLatestReleaseJson(string json, string currentVersion, string preferredVariant)
        {
            string variant = string.Equals(preferredVariant, "Full", StringComparison.OrdinalIgnoreCase) ? "Full" : "Lite";

            if (string.IsNullOrWhiteSpace(json))
            {
                return new UpdateCheckResult
                {
                    Status = UpdateCheckStatus.Failed,
                    CurrentVersion = currentVersion,
                    PackageVariant = variant,
                    ErrorMessage = "Empty response from update server."
                };
            }

            try
            {
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object)
                {
                    return new UpdateCheckResult
                    {
                        Status = UpdateCheckStatus.Failed,
                        CurrentVersion = currentVersion,
                        PackageVariant = variant,
                        ErrorMessage = "Unexpected JSON structure."
                    };
                }

                string tagName = root.TryGetProperty("tag_name", out var tagProp) && tagProp.ValueKind == JsonValueKind.String
                    ? tagProp.GetString() ?? ""
                    : "";

                if (!TryParseVersion(tagName, out Version latestVer))
                {
                    return new UpdateCheckResult
                    {
                        Status = UpdateCheckStatus.Failed,
                        CurrentVersion = currentVersion,
                        PackageVariant = variant,
                        ErrorMessage = $"Invalid release tag: '{tagName}'."
                    };
                }

                string latestDisplay = $"{latestVer.Major}.{latestVer.Minor}.{latestVer.Build}" +
                    (latestVer.Revision > 0 ? $".{latestVer.Revision}" : "");

                string htmlUrl = root.TryGetProperty("html_url", out var urlProp) && urlProp.ValueKind == JsonValueKind.String
                    ? (urlProp.GetString() ?? ReleasesPageUrl)
                    : ReleasesPageUrl;
                if (!htmlUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                    htmlUrl = ReleasesPageUrl;

                string body = root.TryGetProperty("body", out var bodyProp) && bodyProp.ValueKind == JsonValueKind.String
                    ? (bodyProp.GetString() ?? "")
                    : "";

                string? matchedName = null;
                string? matchedUrl = null;
                long matchedSize = 0;

                if (root.TryGetProperty("assets", out var assetsProp) && assetsProp.ValueKind == JsonValueKind.Array)
                {
                    string exactTarget = $"O-Lock-{variant}.zip";
                    string? fallbackName = null;
                    string? fallbackUrl = null;
                    long fallbackSize = 0;

                    foreach (var asset in assetsProp.EnumerateArray())
                    {
                        if (asset.ValueKind != JsonValueKind.Object) continue;
                        string name = asset.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String ? (n.GetString() ?? "") : "";
                        string dlUrl = asset.TryGetProperty("browser_download_url", out var u) && u.ValueKind == JsonValueKind.String ? (u.GetString() ?? "") : "";
                        long size = asset.TryGetProperty("size", out var s) && s.TryGetInt64(out long sz) ? sz : 0;

                        if (!name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) ||
                            !dlUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }

                        if (string.Equals(name, exactTarget, StringComparison.OrdinalIgnoreCase) ||
                            name.Contains(variant, StringComparison.OrdinalIgnoreCase))
                        {
                            matchedName = name;
                            matchedUrl = dlUrl;
                            matchedSize = size;
                            if (string.Equals(name, exactTarget, StringComparison.OrdinalIgnoreCase))
                                break;
                        }
                        else if (fallbackUrl == null)
                        {
                            fallbackName = name;
                            fallbackUrl = dlUrl;
                            fallbackSize = size;
                        }
                    }

                    if (matchedUrl == null && fallbackUrl != null)
                    {
                        matchedName = fallbackName;
                        matchedUrl = fallbackUrl;
                        matchedSize = fallbackSize;
                    }
                }

                bool hasUpdate = IsNewerVersion(currentVersion, tagName);
                return new UpdateCheckResult
                {
                    Status = hasUpdate ? UpdateCheckStatus.UpdateAvailable : UpdateCheckStatus.UpToDate,
                    CurrentVersion = currentVersion,
                    LatestVersion = latestDisplay,
                    TagName = tagName,
                    PackageVariant = variant,
                    AssetName = matchedName,
                    DownloadUrl = matchedUrl,
                    AssetSize = matchedSize,
                    HtmlUrl = htmlUrl,
                    ReleaseNotes = body
                };
            }
            catch (Exception ex)
            {
                return new UpdateCheckResult
                {
                    Status = UpdateCheckStatus.Failed,
                    CurrentVersion = currentVersion,
                    PackageVariant = variant,
                    ErrorMessage = ex.Message
                };
            }
        }

        // 请求 GitHub Releases API 检测是否有新版本
        internal static async Task<UpdateCheckResult> CheckForUpdateAsync(CancellationToken cancellationToken = default)
        {
            string variant = GetCurrentPackageVariant();
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(15));

            try
            {
                using var response = await httpClient.GetAsync(LatestReleaseApiUrl, HttpCompletionOption.ResponseContentRead, timeoutCts.Token);
                if (!response.IsSuccessStatusCode)
                {
                    string err = $"HTTP {(int)response.StatusCode} ({response.ReasonPhrase})";
                    LogError("Update", $"Update check HTTP error: {err}");
                    return new UpdateCheckResult
                    {
                        Status = UpdateCheckStatus.Failed,
                        CurrentVersion = AppVersion,
                        PackageVariant = variant,
                        ErrorMessage = err
                    };
                }

                string json = await response.Content.ReadAsStringAsync(timeoutCts.Token);
                var result = ParseLatestReleaseJson(json, AppVersion, variant);
                if (result.Status == UpdateCheckStatus.Failed)
                    LogError("Update", $"Update check parse failed: {result.ErrorMessage}");
                else
                    LogInfo("Update", $"Update check result: {result.Status} (current={result.CurrentVersion}, latest={result.LatestVersion}, variant={variant})");

                return result;
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                LogError("Update", "Update check timed out");
                return new UpdateCheckResult
                {
                    Status = UpdateCheckStatus.Failed,
                    CurrentVersion = AppVersion,
                    PackageVariant = variant,
                    ErrorMessage = "Request timed out."
                };
            }
            catch (Exception ex)
            {
                LogError("Update", $"Update check exception: {Describe(ex)}");
                return new UpdateCheckResult
                {
                    Status = UpdateCheckStatus.Failed,
                    CurrentVersion = AppVersion,
                    PackageVariant = variant,
                    ErrorMessage = ex.Message
                };
            }
        }

        // 校验提取出的 exe 是否为合法的非空 PE 可执行文件 (MZ 头)
        internal static bool IsValidPeExecutable(string filePath)
        {
            try
            {
                var info = new FileInfo(filePath);
                if (!info.Exists || info.Length < 50 * 1024)
                    return false;

                using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
                int b0 = fs.ReadByte();
                int b1 = fs.ReadByte();
                return b0 == 0x4D && b1 == 0x5A; // "MZ"
            }
            catch
            {
                return false;
            }
        }

        // 启动时静默清理上次自更新遗留的 .old / .new 与临时目录
        internal static void CleanupUpdateLeftovers()
        {
            try
            {
                string? exePath = GetCurrentExecutablePath();
                if (string.IsNullOrEmpty(exePath)) return;

                string? exeDir = Path.GetDirectoryName(exePath);
                if (string.IsNullOrEmpty(exeDir) || !Directory.Exists(exeDir)) return;

                TryDeleteFile(exePath + ".old");
                TryDeleteFile(exePath + ".new");

                foreach (string dir in Directory.GetDirectories(exeDir, ".olock_update_*"))
                {
                    try { Directory.Delete(dir, recursive: true); } catch { }
                }
            }
            catch { }
        }

        private static void TryDeleteFile(string path)
        {
            try
            {
                if (File.Exists(path))
                    File.Delete(path);
            }
            catch { }
        }

        private static void EnsureDirectoryWritable(string directory)
        {
            string probePath = Path.Combine(directory, $".olock_write_probe_{Guid.NewGuid():N}");
            using (File.Create(probePath, 1, FileOptions.DeleteOnClose))
            {
            }
        }

        // 流式下载对应压缩包、解压校验，并在原路径原子替换后重启新实例
        internal static async Task DownloadAndApplyUpdateAsync(
            UpdateCheckResult release,
            IProgress<UpdateDownloadProgress>? progress = null,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(release.DownloadUrl) ||
                !release.DownloadUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("No valid HTTPS download asset found for this release.");
            }

            string? currentExePath = GetCurrentExecutablePath();
            if (string.IsNullOrWhiteSpace(currentExePath) || !File.Exists(currentExePath))
                throw new InvalidOperationException("Cannot determine current executable path.");

            string exeDir = Path.GetDirectoryName(currentExePath)
                ?? throw new InvalidOperationException("Cannot determine executable directory.");

            // 下载前先验证目录可写，避免无权限目录下白下载几十 MB
            EnsureDirectoryWritable(exeDir);

            string stagingDir = Path.Combine(exeDir, $".olock_update_{Guid.NewGuid():N}");
            Directory.CreateDirectory(stagingDir);

            try
            {
                string zipPath = Path.Combine(stagingDir, release.AssetName ?? "update.zip");
                LogInfo("Update", $"Downloading update from {release.DownloadUrl} to {zipPath}");

                using (var response = await httpClient.GetAsync(release.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken))
                {
                    response.EnsureSuccessStatusCode();
                    long totalBytes = response.Content.Headers.ContentLength ?? release.AssetSize;

                    await using var netStream = await response.Content.ReadAsStreamAsync(cancellationToken);
                    await using var fileStream = new FileStream(zipPath, FileMode.Create, FileAccess.Write, FileShare.None, 65536, useAsync: true);

                    byte[] buffer = new byte[65536];
                    long received = 0;
                    int read;
                    while ((read = await netStream.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken)) > 0)
                    {
                        await fileStream.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                        received += read;
                        progress?.Report(new UpdateDownloadProgress(received, totalBytes));
                    }

                    if (totalBytes > 0 && received != totalBytes)
                        throw new IOException($"Incomplete download ({received} / {totalBytes} bytes).");
                }

                string extractDir = Path.Combine(stagingDir, "extracted");
                ZipFile.ExtractToDirectory(zipPath, extractDir, overwriteFiles: true);

                string targetFileName = Path.GetFileName(currentExePath);
                string[] candidates = Directory.GetFiles(extractDir, "*.exe", SearchOption.AllDirectories);
                string? newExePath = candidates.FirstOrDefault(p => string.Equals(Path.GetFileName(p), targetFileName, StringComparison.OrdinalIgnoreCase))
                    ?? candidates.FirstOrDefault(p => string.Equals(Path.GetFileName(p), "OLock.exe", StringComparison.OrdinalIgnoreCase))
                    ?? candidates.FirstOrDefault();

                if (string.IsNullOrEmpty(newExePath) || !IsValidPeExecutable(newExePath))
                    throw new InvalidDataException("Extracted package does not contain a valid OLock executable.");

                // 优先采用 Windows NTFS 原地重命名替换（无需外部脚本，保留原路径与托盘配置）
                string oldExePath = currentExePath + ".old";
                TryDeleteFile(oldExePath);

                bool swappedInPlace = false;
                try
                {
                    File.Move(currentExePath, oldExePath);
                    try
                    {
                        File.Move(newExePath, currentExePath);
                        swappedInPlace = true;
                    }
                    catch
                    {
                        // 移入新文件失败时立即回滚原可执行文件，保证目录始终可启动
                        if (!File.Exists(currentExePath) && File.Exists(oldExePath))
                            File.Move(oldExePath, currentExePath);
                        throw;
                    }
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                {
                    LogInfo("Update", $"In-place rename blocked ({ex.Message}), falling back to helper script");
                }

                if (swappedInPlace)
                {
                    try { Directory.Delete(stagingDir, recursive: true); } catch { }
                    RestartIntoUpdatedExecutable(currentExePath);
                    return;
                }

                // 兜底路径：若运行中重命名被安全软件拦截，暂存为 .new 并拉起隐藏 cmd 等待当前进程退出后覆盖
                string pendingExePath = currentExePath + ".new";
                File.Copy(newExePath, pendingExePath, overwrite: true);
                try { Directory.Delete(stagingDir, recursive: true); } catch { }

                LaunchFallbackUpdaterScript(pendingExePath, currentExePath);
                ExitForExternalUpdater();
            }
            catch
            {
                try
                {
                    if (Directory.Exists(stagingDir))
                        Directory.Delete(stagingDir, recursive: true);
                }
                catch { }
                throw;
            }
        }

        private static void LaunchFallbackUpdaterScript(string pendingExePath, string targetExePath)
        {
            string workDir = Path.GetDirectoryName(targetExePath) ?? AppContext.BaseDirectory;
            // 隐藏命令行循环最多重试 15 次覆盖目标 exe，随后带 --updated 参数启动新实例
            string script =
                $"for /L %i in (1,1,15) do (" +
                $"ping 127.0.0.1 -n 2 >nul & " +
                $"move /Y \"{pendingExePath}\" \"{targetExePath}\" >nul 2>nul && (" +
                $"start \"\" /D \"{workDir}\" \"{targetExePath}\" --updated & exit /b 0" +
                $"))";

            Process.Start(new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = $"/C \"{script}\"",
                WorkingDirectory = workDir,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
                UseShellExecute = false
            });
        }
    }
}
