using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
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
            string? batPath = null;

            try
            {
                await File.WriteAllTextAsync(targetPath, "old-version");
                await File.WriteAllTextAsync(pendingPath, "new-version-1.5.0");

                var psi = UpdateService.CreateFallbackUpdaterStartInfo(pendingPath, targetPath, out batPath);
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
                try { if (batPath != null && File.Exists(batPath)) File.Delete(batPath); } catch { }
                try { Directory.Delete(trickyDir, recursive: true); } catch { }
            }
        }

        [Fact]
        public async Task CopyStreamWithTimeoutsAsync_ThrottlesProgressWithClockAndCatchesFinalBytes()
        {
            // 1. 已知总大小：8 块各 64 KB，前 3 块间隔 <100ms 应被节流，第 4 块跨越 120ms 应汇报，第 8 块到达 100% 必汇报
            const int chunkSize = 64 * 1024;
            long[] chunkTimeSteps = { 1000, 1020, 1040, 1130, 1150, 1410, 1430, 1440 };
            int stepIndex = 0;
            long fakeNow = 1000;
            using (var source = new SteppingChunkStream(chunkSize, chunkTimeSteps.Length, () => fakeNow = chunkTimeSteps[stepIndex++]))
            using (var dest = new MemoryStream())
            {
                var reports = new System.Collections.Generic.List<UpdateDownloadProgress>();
                await UpdateService.CopyStreamWithTimeoutsAsync(
                    source,
                    dest,
                    totalBytes: chunkSize * chunkTimeSteps.Length,
                    idleReadTimeout: TimeSpan.FromSeconds(5),
                    overallTimeout: TimeSpan.FromSeconds(10),
                    progress: new SyncProgress<UpdateDownloadProgress>(reports.Add),
                    clockMs: () => fakeNow);

                Assert.Equal(chunkSize * chunkTimeSteps.Length, dest.Length);
                // 预期汇报点：chunk1(首块)、chunk4(>=100ms)、chunk6(>=250ms)、chunk8(终块100%)
                Assert.Equal(4, reports.Count);
                Assert.Equal(100, reports[^1].Percentage);
                Assert.Equal(chunkSize * chunkTimeSteps.Length, reports[^1].BytesReceived);
            }

            // 2. 未知总大小 (totalBytes=0)：最后一块距离上次汇报 <100ms，验证循环外补报分支确保最终字节数送达
            long[] unknownSteps = { 2000, 2020, 2040 };
            int idx2 = 0;
            long now2 = 2000;
            using (var source2 = new SteppingChunkStream(chunkSize, unknownSteps.Length, () => now2 = unknownSteps[idx2++]))
            using (var dest2 = new MemoryStream())
            {
                var reports2 = new System.Collections.Generic.List<UpdateDownloadProgress>();
                await UpdateService.CopyStreamWithTimeoutsAsync(
                    source2,
                    dest2,
                    totalBytes: 0,
                    idleReadTimeout: TimeSpan.FromSeconds(5),
                    overallTimeout: TimeSpan.FromSeconds(10),
                    progress: new SyncProgress<UpdateDownloadProgress>(reports2.Add),
                    clockMs: () => now2);

                Assert.Equal(2, reports2.Count); // 首块汇报 + 循环外终值补报
                Assert.Equal(chunkSize * unknownSteps.Length, reports2[^1].BytesReceived);
            }
        }

        [Fact]
        public async Task CheckForUpdateAsync_WhenCancelledByCaller_LogsDebugWithoutError()
        {
            string tempLog = Path.Combine(Path.GetTempPath(), $"olock_update_cancel_{Guid.NewGuid():N}.log");
            var prevConfig = Program.config;
            string? prevLogPath = Logging.SwapLogFilePath(tempLog);
            try
            {
                var cfg = AppConfig.CreateDefault();
                cfg.LogVerbose = true; // 开启详细日志以验证真正写入了 DEBUG 而非静默空文件
                Program.config = cfg;

                using var cts = new CancellationTokenSource();
                cts.Cancel();

                var result = await UpdateService.CheckForUpdateAsync(cts.Token);
                Assert.Equal(UpdateCheckStatus.Failed, result.Status);
                Assert.Equal("Cancelled.", result.ErrorMessage);

                string[] logLines = Logging.ReadLogSnapshotLines();
                Assert.Contains(logLines, l => l.Contains("DEBUG:") && l.Contains("cancelled"));
                Assert.DoesNotContain(logLines, l => l.Contains("ERROR:"));
            }
            finally
            {
                Logging.SwapLogFilePath(prevLogPath);
                Program.config = prevConfig;
                try { if (File.Exists(tempLog)) File.Delete(tempLog); } catch { }
            }
        }

        [Fact]
        public void RestartIntoUpdatedExecutable_WhenProcessStartFails_PreservesMonitorAndTryRollbackRestoresExe()
        {
            var prevStarter = Program.processStarter;
            var prevTimer = Program.monitorTimer;
            using var testTimer = new System.Windows.Forms.Timer();
            string tempDir = Path.Combine(Path.GetTempPath(), $"olock_rollback_{Guid.NewGuid():N}");
            Directory.CreateDirectory(tempDir);
            string targetExe = Path.Combine(tempDir, "OLock.exe");
            string oldExe = targetExe + ".old";
            try
            {
                Program.ResetShutdownStateForTest();
                Program.monitorTimer = testTimer;
                testTimer.Start();

                Program.processStarter = _ => throw new System.ComponentModel.Win32Exception("Blocked by antivirus");

                Assert.Throws<System.ComponentModel.Win32Exception>(() =>
                    Program.RestartIntoUpdatedExecutable(targetExe));

                // 拉起失败时旧进程托盘/监控/关闭标志绝不能被破坏
                Assert.False(Program.IsShuttingDown);
                Assert.Same(testTimer, Program.monitorTimer);
                Assert.True(testTimer.Enabled);

                // 验证回滚函数可将 .old 原可执行文件安全恢复回原路径
                File.WriteAllText(targetExe, "blocked-new-exe");
                File.WriteAllText(oldExe, "working-old-exe");
                Assert.True(UpdateService.TryRollbackExecutable(targetExe, oldExe));

                Assert.Equal("working-old-exe", File.ReadAllText(targetExe));
                Assert.False(File.Exists(oldExe));
                Assert.False(File.Exists(targetExe + ".failed"));
            }
            finally
            {
                Program.processStarter = prevStarter;
                Program.ResetShutdownStateForTest();
                testTimer.Stop();
                Program.monitorTimer = prevTimer;
                try { Directory.Delete(tempDir, recursive: true); } catch { }
            }
        }

        [Fact]
        public async Task ApplyDownloadedZipAsync_NullStartAndImmediateCrash_RollsBackAndPreservesTrayAndMonitor()
        {
            var prevStarter = Program.processStarter;
            var prevProbeMs = Program.updatedProcessStabilityProbeMs;
            var prevTimer = Program.monitorTimer;
            var prevConfig = Program.config;
            var prevMonitor = Program.monitor;
            using var testTimer = new System.Windows.Forms.Timer();
            string tempDir = Path.Combine(Path.GetTempPath(), $"olock_apply_e2e_{Guid.NewGuid():N}");
            Directory.CreateDirectory(tempDir);
            string targetExe = Path.Combine(tempDir, "OLock.exe");
            byte[] originalPe = CreateFakePeBytes(0x11);
            byte[] updatedPe = CreateFakePeBytes(0x22);
            try
            {
                UpdateService.ResetRecoveryStateForTest();
                Program.ResetShutdownStateForTest();
                Program.config = AppConfig.CreateDefault();
                Program.monitor = new MonitorStateMachine(Program.config, _ => { }, _ => { });
                Program.monitorTimer = testTimer;
                testTimer.Start();
                TrayUI.InitTrayIcon();

                await File.WriteAllBytesAsync(targetExe, originalPe);

                // 场景 1: processStarter 返回 null -> 应抛异常、保持托盘与监控存活并自动回滚回原 PE
                string staging1 = Path.Combine(tempDir, ".olock_update_case1");
                string zip1 = CreateZipWithExe(staging1, updatedPe);
                Program.processStarter = _ => null;

                await Assert.ThrowsAsync<InvalidOperationException>(() =>
                    UpdateService.ApplyDownloadedZipAsync(zip1, targetExe, staging1));

                Assert.False(Program.IsShuttingDown);
                Assert.True(testTimer.Enabled);
                Assert.NotNull(TrayUI.TrayIconForTest);
                Assert.True(TrayUI.TrayIconForTest!.Visible);
                Assert.Equal(originalPe, await File.ReadAllBytesAsync(targetExe));
                Assert.False(File.Exists(targetExe + ".old"));
                Assert.False(UpdateService.PendingManualRecovery);

                // 场景 2: 新版拉起后在 150ms 探测期内瞬时崩溃退出 (cmd /c exit 1) -> 应拦截并回滚
                string staging2 = Path.Combine(tempDir, ".olock_update_case2");
                string zip2 = CreateZipWithExe(staging2, updatedPe);
                Program.updatedProcessStabilityProbeMs = 300;
                Program.processStarter = _ => Process.Start(new ProcessStartInfo
                {
                    FileName = "cmd.exe",
                    Arguments = "/D /C exit 1",
                    CreateNoWindow = true,
                    UseShellExecute = false
                });

                var crashEx = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                    UpdateService.ApplyDownloadedZipAsync(zip2, targetExe, staging2));
                Assert.Contains("exited immediately", crashEx.Message);
                Assert.False(Program.IsShuttingDown);
                Assert.True(testTimer.Enabled);
                Assert.NotNull(TrayUI.TrayIconForTest);
                Assert.True(TrayUI.TrayIconForTest!.Visible);
                Assert.Equal(originalPe, await File.ReadAllBytesAsync(targetExe));
                Assert.False(UpdateService.PendingManualRecovery);
            }
            finally
            {
                UpdateService.ResetRecoveryStateForTest();
                Program.processStarter = prevStarter;
                Program.updatedProcessStabilityProbeMs = prevProbeMs;
                Program.ResetShutdownStateForTest();
                TrayUI.DisposeTrayIcon();
                testTimer.Stop();
                Program.monitorTimer = prevTimer;
                Program.monitor = prevMonitor;
                Program.config = prevConfig;
                try { Directory.Delete(tempDir, recursive: true); } catch { }
            }
        }

        [Fact]
        public async Task ApplyDownloadedZipAsync_WhenRollbackFails_SetsPendingRecoveryAndProtectsOldBackup()
        {
            var prevStarter = Program.processStarter;
            string tempDir = Path.Combine(Path.GetTempPath(), $"olock_rollback_lock_{Guid.NewGuid():N}");
            Directory.CreateDirectory(tempDir);
            string targetExe = Path.Combine(tempDir, "OLock.exe");
            byte[] originalPe = CreateFakePeBytes(0x33);
            byte[] updatedPe = CreateFakePeBytes(0x44);
            FileStream? avLockStream = null;
            try
            {
                UpdateService.ResetRecoveryStateForTest();
                Program.ResetShutdownStateForTest();
                await File.WriteAllBytesAsync(targetExe, originalPe);

                string staging1 = Path.Combine(tempDir, ".olock_update_lock1");
                string zip1 = CreateZipWithExe(staging1, updatedPe);

                // 模拟杀软在启动瞬间独占锁死新版 targetExe 导致启动与回滚均失败
                Program.processStarter = psi =>
                {
                    avLockStream = new FileStream(psi.FileName, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
                    throw new System.ComponentModel.Win32Exception("Locked by AV");
                };

                var ioEx = await Assert.ThrowsAsync<IOException>(() =>
                    UpdateService.ApplyDownloadedZipAsync(zip1, targetExe, staging1));
                Assert.Contains("automatic rollback failed", ioEx.Message);
                Assert.True(UpdateService.PendingManualRecovery);
                Assert.True(File.Exists(targetExe + ".old"));
                Assert.Equal(originalPe, await File.ReadAllBytesAsync(targetExe + ".old"));

                // 释放锁后再次尝试更新必须被 PendingManualRecovery 拒绝，严禁删除/覆写唯一 .old 备份
                avLockStream?.Dispose();
                avLockStream = null;

                string staging2 = Path.Combine(tempDir, ".olock_update_lock2");
                string zip2 = CreateZipWithExe(staging2, updatedPe);
                await Assert.ThrowsAsync<InvalidOperationException>(() =>
                    UpdateService.ApplyDownloadedZipAsync(zip2, targetExe, staging2));

                Assert.Equal(originalPe, await File.ReadAllBytesAsync(targetExe + ".old"));
            }
            finally
            {
                avLockStream?.Dispose();
                UpdateService.ResetRecoveryStateForTest();
                Program.processStarter = prevStarter;
                Program.ResetShutdownStateForTest();
                try { Directory.Delete(tempDir, recursive: true); } catch { }
            }
        }

        [Fact]
        public void UpdateWindow_DuringApplyPhase_BlocksUserCloseAndCancel()
        {
            var prefetched = new UpdateCheckResult
            {
                Status = UpdateCheckStatus.UpdateAvailable,
                CurrentVersion = "1.4.1",
                LatestVersion = "1.5.0",
                AssetName = "O-Lock-Lite.zip",
                DownloadUrl = "https://example.com/O-Lock-Lite.zip",
                ReleaseNotes = "notes"
            };
            using var window = new UpdateWindow(prefetched, activateOnShow: false)
            {
                ShowInTaskbar = false,
                Opacity = 0
            };
            try
            {
                window.Show();
                Assert.True(window.IsHandleCreated);
                Assert.False(UpdateForm.IsApplyingUpdate);

                // 进入不可逆应用阶段
                Assert.True(window.TryEnterApplyPhaseForTest());
                Assert.True(window.IsApplyingForTest);
                Assert.True(UpdateForm.IsApplyingUpdate);

                // 用户点击 [X] / Alt+F4 (CloseReason.UserClosing) 必须被拦截
                window.Close();
                Assert.False(window.IsDisposed);
                Assert.True(window.IsApplyingForTest);

                // 应用阶段点击取消不得取消状态
                window.SimulateCancelClickForTest();
                Assert.True(window.IsApplyingForTest);

                // 退出应用阶段后可正常关闭
                window.ExitApplyPhaseForTest();
                Assert.False(UpdateForm.IsApplyingUpdate);
                window.Close();
                Assert.True(window.IsDisposed);
            }
            finally
            {
                if (!window.IsDisposed)
                    window.ExitApplyPhaseForTest();
                Assert.False(UpdateForm.IsApplyingUpdate);
            }
        }

        private static byte[] CreateFakePeBytes(byte marker)
        {
            byte[] buf = new byte[60 * 1024];
            buf[0] = 0x4D; // 'M'
            buf[1] = 0x5A; // 'Z'
            buf[2] = marker;
            return buf;
        }

        private static string CreateZipWithExe(string stagingDir, byte[] peBytes)
        {
            Directory.CreateDirectory(stagingDir);
            string zipPath = Path.Combine(stagingDir, "O-Lock-Lite.zip");
            using (var archive = ZipFile.Open(zipPath, ZipArchiveMode.Create))
            {
                var entry = archive.CreateEntry("OLock.exe");
                using var s = entry.Open();
                s.Write(peBytes, 0, peBytes.Length);
            }
            return zipPath;
        }

        private sealed class SteppingChunkStream : Stream
        {
            private readonly int chunkSize;
            private int remainingChunks;
            private readonly Action onChunkRead;

            public SteppingChunkStream(int chunkSize, int chunkCount, Action onChunkRead)
            {
                this.chunkSize = chunkSize;
                remainingChunks = chunkCount;
                this.onChunkRead = onChunkRead;
            }

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

            public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            {
                if (remainingChunks <= 0)
                    return ValueTask.FromResult(0);
                remainingChunks--;
                onChunkRead();
                return ValueTask.FromResult(Math.Min(chunkSize, buffer.Length));
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
