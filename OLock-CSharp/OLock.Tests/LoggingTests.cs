using OLock;

namespace OLock.Tests
{
    // 日志过期清理的行过滤逻辑测试
    public class LoggingTests
    {
        [Fact]
        public void FilterLines_DropsEntriesOlderThanCutoff()
        {
            string[] lines =
            {
                "[2026-09-01 08:00:00] INFO: 旧日志",
                "[2026-09-13 10:00:00] INFO: 新日志",
                "[2026-09-10 00:00:00] ERROR: 正好在边界上"
            };
            var kept = Logging.FilterLines(lines, new DateTime(2026, 9, 10, 0, 0, 0));

            Assert.Equal(2, kept.Count);
            Assert.DoesNotContain(kept, l => l.Contains("旧日志"));
            Assert.Contains(kept, l => l.Contains("新日志"));
            Assert.Contains(kept, l => l.Contains("正好在边界上")); // 等于 cutoff 不算过期
        }

        [Fact]
        public void FilterLines_KeepsUnparseableLines()
        {
            string[] lines = { "被轮转截断的半行日志", "[not-a-date] INFO: x" };
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
        [InlineData("[2026-09-23 11:07:50] INFO: [监控] 手机在线", "Info")]
        [InlineData("[2026-09-23 11:07:58] ERROR: [网络] 调用失败 (TcpConnectionChecker.cs:108)", "Error")]
        [InlineData("[2026-09-23 11:07:47] DEBUG: [网络] 检查: 87 行", "Debug")]
        [InlineData("[2026-09-23 11:07:59] WARN: 未知级别", "Unknown")]
        [InlineData("被轮转截断的半行日志", "Unknown")]
        [InlineData("", "Unknown")]
        public void ParseLevel_ClassifiesLogLines(string line, string expected)
        {
            // 用字符串断言避免 public 测试方法暴露 internal 枚举类型 (CS0051)
            Assert.Equal(expected, Logging.ParseLevel(line).ToString());
        }
    }
}
