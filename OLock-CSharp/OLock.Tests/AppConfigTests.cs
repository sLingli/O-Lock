using OLock;

namespace OLock.Tests
{
    // 配置默认值、Normalize 钳制与旧版设置迁移的测试
    public class AppConfigTests
    {
        [Theory]
        [InlineData(3, 3, 9)]
        [InlineData(20, 3, 60)]
        [InlineData(1, 1, 1)]
        public void MigrateOfflineSeconds_MultipliesThresholdByInterval(int threshold, int interval, int expected)
        {
            Assert.Equal(expected, AppConfig.MigrateOfflineSeconds(threshold, interval));
        }

        [Theory]
        [InlineData(3, 9)]
        [InlineData(4, 12)]
        public void MigrateOfflineSeconds_MissingInterval_UsesDefault3(int threshold, int expected)
        {
            Assert.Equal(expected, AppConfig.MigrateOfflineSeconds(threshold, null));
        }

        [Fact]
        public void CreateDefault_HasExpectedValues()
        {
            var c = AppConfig.CreateDefault();
            Assert.Equal("O+Connect", c.AppProcessName);
            Assert.Equal(9, c.OfflineSeconds);
            Assert.Equal(60, c.WarmupSeconds);
            Assert.Equal(30, c.MinWarmupSeconds);
            Assert.Equal(600, c.MaxWarmupSeconds);
            Assert.Equal(new[] { "192.168.", "10." }, c.AllowedRemoteIpPrefixes);
            Assert.Empty(c.IgnoredRemoteIpPrefixes);
        }

        [Theory]
        [InlineData(0, 9)]     // 非法值回退默认
        [InlineData(-5, 9)]
        [InlineData(5, 5)]     // 合法值保持
        [InlineData(120, 120)] // 上边界
        [InlineData(500, 120)] // 超上界钳制
        public void Normalize_ClampsOfflineSeconds(int input, int expected)
        {
            var c = AppConfig.CreateDefault();
            c.OfflineSeconds = input;
            c.Normalize();
            Assert.Equal(expected, c.OfflineSeconds);
        }

        [Fact]
        public void Normalize_BlankProcessName_FallsBackToDefault()
        {
            var c = AppConfig.CreateDefault();
            c.AppProcessName = "  ";
            c.Normalize();
            Assert.Equal("O+Connect", c.AppProcessName);
        }

        [Fact]
        public void Normalize_EmptyPrefixes_FallBackToDefaults()
        {
            var c = AppConfig.CreateDefault();
            c.AllowedRemoteIpPrefixes = Array.Empty<string>();
            c.IgnoredRemoteIpPrefixes = null!;
            c.Normalize();
            Assert.Equal(new[] { "192.168.", "10." }, c.AllowedRemoteIpPrefixes);
            Assert.Empty(c.IgnoredRemoteIpPrefixes);
        }

        [Theory]
        [InlineData(0, 60)]     // 非法值回退默认
        [InlineData(10, 30)]    // 低于 MinWarmup → Min
        [InlineData(700, 600)]  // 超过 MaxWarmup → Max
        [InlineData(100, 100)]  // 合法值保持
        public void Normalize_ClampsWarmupSeconds(int warmup, int expected)
        {
            var c = AppConfig.CreateDefault(); // Min=30, Max=600
            c.WarmupSeconds = warmup;
            c.Normalize();
            Assert.Equal(expected, c.WarmupSeconds);
        }

        [Fact]
        public void Normalize_MaxWarmupBelowMin_ClampsUpToMin()
        {
            var c = AppConfig.CreateDefault();
            c.MaxWarmupSeconds = 5; // 小于 MinWarmup(30)
            c.Normalize();
            Assert.Equal(30, c.MaxWarmupSeconds);
            Assert.Equal(30, c.WarmupSeconds); // Clamp(60, 30, 30, 60) → 30
        }
    }
}
