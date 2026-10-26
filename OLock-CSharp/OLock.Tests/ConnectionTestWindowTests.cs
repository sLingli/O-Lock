using System.Windows.Forms;
using OLock;

namespace OLock.Tests
{
    // 测试连接测试窗口在演练期间关闭/销毁时能否正确复位后台监控状态
    public class ConnectionTestWindowTests
    {
        [Fact]
        public void ClosingWindowDuringDrill_RestoresMonitorTimerAndResetsDrillActive()
        {
            // Arrange: 准备监控环境与定时器
            Program.config = AppConfig.CreateDefault();
            Program.monitor = new MonitorStateMachine(Program.config, _ => { }, _ => { });
            Program.monitorTimer = new System.Windows.Forms.Timer();
            Program.monitorTimer.Start();
            Assert.True(Program.monitorTimer.Enabled);

            using var window = new ConnectionTestWindow();

            // Act 1: 启动演练，断言后台监控定时器已暂停且状态为 Drill
            window.BeginDisconnectTest();
            Assert.True(Program.DrillActive);
            Assert.False(Program.monitorTimer.Enabled);
            Assert.IsType<DryRunLockExecutor>(Program.lockExecutor);

            // Act 2: 用户中途关闭窗口
            window.Close();

            // Assert: 后台监控定时器必须恢复运行，DrillActive 必须清除，执行器必须恢复真实执行器
            Assert.False(Program.DrillActive);
            Assert.True(Program.monitorTimer.Enabled);
            Assert.IsType<RealLockExecutor>(Program.lockExecutor);

            // Clean up
            Program.monitorTimer.Stop();
            Program.monitorTimer.Dispose();
        }

        [Fact]
        public void DisposingWindowDuringDrill_RestoresMonitorTimerAndResetsDrillActive()
        {
            // Arrange
            Program.config = AppConfig.CreateDefault();
            Program.monitor = new MonitorStateMachine(Program.config, _ => { }, _ => { });
            Program.monitorTimer = new System.Windows.Forms.Timer();
            Program.monitorTimer.Start();
            Assert.True(Program.monitorTimer.Enabled);

            var window = new ConnectionTestWindow();
            window.BeginDisconnectTest();
            Assert.True(Program.DrillActive);

            // Act: 直接 Dispose
            window.Dispose();

            // Assert
            Assert.False(Program.DrillActive);
            Assert.True(Program.monitorTimer.Enabled);
            Assert.IsType<RealLockExecutor>(Program.lockExecutor);

            // Clean up
            Program.monitorTimer.Stop();
            Program.monitorTimer.Dispose();
        }
    }
}
