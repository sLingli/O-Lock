using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using OLock;

namespace OLock.Tests
{
    // 版本解析比较、GitHub Release JSON 选包解析与 PE 头校验的单元测试
    [Collection("GlobalState")]
    public class UpdateServiceTests
    {
        [Theory]
        [InlineData("1.4.1", 1, 4, 1, 0)]
        [InlineData("v1.4.1", 1, 4, 1, 0)]
        [InlineData("V1.5.0", 1, 5, 0, 0)]
        [InlineData("1.5", 1, 5, 0, 0)]
        [InlineData("1.4.1+0ec04c8", 1, 4, 1, 0)]
        [InlineData("v2.0.0-beta.1", 2, 0, 0, 0)]
        public void TryParseVersion_NormalizesValidVersions(string raw, int major, int minor, int build, int rev)
        {
            Assert.True(UpdateService.TryParseVersion(raw, out Version v));
            Assert.Equal(new Version(major, minor, build, rev), v);
        }

        [Theory]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData("not-a-version")]
        [InlineData("v")]
        public void TryParseVersion_RejectsInvalidStrings(string raw)
        {
            Assert.False(UpdateService.TryParseVersion(raw, out _));
        }

        [Theory]
        [InlineData("1.4.1", "v1.4.2", true)]
        [InlineData("1.4.1+abc", "v1.5.0", true)]
        [InlineData("1.4.0", "v1.4", false)]   // 归一化后相等，不算新版本
        [InlineData("1.4.1", "v1.4.1", false)]
        [InlineData("1.5.0", "v1.4.9", false)]
        public void IsNewerVersion_ComparesNormalizedVersions(string current, string latest, bool expected)
        {
            Assert.Equal(expected, UpdateService.IsNewerVersion(current, latest));
        }

        [Fact]
        public void ParseLatestReleaseJson_SelectsMatchingLiteAssetWhenNewer()
        {
            string json = """
            {
              "tag_name": "v1.5.0",
              "html_url": "https://github.com/sLingli/O-Lock/releases/tag/v1.5.0",
              "body": "- feat: check for updates",
              "assets": [
                {
                  "name": "O-Lock-Full.zip",
                  "size": 63000000,
                  "browser_download_url": "https://github.com/sLingli/O-Lock/releases/download/v1.5.0/O-Lock-Full.zip"
                },
                {
                  "name": "O-Lock-Lite.zip",
                  "size": 170000,
                  "browser_download_url": "https://github.com/sLingli/O-Lock/releases/download/v1.5.0/O-Lock-Lite.zip"
                }
              ]
            }
            """;

            var res = UpdateService.ParseLatestReleaseJson(json, "1.4.1", "Lite");

            Assert.Equal("UpdateAvailable", res.Status.ToString());
            Assert.Equal("1.5.0", res.LatestVersion);
            Assert.Equal("O-Lock-Lite.zip", res.AssetName);
            Assert.Equal(170000, res.AssetSize);
            Assert.Contains("O-Lock-Lite.zip", res.DownloadUrl);
            Assert.Contains("check for updates", res.ReleaseNotes);
        }

        [Fact]
        public void ParseLatestReleaseJson_SelectsMatchingFullAsset()
        {
            string json = """
            {
              "tag_name": "v1.5.0",
              "html_url": "https://github.com/sLingli/O-Lock/releases/tag/v1.5.0",
              "body": "notes",
              "assets": [
                {
                  "name": "O-Lock-Lite.zip",
                  "size": 170000,
                  "browser_download_url": "https://github.com/sLingli/O-Lock/releases/download/v1.5.0/O-Lock-Lite.zip"
                },
                {
                  "name": "O-Lock-Full.zip",
                  "size": 63000000,
                  "browser_download_url": "https://github.com/sLingli/O-Lock/releases/download/v1.5.0/O-Lock-Full.zip"
                }
              ]
            }
            """;

            var res = UpdateService.ParseLatestReleaseJson(json, "1.4.1", "Full");

            Assert.Equal("UpdateAvailable", res.Status.ToString());
            Assert.Equal("O-Lock-Full.zip", res.AssetName);
            Assert.Equal(63000000, res.AssetSize);
        }

        [Theory]
        [InlineData("Full", "O-Lock-Lite.zip")]
        [InlineData("Lite", "O-Lock-Full.zip")]
        [InlineData("Full", "Source-code.zip")]
        [InlineData("Lite", "Symbols.zip")]
        public void ParseLatestReleaseJson_DoesNotCrossFallbackToWrongVariantOrUnrelatedZip(string currentVariant, string uploadedZipName)
        {
            string json = $$"""
            {
              "tag_name": "v1.6.0",
              "html_url": "https://github.com/sLingli/O-Lock/releases/tag/v1.6.0",
              "body": "notes",
              "assets": [
                {
                  "name": "{{uploadedZipName}}",
                  "size": 170000,
                  "browser_download_url": "https://github.com/sLingli/O-Lock/releases/download/v1.6.0/{{uploadedZipName}}"
                }
              ]
            }
            """;

            var res = UpdateService.ParseLatestReleaseJson(json, "1.5.0", currentVariant);

            Assert.Equal("UpdateAvailable", res.Status.ToString());
            Assert.Null(res.AssetName);
            Assert.Null(res.DownloadUrl);
            Assert.Equal(0, res.AssetSize);
        }

        [Fact]
        public void ParseLatestReleaseJson_SameOrOlderVersion_ReportsUpToDate()
        {
            string json = """
            {
              "tag_name": "v1.4.1",
              "html_url": "https://github.com/sLingli/O-Lock/releases/tag/v1.4.1",
              "body": "",
              "assets": []
            }
            """;

            var res = UpdateService.ParseLatestReleaseJson(json, "1.4.1", "Lite");

            Assert.Equal("UpToDate", res.Status.ToString());
            Assert.Equal("1.4.1", res.LatestVersion);
        }

        [Fact]
        public void ParseLatestReleaseJson_RejectsNonHttpsAssetUrl()
        {
            string json = """
            {
              "tag_name": "v1.5.0",
              "html_url": "http://insecure.example.com/release",
              "body": "",
              "assets": [
                {
                  "name": "O-Lock-Lite.zip",
                  "size": 170000,
                  "browser_download_url": "http://insecure.example.com/O-Lock-Lite.zip"
                }
              ]
            }
            """;

            var res = UpdateService.ParseLatestReleaseJson(json, "1.4.1", "Lite");

            Assert.Equal("UpdateAvailable", res.Status.ToString());
            Assert.Null(res.DownloadUrl);
            Assert.Equal(UpdateService.ReleasesPageUrl, res.HtmlUrl);
        }

        [Fact]
        public void ParseLatestReleaseJson_InvalidJson_ReportsFailed()
        {
            var res = UpdateService.ParseLatestReleaseJson("{broken", "1.4.1", "Lite");
            Assert.Equal("Failed", res.Status.ToString());
            Assert.False(string.IsNullOrEmpty(res.ErrorMessage));
        }

        [Fact]
        public void IsValidPeExecutable_ChecksSizeAndMzHeader()
        {
            string tempFile = Path.Combine(Path.GetTempPath(), $"olock_pe_test_{Guid.NewGuid():N}.exe");
            try
            {
                // 过小文件应拒绝
                File.WriteAllBytes(tempFile, new byte[] { 0x4D, 0x5A, 0x90, 0x00 });
                Assert.False(UpdateService.IsValidPeExecutable(tempFile));

                // 足够大且带 MZ 头应通过
                byte[] validBuf = new byte[60 * 1024];
                validBuf[0] = 0x4D; // 'M'
                validBuf[1] = 0x5A; // 'Z'
                File.WriteAllBytes(tempFile, validBuf);
                Assert.True(UpdateService.IsValidPeExecutable(tempFile));

                // 足够大但非 MZ 头应拒绝
                validBuf[0] = 0x50; // 'P' (例如 ZIP 头 PK)
                validBuf[1] = 0x4B;
                File.WriteAllBytes(tempFile, validBuf);
                Assert.False(UpdateService.IsValidPeExecutable(tempFile));
            }
            finally
            {
                if (File.Exists(tempFile))
                    File.Delete(tempFile);
            }
        }

        [Theory]
        [InlineData("en")]
        [InlineData("zh-Hans")]
        [InlineData("zh-Hant")]
        public void UpdateLocalizationKeys_AreDefinedAndFormattableInAllLanguages(string lang)
        {
            string prev = Localization.currentLang;
            try
            {
                Localization.currentLang = lang;
                string[] plainKeys =
                {
                    "settings_check_updates",
                    "tray_check_update",
                    "update_title",
                    "update_checking",
                    "update_up_to_date",
                    "update_notes_label",
                    "update_notes_empty",
                    "update_applying",
                    "update_cancelled",
                    "update_btn_install",
                    "update_btn_recheck",
                    "update_btn_cancel",
                    "update_btn_open_page",
                    "update_btn_close"
                };

                foreach (string key in plainKeys)
                {
                    string val = Localization.Tr(key);
                    Assert.NotEqual(key, val);
                    Assert.False(string.IsNullOrWhiteSpace(val));
                }

                Assert.Contains("1.5.0", Localization.Tr("tray_check_update_new", "1.5.0"));
                Assert.Contains("OLock", Localization.Tr("tray_update_balloon_title", "OLock"));
                Assert.Contains("1.5.0", Localization.Tr("tray_update_balloon_text", "1.5.0"));
                Assert.Contains("Lite", Localization.Tr("update_current_version", "1.4.1", "Lite"));
                Assert.Contains("v1.5.0", Localization.Tr("update_latest_version", "v1.5.0"));
                Assert.Contains("1.5.0", Localization.Tr("update_available", "1.5.0", "O-Lock-Lite.zip"));
                Assert.Contains("1.5.0", Localization.Tr("update_available_no_asset", "1.5.0"));
                Assert.Contains("403", Localization.Tr("update_failed", "HTTP 403"));
                Assert.Contains("50%", Localization.Tr("update_downloading", "O-Lock-Lite.zip", "50%"));
                Assert.Contains("disk full", Localization.Tr("update_apply_failed", "disk full"));
            }
            finally
            {
                Localization.currentLang = prev;
            }
        }

        [Fact]
        public void NotifyUpdateAvailable_WhenTrayNotInitializedOrHidden_DoesNotThrow()
        {
            var result = new UpdateCheckResult
            {
                Status = UpdateCheckStatus.UpdateAvailable,
                CurrentVersion = "1.4.1",
                LatestVersion = "1.5.0"
            };

            TrayUI.HideTrayIcon();
            var ex = Record.Exception(() => TrayUI.NotifyUpdateAvailable(result));
            Assert.Null(ex);
        }

        [Fact]
        public async Task CopyStreamWithTimeoutsAsync_WhenStreamStalls_ThrowsTimeoutException()
        {
            using var stalledSource = new StalledReadStream();
            using var dest = new MemoryStream();

            await Assert.ThrowsAsync<TimeoutException>(() =>
                UpdateService.CopyStreamWithTimeoutsAsync(
                    stalledSource,
                    dest,
                    totalBytes: 1024,
                    idleReadTimeout: TimeSpan.FromMilliseconds(60),
                    overallTimeout: TimeSpan.FromSeconds(10)));
        }

        [Fact]
        public async Task CopyStreamWithTimeoutsAsync_WhenCallerCancels_ThrowsOperationCanceledException()
        {
            using var stalledSource = new StalledReadStream();
            using var dest = new MemoryStream();
            using var userCts = new CancellationTokenSource(TimeSpan.FromMilliseconds(40));

            var ex = await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                UpdateService.CopyStreamWithTimeoutsAsync(
                    stalledSource,
                    dest,
                    totalBytes: 1024,
                    idleReadTimeout: TimeSpan.FromSeconds(10),
                    overallTimeout: TimeSpan.FromSeconds(30),
                    cancellationToken: userCts.Token));

            Assert.IsNotType<TimeoutException>(ex);
        }

        [Fact]
        public async Task FallbackUpdaterScript_HandlesSpecialCharsAndUnicodePaths()
        {
            string trickyDir = Path.Combine(Path.GetTempPath(), $"olock_test & (中文) ^100%_{Guid.NewGuid():N}");
            Directory.CreateDirectory(trickyDir);
            string targetPath = Path.Combine(trickyDir, "OLock.exe");
            string pendingPath = targetPath + ".new";

            try
            {
                await File.WriteAllTextAsync(targetPath, "old-version");
                await File.WriteAllTextAsync(pendingPath, "new-version-1.5.0");

                var psi = UpdateService.CreateFallbackUpdaterStartInfo(pendingPath, targetPath, out string batPath);
                Assert.True(File.Exists(batPath));

                // 测试模式下跳过拉起 GUI 进程并缩短等待间隔，真实跑完 cmd.exe 替换与自清理流程
                psi.Environment["OLOCK_SKIP_LAUNCH"] = "1";
                psi.Environment["OLOCK_PING_COUNT"] = "1";

                using var proc = Process.Start(psi);
                Assert.NotNull(proc);
                await proc!.WaitForExitAsync();

                Assert.Equal(0, proc.ExitCode);
                Assert.False(File.Exists(pendingPath));
                Assert.Equal("new-version-1.5.0", await File.ReadAllTextAsync(targetPath));
                Assert.False(File.Exists(batPath));
            }
            finally
            {
                try { Directory.Delete(trickyDir, recursive: true); } catch { }
            }
        }

        [Fact]
        public async Task CopyStreamWithTimeoutsAsync_ThrottlesProgressAndReportsFinalBytes()
        {
            const int totalSize = 2 * 1024 * 1024; // 2 MB = 32 chunks of 64 KB
            using var source = new MemoryStream(new byte[totalSize]);
            using var dest = new MemoryStream();
            var reports = new System.Collections.Generic.List<UpdateDownloadProgress>();
            var progress = new SyncProgress<UpdateDownloadProgress>(reports.Add);

            await UpdateService.CopyStreamWithTimeoutsAsync(
                source,
                dest,
                totalBytes: totalSize,
                idleReadTimeout: TimeSpan.FromSeconds(5),
                overallTimeout: TimeSpan.FromSeconds(10),
                progress: progress);

            Assert.Equal(totalSize, dest.Length);
            Assert.NotEmpty(reports);
            Assert.True(reports.Count < 16, $"Expected throttled reports (<16), got {reports.Count}");
            Assert.Equal(totalSize, reports[^1].BytesReceived);
            Assert.Equal(100, reports[^1].Percentage);
        }

        [Fact]
        public async Task CheckForUpdateAsync_WhenCancelledByCaller_ReturnsCancelledWithoutErrorLog()
        {
            string tempLog = Path.Combine(Path.GetTempPath(), $"olock_update_cancel_{Guid.NewGuid():N}.log");
            string? prevLogPath = Logging.SwapLogFilePath(tempLog);
            try
            {
                using var cts = new CancellationTokenSource();
                cts.Cancel();

                var result = await UpdateService.CheckForUpdateAsync(cts.Token);
                Assert.Equal(UpdateCheckStatus.Failed, result.Status);
                Assert.Equal("Cancelled.", result.ErrorMessage);

                string[] logLines = Logging.ReadLogSnapshotLines();
                Assert.DoesNotContain(logLines, l => l.Contains("ERROR:"));
            }
            finally
            {
                Logging.SwapLogFilePath(prevLogPath);
                try { if (File.Exists(tempLog)) File.Delete(tempLog); } catch { }
            }
        }

        private sealed class SyncProgress<T> : IProgress<T>
        {
            private readonly Action<T> onReport;
            public SyncProgress(Action<T> onReport) => this.onReport = onReport;
            public void Report(T value) => onReport(value);
        }

        private sealed class StalledReadStream : Stream
        {
            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => false;
            public override long Length => throw new NotSupportedException();
            public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
            public override void Flush() { }
            public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

            public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return 0;
            }
        }
    }
}
