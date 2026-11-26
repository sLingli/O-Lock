using System;
using System.Collections.Generic;
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
    }
}
