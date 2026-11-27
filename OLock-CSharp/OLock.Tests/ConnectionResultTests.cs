using System;
using System.Collections.Generic;
using System.Net;
using System.Runtime.InteropServices;
using OLock;

namespace OLock.Tests
{
    // 连接检测的原因归因测试：验证进程/连接条数/命中情况、IP 白名单与 TCP 表解析
    [Collection("GlobalState")]
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

        [Fact]
        public void ScanTcpV4Table_ParsesUnmanagedMibRowOffsetsCorrectly()
        {
            // MIB_TCPROW_OWNER_PID: 24 字节/行 [state@0, localAddr@4, localPort@8, remoteAddr@12, remotePort@16, owningPid@20]
            const int rowSize = 24;
            const int entryCount = 4;
            int totalSize = sizeof(int) + entryCount * rowSize;
            IntPtr buffer = Marshal.AllocHGlobal(totalSize);
            try
            {
                // 清零整个缓冲区
                for (int b = 0; b < totalSize; b++) Marshal.WriteByte(buffer, b, 0);
                Marshal.WriteInt32(buffer, entryCount);

                void WriteV4Row(int idx, int state, string remoteIp, int pid)
                {
                    IntPtr row = buffer + sizeof(int) + idx * rowSize;
                    Marshal.WriteInt32(row + 0, state);
                    byte[] ipBytes = IPAddress.Parse(remoteIp).GetAddressBytes();
                    Marshal.Copy(ipBytes, 0, row + 12, 4);
                    Marshal.WriteInt32(row + 20, pid);
                }

                WriteV4Row(0, state: 2, remoteIp: "192.168.8.10", pid: 4242); // LISTEN(2)，应忽略
                WriteV4Row(1, state: 5, remoteIp: "192.168.8.11", pid: 9999); // 非目标 PID，应忽略
                WriteV4Row(2, state: 5, remoteIp: "203.0.113.77", pid: 4242); // 目标 PID 公网连接，应计入样本
                WriteV4Row(3, state: 5, remoteIp: "192.168.8.99", pid: 4242); // 目标 PID 命中私网连接

                var cfg = AppConfig.CreateDefault();
                var pids = new HashSet<uint> { 4242u };

                var facts = TcpConnectionChecker.ScanTcpV4Table(buffer, totalSize, pids, cfg, collectDiagnostics: true);
                Assert.Equal(2, facts.EstablishedCount);
                Assert.Equal("192.168.8.99", facts.HitIp);
                Assert.Equal(4242u, facts.HitPid);
                Assert.Contains("203.0.113.77", facts.Samples);
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }

        [Fact]
        public void ScanTcpV6Table_ParsesMappedIpv4AndNativeIpv6OffsetsCorrectly()
        {
            // MIB_TCP6ROW_OWNER_PID: 56 字节/行 [remoteAddr@24 (16B), state@48 (4B), owningPid@52 (4B)]
            const int rowSize = 56;
            const int entryCount = 2;
            int totalSize = sizeof(int) + entryCount * rowSize;
            IntPtr buffer = Marshal.AllocHGlobal(totalSize);
            try
            {
                for (int b = 0; b < totalSize; b++) Marshal.WriteByte(buffer, b, 0);
                Marshal.WriteInt32(buffer, entryCount);

                void WriteV6Row(int idx, int state, string remoteIp, int pid)
                {
                    IntPtr row = buffer + sizeof(int) + idx * rowSize;
                    byte[] ipBytes = IPAddress.Parse(remoteIp).GetAddressBytes();
                    Marshal.Copy(ipBytes, 0, row + 24, 16);
                    Marshal.WriteInt32(row + 48, state);
                    Marshal.WriteInt32(row + 52, pid);
                }

                WriteV6Row(0, state: 5, remoteIp: "::ffff:172.16.4.20", pid: 7777);
                WriteV6Row(1, state: 5, remoteIp: "fe80::1a2b:3c4d", pid: 7777);

                var cfg = AppConfig.CreateDefault(); // 默认空允许列表自动匹配 172.16 与 fe80::
                var pids = new HashSet<uint> { 7777u };

                var facts = TcpConnectionChecker.ScanTcpV6Table(buffer, totalSize, pids, cfg, collectDiagnostics: true);
                Assert.Equal(2, facts.EstablishedCount);
                Assert.Equal("172.16.4.20", facts.HitIp); // IPv4-mapped IPv6 自动解包为 IPv4
                Assert.Equal(7777u, facts.HitPid);

                // 当仅配置 fe80: 白名单时，应跳过 172.16.4.20 并命中第 2 行的原生 IPv6 链路本地地址
                cfg.AllowedRemoteIpPrefixes = new[] { "fe80:" };
                var v6OnlyFacts = TcpConnectionChecker.ScanTcpV6Table(buffer, totalSize, pids, cfg, collectDiagnostics: true);
                Assert.Equal(2, v6OnlyFacts.EstablishedCount);
                Assert.Equal("fe80::1a2b:3c4d", v6OnlyFacts.HitIp);
                Assert.Contains("172.16.4.20", v6OnlyFacts.Samples);
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }

        [Fact]
        public void GetLocalIpAddresses_CachesSnapshotAndRefreshesAfterInvalidation()
        {
            TcpConnectionChecker.InvalidateLocalIpCache();
            var first = TcpConnectionChecker.GetLocalIpAddresses();
            var second = TcpConnectionChecker.GetLocalIpAddresses();
            Assert.NotNull(first);
            if (first.Count > 0)
                Assert.Same(first, second); // 60 秒内命中缓存复用同一不可变快照

            TcpConnectionChecker.InvalidateLocalIpCache();
            var refreshed = TcpConnectionChecker.GetLocalIpAddresses();
            Assert.NotNull(refreshed);
            Assert.NotSame(first, refreshed); // 失效后应重新采集生成新快照
        }
    }
}
