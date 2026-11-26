using OLock;

namespace OLock.Tests
{
    // 日志过期清理的行过滤逻辑测试
    [Collection("GlobalState")]
    public class LoggingTests
    {
        [Fact]
        public void FilterLines_DropsEntriesOlderThanCutoff()
        {
            string[] lines =
            {
                "[2026-09-01 08:00:00] INFO: Old log",
                "[2026-09-13 10:00:00] INFO: New log",
                "[2026-09-10 00:00:00] ERROR: Exactly on boundary"
            };
            var kept = Logging.FilterLines(lines, new DateTime(2026, 9, 10, 0, 0, 0));

            Assert.Equal(2, kept.Count);
            Assert.DoesNotContain(kept, l => l.Contains("Old log"));
            Assert.Contains(kept, l => l.Contains("New log"));
            Assert.Contains(kept, l => l.Contains("Exactly on boundary")); // 等于 cutoff 不算过期
        }

        [Fact]
        public void FilterLines_KeepsUnparseableLines()
        {
            string[] lines = { "Truncated log fragment from rotation", "[not-a-date] INFO: x" };
            var kept = Logging.FilterLines(lines, DateTime.Now);

            Assert.Equal(2, kept.Count); // 解析不出时间戳的行保守保留
        }

        [Fact]
        public void FilterLines_AllExpired_ReturnsEmpty()
        {
            string[] lines = { "[2020-01-01 00:00:00] INFO: a", "[2020-01-02 00:00:00] INFO: b" };
            var kept = Logging.FilterLines(lines, DateTime.Now);

            Assert.Empty(kept);
        }

        [Theory]
        [InlineData("[2026-09-23 11:07:50] INFO: [Monitor] Phone online", "Info")]
        [InlineData("[2026-09-23 11:07:58] ERROR: [Network] Call failed (TcpConnectionChecker.cs:108)", "Error")]
        [InlineData("[2026-09-23 11:07:47] DEBUG: [Network] Check: 87 lines", "Debug")]
        [InlineData("[2026-09-23 11:07:59] WARN: Unknown level", "Unknown")]
        [InlineData("Truncated log fragment from rotation", "Unknown")]
        [InlineData("", "Unknown")]
        public void ParseLevel_ClassifiesLogLines(string line, string expected)
        {
            // 用字符串断言避免 public 测试方法暴露 internal 枚举类型 (CS0051)
            Assert.Equal(expected, Logging.ParseLevel(line).ToString());
        }

        [Fact]
        public async Task ConcurrentWriteCleanupAndSnapshotRead_IsThreadSafe()
        {
            string tempLog = Path.Combine(Path.GetTempPath(), $"olock_log_concurrency_{Guid.NewGuid():N}.log");
            var prevConfig = Program.config;
            string? prevPath = Logging.SwapLogFilePath(tempLog);
            try
            {
                Program.config = AppConfig.CreateDefault();
                File.WriteAllLines(tempLog, new[]
                {
                    "[2020-01-01 00:00:00] INFO: [Test] Expired entry",
                    $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] INFO: [Test] Fresh entry"
                });

                var tasks = new List<Task>
                {
                    Task.Run(() =>
                    {
                        for (int i = 0; i < 40; i++)
                            Logging.LogInfo("Test", $"concurrent-write-{i}");
                    }),
                    Task.Run(() =>
                    {
                        for (int i = 0; i < 15; i++)
                            Logging.CleanupExpiredEntries();
                    }),
                    Task.Run(() =>
                    {
                        for (int i = 0; i < 30; i++)
                            _ = Logging.ReadLogSnapshotLines();
                    })
                };

                await Task.WhenAll(tasks);

                string[] finalLines = Logging.ReadLogSnapshotLines();
                Assert.DoesNotContain(finalLines, l => l.Contains("Expired entry"));
                Assert.Contains(finalLines, l => l.Contains("Fresh entry"));
                Assert.Contains(finalLines, l => l.Contains("concurrent-write-39"));
            }
            finally
            {
                Logging.SwapLogFilePath(prevPath);
                Program.config = prevConfig;
                try { if (File.Exists(tempLog)) File.Delete(tempLog); } catch { }
            }
        }
    }
}
