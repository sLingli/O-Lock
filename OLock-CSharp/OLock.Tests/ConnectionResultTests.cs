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
    }
}
