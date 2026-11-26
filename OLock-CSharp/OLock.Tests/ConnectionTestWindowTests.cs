using Microsoft.Win32;
using OLock;

namespace OLock.Tests
{
    // 测试连接测试窗口在演练期间关闭/销毁及休眠唤醒时能否正确维护后台监控状态
    [Collection("GlobalState")]
    public class ConnectionTestWindowTests
    {
        [Fact]
        public void ClosingWindowDuringDrill_RestoresMonitorTimerAndResetsDrillActive()
        {
            var prevConfig = Program.config;
            var prevMonitor = Program.monitor;
            var prevTimer = Program.monitorTimer;
            var prevExecutor = Program.lockExecutor;
            using var testTimer = new System.Windows.Forms.Timer();
            try
            {
                Program.config = AppConfig.CreateDefault();
                Program.monitor = new MonitorStateMachine(Program.config, _ => { }, _ => { });
                Program.monitorTimer = testTimer;
                testTimer.Start();
                Assert.True(testTimer.Enabled);

                using var window = new ConnectionTestWindow();

                // Act 1: 启动演练，断言后台监控定时器已暂停且状态为 Drill
                window.BeginDisconnectTest();
                Assert.True(Program.DrillActive);
                Assert.False(testTimer.Enabled);
                Assert.IsType<DryRunLockExecutor>(Program.lockExecutor);

                // Act 2: 用户中途关闭窗口
                window.Close();

                // Assert: 后台监控定时器必须恢复运行，DrillActive 必须清除，执行器必须恢复真实执行器
                Assert.False(Program.DrillActive);
                Assert.True(testTimer.Enabled);
                Assert.IsType<RealLockExecutor>(Program.lockExecutor);
            }
            finally
            {
                if (Program.DrillActive) Program.EndDrill(waitReconnect: false);
                testTimer.Stop();
                Program.monitorTimer = prevTimer;
                Program.monitor = prevMonitor;
                Program.config = prevConfig;
                Program.lockExecutor = prevExecutor;
            }
        }

        [Fact]
        public void DisposingWindowDuringDrill_RestoresMonitorTimerAndResetsDrillActive()
        {
            var prevConfig = Program.config;
            var prevMonitor = Program.monitor;
            var prevTimer = Program.monitorTimer;
            var prevExecutor = Program.lockExecutor;
            using var testTimer = new System.Windows.Forms.Timer();
            try
            {
                Program.config = AppConfig.CreateDefault();
                Program.monitor = new MonitorStateMachine(Program.config, _ => { }, _ => { });
                Program.monitorTimer = testTimer;
                testTimer.Start();
                Assert.True(testTimer.Enabled);

                var window = new ConnectionTestWindow();
                window.BeginDisconnectTest();
                Assert.True(Program.DrillActive);

                // Act: 直接 Dispose
                window.Dispose();

                // Assert
                Assert.False(Program.DrillActive);
                Assert.True(testTimer.Enabled);
                Assert.IsType<RealLockExecutor>(Program.lockExecutor);
            }
            finally
            {
                if (Program.DrillActive) Program.EndDrill(waitReconnect: false);
                testTimer.Stop();
                Program.monitorTimer = prevTimer;
                Program.monitor = prevMonitor;
                Program.config = prevConfig;
                Program.lockExecutor = prevExecutor;
            }
        }

        [Fact]
        public void PowerResumeDuringDrill_DoesNotRestartMonitorTimer()
        {
            var prevConfig = Program.config;
            var prevMonitor = Program.monitor;
            var prevTimer = Program.monitorTimer;
            var prevExecutor = Program.lockExecutor;
            using var testTimer = new System.Windows.Forms.Timer();
            try
            {
                Program.config = AppConfig.CreateDefault();
                Program.monitor = new MonitorStateMachine(Program.config, _ => { }, _ => { });
                Program.monitorTimer = testTimer;
                testTimer.Start();

                using var window = new ConnectionTestWindow();
                window.BeginDisconnectTest();
                Assert.True(Program.DrillActive);
                Assert.False(testTimer.Enabled);

                // Act: 演练期间模拟 S3 唤醒
                Program.OnPowerModeChanged(null!, new PowerModeChangedEventArgs(PowerModes.Resume));

                // Assert: 状态机重置回等待态，但绝不提前启动 monitorTimer 造成与 drillTimer 双重驱动
                Assert.True(Program.monitor.IsWaitingForApp);
                Assert.False(testTimer.Enabled);
            }
            finally
            {
                if (Program.DrillActive) Program.EndDrill(waitReconnect: false);
                testTimer.Stop();
                Program.monitorTimer = prevTimer;
                Program.monitor = prevMonitor;
                Program.config = prevConfig;
                Program.lockExecutor = prevExecutor;
            }
        }
    }
}
