using System;
using Microsoft.Win32;
using OLock;

namespace OLock.Tests
{
    // 涉及共享静态状态 (Program.config / Localization / Logging) 的测试类归入同一集合，避免 xUnit 跨类并发互相踩写
    [CollectionDefinition("GlobalState", DisableParallelization = true)]
    public sealed class GlobalStateCollection { }

    // 配置默认值、Normalize 钳制与旧版设置迁移的测试
    [Collection("GlobalState")]
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
            Assert.Empty(c.AllowedRemoteIpPrefixes);
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
        public void Normalize_EmptyPrefixes_PreservedAsEmptyForPrivateNetworkAutoMatch()
        {
            var c = AppConfig.CreateDefault();
            c.AllowedRemoteIpPrefixes = new[] { "  ", "" };
            c.IgnoredRemoteIpPrefixes = null!;
            c.Normalize();
            Assert.Empty(c.AllowedRemoteIpPrefixes);
            Assert.Empty(c.IgnoredRemoteIpPrefixes);

            // 经 Normalize() 后的默认配置必须能匹配 192.168 / 10 / 172.16 / IPv6 链路本地，且拒绝公网 IP
            Assert.True(TcpConnectionChecker.HasAllowedRemoteIpPrefix("192.168.1.5", c, _ => false));
            Assert.True(TcpConnectionChecker.HasAllowedRemoteIpPrefix("10.0.0.8", c, _ => false));
            Assert.True(TcpConnectionChecker.HasAllowedRemoteIpPrefix("172.16.12.34", c, _ => false));
            Assert.True(TcpConnectionChecker.HasAllowedRemoteIpPrefix("fe80::1234", c, _ => false));
            Assert.False(TcpConnectionChecker.HasAllowedRemoteIpPrefix("8.8.8.8", c, _ => false));
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

        [Theory]
        [InlineData(0, 7)]     // 非法值回退默认
        [InlineData(-1, 7)]
        [InlineData(1, 1)]
        [InlineData(7, 7)]
        [InlineData(400, 365)] // 超上界钳制
        public void Normalize_ClampsLogRetentionDays(int input, int expected)
        {
            var c = AppConfig.CreateDefault();
            c.LogRetentionDays = input;
            c.Normalize();
            Assert.Equal(expected, c.LogRetentionDays);
        }

        [Fact]
        public void CreateDefault_LogRetentionDays_Is7()
        {
            Assert.Equal(7, AppConfig.CreateDefault().LogRetentionDays);
        }

        [Fact]
        public void CreateDefault_LogVerbose_IsDisabled()
        {
            Assert.False(AppConfig.CreateDefault().LogVerbose);
        }

        [Fact]
        public void CreateDefault_CheckUpdatesOnStartup_IsEnabled()
        {
            Assert.True(AppConfig.CreateDefault().CheckUpdatesOnStartup);
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

        [Fact]
        public void Normalize_TrimsIpPrefixLists()
        {
            var c = AppConfig.CreateDefault();
            c.AllowedRemoteIpPrefixes = new[] { " 192.168. ", " 10." };
            c.IgnoredRemoteIpPrefixes = new[] { " 172.16. " };
            c.Normalize();
            Assert.Equal(new[] { "192.168.", "10." }, c.AllowedRemoteIpPrefixes);
            Assert.Equal(new[] { "172.16." }, c.IgnoredRemoteIpPrefixes);
        }

        [Fact]
        public void Normalize_TrimsAppProcessName()
        {
            var c = AppConfig.CreateDefault();
            c.AppProcessName = "  O+Connect  ";
            c.Normalize();
            Assert.Equal("O+Connect", c.AppProcessName);
        }

        [Fact]
        public void Storage_SaveAndLoad_RoundTripsAllFields()
        {
            string testKey = $@"Software\OLock_Test_{Guid.NewGuid():N}";
            var prevConfig = Program.config;
            bool prevSleep = Program.autoSleep;
            bool prevScreenOff = Program.autoScreenOff;
            try
            {
                Program.config = new AppConfig
                {
                    AppProcessName = "CustomConnect",
                    OfflineSeconds = 15,
                    WarmupSeconds = 90,
                    MinWarmupSeconds = 20,
                    MaxWarmupSeconds = 300,
                    AllowedRemoteIpPrefixes = new[] { "192.168.50.", "fe80:" },
                    IgnoredRemoteIpPrefixes = new[] { "192.168.50.1" },
                    SleepCommand = "custom-sleep.exe",
                    SleepArguments = "--fast",
                    LogRetentionDays = 14,
                    LogVerbose = true,
                    CheckUpdatesOnStartup = false
                };
                Program.autoSleep = true;
                Program.autoScreenOff = false;

                AppConfigStorage.SaveSettings(testKey);

                // 重置为默认值再读回
                Program.config = AppConfig.CreateDefault();
                Program.autoSleep = false;
                Program.autoScreenOff = true;

                AppConfigStorage.LoadSettings(testKey);
                Program.config.Normalize();

                Assert.Equal("CustomConnect", Program.config.AppProcessName);
                Assert.Equal(15, Program.config.OfflineSeconds);
                Assert.Equal(90, Program.config.WarmupSeconds);
                Assert.Equal(20, Program.config.MinWarmupSeconds);
                Assert.Equal(300, Program.config.MaxWarmupSeconds);
                Assert.Equal(new[] { "192.168.50.", "fe80:" }, Program.config.AllowedRemoteIpPrefixes);
                Assert.Equal(new[] { "192.168.50.1" }, Program.config.IgnoredRemoteIpPrefixes);
                Assert.Equal("custom-sleep.exe", Program.config.SleepCommand);
                Assert.Equal("--fast", Program.config.SleepArguments);
                Assert.Equal(14, Program.config.LogRetentionDays);
                Assert.True(Program.config.LogVerbose);
                Assert.False(Program.config.CheckUpdatesOnStartup);
                Assert.True(Program.autoSleep);
                Assert.False(Program.autoScreenOff);
            }
            finally
            {
                Program.config = prevConfig;
                Program.autoSleep = prevSleep;
                Program.autoScreenOff = prevScreenOff;
                Registry.CurrentUser.DeleteSubKeyTree(testKey, throwOnMissingSubKey: false);
            }
        }

        [Fact]
        public void Storage_LoadSettings_MigratesLegacyOfflineThresholdAndIgnoresSingleCorruptKey()
        {
            string testKey = $@"Software\OLock_Test_{Guid.NewGuid():N}";
            var prevConfig = Program.config;
            bool prevSleep = Program.autoSleep;
            bool prevScreenOff = Program.autoScreenOff;
            try
            {
                using (var key = Registry.CurrentUser.CreateSubKey(testKey))
                {
                    key.SetValue("OfflineThreshold", 5);
                    key.SetValue("CheckIntervalSeconds", 4);
                    key.SetValue("WarmupSeconds", "corrupted-int");   // 单项损坏不应中断其余项读取
                    key.SetValue("LogRetentionDays", 21);
                    key.SetValue("CheckUpdatesOnStartup", "not-a-bool");
                }

                Program.config = AppConfig.CreateDefault();
                AppConfigStorage.LoadSettings(testKey);
                Program.config.Normalize();

                Assert.Equal(20, Program.config.OfflineSeconds);     // 5 * 4 迁移生效
                Assert.Equal(60, Program.config.WarmupSeconds);      // 损坏项回退默认
                Assert.Equal(21, Program.config.LogRetentionDays);   // 后续有效项正常读取
                Assert.True(Program.config.CheckUpdatesOnStartup);   // 损坏布尔保持默认
            }
            finally
            {
                Program.config = prevConfig;
                Program.autoSleep = prevSleep;
                Program.autoScreenOff = prevScreenOff;
                Registry.CurrentUser.DeleteSubKeyTree(testKey, throwOnMissingSubKey: false);
            }
        }

    }
}
