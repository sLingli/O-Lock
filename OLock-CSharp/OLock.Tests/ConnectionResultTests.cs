using System.Collections.Generic;
using OLock;

namespace OLock.Tests
{
    // 连接检测的原因归因测试：验证进程/连接条数/命中情况如何映射为诊断结论
    public class ConnectionResultTests
    {
        [Fact]
        public void EvaluateConnection_ProcessNotRunning_ReportsProcessNotRunning()
        {
            var r = TcpConnectionChecker.EvaluateConnection(
                appRunning: false, establishedCount: 0, hitIp: null, hitPid: null, samples: new List<string>());

            Assert.False(r.IsConnected);
            Assert.False(r.AppRunning);
            Assert.Equal("ProcessNotRunning", r.Reason.ToString());
        }

        [Fact]
        public void EvaluateConnection_HitMatchedIp_ReportsConnectedWithDetails()
        {
            var r = TcpConnectionChecker.EvaluateConnection(
                appRunning: true, establishedCount: 4, hitIp: "192.168.10.55", hitPid: 1234, samples: new List<string>());

            Assert.True(r.IsConnected);
            Assert.True(r.AppRunning);
            Assert.Equal("192.168.10.55", r.RemoteIp);
            Assert.Equal(1234u, r.Pid);
            Assert.Equal(4, r.EstablishedCount);
            Assert.Equal("Connected", r.Reason.ToString());
        }

        [Fact]
        public void EvaluateConnection_HasConnectionsButNoneMatched_ReportsIpNotMatchedWithSamples()
        {
            var samples = new List<string> { "127.0.0.1", "203.0.113.5" };
            var r = TcpConnectionChecker.EvaluateConnection(
                appRunning: true, establishedCount: 2, hitIp: null, hitPid: null, samples: samples);

            Assert.False(r.IsConnected);
            Assert.True(r.AppRunning);
            Assert.Equal(2, r.EstablishedCount);
            Assert.Equal(samples, r.SampleRemoteIps);
            Assert.Equal("IpNotMatched", r.Reason.ToString());
        }

        [Fact]
        public void EvaluateConnection_ProcessRunningButZeroConnections_ReportsNoEstablishedConnection()
        {
            var r = TcpConnectionChecker.EvaluateConnection(
                appRunning: true, establishedCount: 0, hitIp: null, hitPid: null, samples: new List<string>());

            Assert.False(r.IsConnected);
            Assert.True(r.AppRunning);
            Assert.Equal(0, r.EstablishedCount);
            Assert.Equal("NoEstablishedConnection", r.Reason.ToString());
        }

        [Fact]
        public void EvaluateConnection_HitWinsOverUnmatchedSamples()
        {
            var r = TcpConnectionChecker.EvaluateConnection(
                appRunning: true, establishedCount: 3, hitIp: "10.0.0.8", hitPid: 99,
                samples: new List<string> { "127.0.0.1" });

            Assert.True(r.IsConnected);
            Assert.Equal("10.0.0.8", r.RemoteIp);
            Assert.Equal("Connected", r.Reason.ToString());
        }

        [Fact]
        public void EvaluateConnection_NullSamples_FallsBackToEmptyList()
        {
            var r = TcpConnectionChecker.EvaluateConnection(
                appRunning: true, establishedCount: 0, hitIp: null, hitPid: null, samples: null);

            Assert.False(r.IsConnected);
            Assert.NotNull(r.SampleRemoteIps);
            Assert.Empty(r.SampleRemoteIps);
        }

        [Theory]
        [InlineData("10.0.0.1", true)]
        [InlineData("172.15.255.255", false)]
        [InlineData("172.16.0.1", true)]
        [InlineData("172.31.255.254", true)]
        [InlineData("172.32.0.1", false)]
        [InlineData("192.168.1.5", true)]
        [InlineData("8.8.8.8", false)]
        [InlineData("::ffff:192.168.1.5", true)]
        [InlineData("fe80::1a2b:3c4d", true)]
        [InlineData("fd12:3456::1", true)]
        [InlineData("2001:4860:4860::8888", false)]
        [InlineData("not-an-ip", false)]
        public void IsPrivateIpAddress_ClassifiesIpv4AndIpv6Correctly(string ip, bool expected)
        {
            Assert.Equal(expected, TcpConnectionChecker.IsPrivateIpAddress(ip));
        }

        [Theory]
        [InlineData("192.168.1.55", "192.168.1.", true)]
        [InlineData("192.168.1.55", "192.168.1", true)]
        [InlineData("192.168.100.55", "192.168.1", false)] // 段边界保护：不能误匹配 192.168.100.x
        [InlineData("192.168.1.5", "192.168.1.5", true)]
        [InlineData("192.168.1.50", "192.168.1.5", false)] // 不能把 .5 误匹配为 .50
        [InlineData("fe80::1234", "fe80:", true)]
        [InlineData("fe80::1234", "fe80", true)]
        public void MatchesIpPrefix_EnforcesSegmentBoundaries(string ip, string prefix, bool expected)
        {
            Assert.Equal(expected, TcpConnectionChecker.MatchesIpPrefix(ip, prefix));
        }

        [Fact]
        public void HasAllowedRemoteIpPrefix_StrictWhitelist_RejectsOtherPrivateIps()
        {
            var cfg = AppConfig.CreateDefault();
            cfg.AllowedRemoteIpPrefixes = new[] { "192.168.1." };
            cfg.IgnoredRemoteIpPrefixes = Array.Empty<string>();

            Assert.True(TcpConnectionChecker.HasAllowedRemoteIpPrefix("192.168.1.20", cfg, _ => false));
            Assert.False(TcpConnectionChecker.HasAllowedRemoteIpPrefix("10.0.0.8", cfg, _ => false));
            Assert.False(TcpConnectionChecker.HasAllowedRemoteIpPrefix("172.16.0.8", cfg, _ => false));
            Assert.False(TcpConnectionChecker.HasAllowedRemoteIpPrefix("192.168.2.20", cfg, _ => false));
        }

        [Fact]
        public void HasAllowedRemoteIpPrefix_EmptyWhitelist_FallsBackToPrivateAddress()
        {
            var cfg = AppConfig.CreateDefault();
            cfg.AllowedRemoteIpPrefixes = Array.Empty<string>();
            cfg.IgnoredRemoteIpPrefixes = Array.Empty<string>();

            Assert.True(TcpConnectionChecker.HasAllowedRemoteIpPrefix("172.16.5.9", cfg, _ => false));
            Assert.True(TcpConnectionChecker.HasAllowedRemoteIpPrefix("fe80::abcd", cfg, _ => false));
            Assert.False(TcpConnectionChecker.HasAllowedRemoteIpPrefix("8.8.8.8", cfg, _ => false));
        }

        [Fact]
        public void HasAllowedRemoteIpPrefix_IgnoredAndLocalIpsAlwaysRejected()
        {
            var cfg = AppConfig.CreateDefault();
            cfg.AllowedRemoteIpPrefixes = new[] { "192.168." };
            cfg.IgnoredRemoteIpPrefixes = new[] { "192.168.1.1" };

            Assert.False(TcpConnectionChecker.HasAllowedRemoteIpPrefix("192.168.1.1", cfg, _ => false));
            Assert.True(TcpConnectionChecker.HasAllowedRemoteIpPrefix("192.168.1.10", cfg, _ => false)); // 忽略 192.168.1.1 不误伤 .10
            Assert.False(TcpConnectionChecker.HasAllowedRemoteIpPrefix("192.168.1.25", cfg, ip => ip == "192.168.1.25"));
            Assert.True(TcpConnectionChecker.HasAllowedRemoteIpPrefix("::ffff:192.168.1.88", cfg, _ => false));
        }
    }
}
