using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using OLock;

namespace OLock.Tests
{
    // 测试执行器行为：只报告、不动作。报告内容是断开测试完成态的展示来源，必须可读且含"模拟"字样。
    [Collection("GlobalState")]
    public class LockExecutorTests
    {
        [Fact]
        public void DryRun_ExecuteLock_ReportsWithoutLocking()
        {
            string prevLang = Localization.currentLang;
            try
            {
                var reports = new List<string>();
                var executor = new DryRunLockExecutor(reports.Add);

                Localization.currentLang = "en";
                executor.ExecuteLock();

                var report = Assert.Single(reports);
                Assert.Contains("Simulated", report);
                Assert.Contains("lock", report, StringComparison.OrdinalIgnoreCase);
                Assert.Contains("LockWorkStation", report);
            }
            finally
            {
                Localization.currentLang = prevLang;
            }
        }

        [Fact]
        public void DryRun_ExecuteSleep_ReportsConfiguredCommand()
        {
            string prevLang = Localization.currentLang;
            try
            {
                var reports = new List<string>();
                var executor = new DryRunLockExecutor(reports.Add, "my-sleep.exe", "-hibernate");

                Localization.currentLang = "en";
                executor.ExecuteSleep();

                var report = Assert.Single(reports);
                Assert.Contains("Simulated", report);
                Assert.Contains("my-sleep.exe", report);
                Assert.Contains("-hibernate", report);
            }
            finally
            {
                Localization.currentLang = prevLang;
            }
        }

        [Fact]
        public void DryRun_NullReportCallback_DoesNotThrow()
        {
            var executor = new DryRunLockExecutor(null!);

            var ex = Record.Exception(() => executor.ExecuteLock());

            Assert.Null(ex);
        }

        [Fact]
        public void Localization_TrWithoutArgs_ReturnsTemplateDirectlyWithoutFormatException()
        {
            string prevLang = Localization.currentLang;
            try
            {
                Localization.currentLang = "en";
                // 即使传入需要占位符的键且未传参，无参直出也不应抛 FormatException
                string rawTemplate = Localization.Tr("tray_online");
                Assert.Contains("{0}", rawTemplate);
            }
            finally
            {
                Localization.currentLang = prevLang;
            }
        }

        [Fact]
        public async Task RealLockExecutor_WhenLockOrScreenOffFails_LogsWin32ErrorsWithoutThrowing()
        {
            string tempLog = Path.Combine(Path.GetTempPath(), $"olock_real_lock_err_{Guid.NewGuid():N}.log");
            string? prevLogPath = Logging.SwapLogFilePath(tempLog);
            bool prevScreenOff = Program.autoScreenOff;
            var screenOffDone = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            try
            {
                Program.autoScreenOff = true;
                var executor = new RealLockExecutor(
                    lockWorkStation: () => false,
                    sendMessageTimeout: (_, _, _, _, _, _) => IntPtr.Zero,
                    getLastWin32Error: () => 1460, // ERROR_TIMEOUT
                    screenOffDelayMs: 0,
                    onScreenOffCompleted: () => screenOffDone.TrySetResult(true));

                var ex = Record.Exception(() => executor.ExecuteLock());
                Assert.Null(ex);

                // 等待后台关屏回调（含写日志）完整结束，无需轮询
                await screenOffDone.Task.WaitAsync(TimeSpan.FromSeconds(5));

                string[] lines = Logging.ReadLogSnapshotLines();
                Assert.Contains(lines, l => l.Contains("ERROR:") && l.Contains("LockWorkStation failed (Win32 error 1460)"));
                Assert.Contains(lines, l => l.Contains("ERROR:") && l.Contains("Screen-off broadcast timed out or failed (Win32 error 1460)"));
            }
            finally
            {
                Program.autoScreenOff = prevScreenOff;
                Logging.SwapLogFilePath(prevLogPath);
                try { if (File.Exists(tempLog)) File.Delete(tempLog); } catch { }
            }
        }
    }
}
