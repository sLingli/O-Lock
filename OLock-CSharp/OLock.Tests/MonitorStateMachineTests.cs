using OLock;

namespace OLock.Tests
{
    // 监控状态机行为测试：覆盖解锁重置、缓冲期倒计时、离线累计、进程退出、锁屏暂停等路径
    public class MonitorStateMachineTests
    {
        private readonly List<string> _logs = new();
        private readonly List<string> _errors = new();

        private MonitorStateMachine Create(int offlineSeconds = 9, int warmupSeconds = 60)
        {
            var config = AppConfig.CreateDefault();
            config.OfflineSeconds = offlineSeconds;
            config.WarmupSeconds = warmupSeconds;
            return new MonitorStateMachine(config, _logs.Add, _errors.Add);
        }

        [Fact]
        public void Initial_State_IsWaitingForApp()
        {
            var m = Create();
            Assert.True(m.IsWaitingForApp);
            Assert.False(m.IsWarmup);
            Assert.False(m.IsOnline);
        }

        [Fact]
        public void Waiting_AppStarted_EntersWarmup()
        {
            var m = Create();
            m.Tick(screenLocked: false, appRunning: true, phoneConnected: null);
            Assert.True(m.IsWarmup);
            Assert.False(m.IsWaitingForApp);
            Assert.Equal(60, m.WarmupRemaining);
        }

        [Fact]
        public void Waiting_AppNotStarted_StaysWaiting()
        {
            var m = Create();
            m.Tick(screenLocked: false, appRunning: false, phoneConnected: null);
            Assert.True(m.IsWaitingForApp);
        }

        [Fact]
        public void Warmup_NullCheckResult_DoesNotCountDown()
        {
            var m = Create();
            m.Tick(false, appRunning: true, null);           // 进入缓冲期
            m.Tick(false, true, phoneConnected: null);       // 检查在途，本秒跳过
            Assert.Equal(60, m.WarmupRemaining);
        }

        [Fact]
        public void Warmup_CountsDownOnePerSecond()
        {
            var m = Create();
            m.Tick(false, true, null);
            m.Tick(false, true, phoneConnected: false);
            m.Tick(false, true, phoneConnected: false);
            Assert.Equal(58, m.WarmupRemaining);
        }

        [Fact]
        public void Warmup_PhoneConnected_GoesOnline()
        {
            var m = Create();
            m.Tick(false, true, null);
            m.Tick(false, true, phoneConnected: true);
            Assert.False(m.IsWarmup);
            Assert.True(m.IsOnline);
            Assert.Equal(0, m.OfflineSeconds);
        }

        [Fact]
        public void Warmup_Timeout_ReturnsLockAndResets()
        {
            var m = Create(warmupSeconds: 60);
            m.Tick(false, true, null);
            MonitorAction action = MonitorAction.None;
            for (int i = 0; i < 59; i++)
                action = m.Tick(false, true, phoneConnected: false);
            Assert.Equal(1, m.WarmupRemaining);
            Assert.Equal(MonitorAction.None, action);

            action = m.Tick(false, true, phoneConnected: false); // 第 60 秒
            Assert.Equal(MonitorAction.Lock, action);
            Assert.True(m.IsWaitingForApp);
            Assert.Contains(_logs, l => l.Contains("缓冲期超时"));
        }

        [Fact]
        public void Warmup_AppExited_GoesWaiting()
        {
            var m = Create();
            m.Tick(false, true, null);
            m.Tick(screenLocked: false, appRunning: false, null);
            Assert.True(m.IsWaitingForApp);
        }

        [Fact]
        public void Monitoring_OfflineAccumulates_LocksAtThreshold()
        {
            var m = Create(offlineSeconds: 9);
            m.Tick(false, true, null);   // 缓冲期
            m.Tick(false, true, true);   // 在线，进入监控
            MonitorAction action = MonitorAction.None;
            for (int i = 0; i < 8; i++)
                action = m.Tick(false, true, phoneConnected: false);
            Assert.Equal(8, m.OfflineSeconds);
            Assert.False(m.IsOnline);
            Assert.Equal(MonitorAction.None, action);

            action = m.Tick(false, true, phoneConnected: false); // 第 9 秒
            Assert.Equal(MonitorAction.Lock, action);
            Assert.Equal(0, m.OfflineSeconds);   // 计数清零
            Assert.False(m.IsWaitingForApp);     // 仍处于监控阶段，等待锁屏后的解锁事件
            Assert.False(m.IsWarmup);
            Assert.Contains(_logs, l => l.Contains("执行锁屏"));
        }

        [Fact]
        public void Monitoring_Reconnect_ResetsOfflineSeconds()
        {
            var m = Create();
            m.Tick(false, true, null);
            m.Tick(false, true, true);
            for (int i = 0; i < 3; i++) m.Tick(false, true, phoneConnected: false);
            Assert.Equal(3, m.OfflineSeconds);
            m.Tick(false, true, phoneConnected: true);
            Assert.Equal(0, m.OfflineSeconds);
            Assert.True(m.IsOnline);
        }

        [Fact]
        public void Monitoring_AppExited_GoesWaiting()
        {
            var m = Create();
            m.Tick(false, true, null);
            m.Tick(false, true, true);
            m.Tick(screenLocked: false, appRunning: false, null);
            Assert.True(m.IsWaitingForApp);
            Assert.False(m.IsOnline);
        }

        [Fact]
        public void Monitoring_NullCheckResult_DoesNotCountOffline()
        {
            var m = Create();
            m.Tick(false, true, null);
            m.Tick(false, true, true);
            m.Tick(false, true, phoneConnected: null);
            Assert.Equal(0, m.OfflineSeconds);
            Assert.True(m.IsOnline);
        }

        [Fact]
        public void Locked_PausesMonitoring_UnlockResets()
        {
            var m = Create();
            m.Tick(false, true, null);
            m.Tick(false, true, true);
            Assert.True(m.IsOnline);

            m.Tick(screenLocked: true, appRunning: false, null);
            Assert.True(m.WasLocked);
            Assert.True(m.IsOnline); // 锁屏期间状态冻结

            m.Tick(screenLocked: false, appRunning: false, null); // 解锁
            Assert.False(m.WasLocked);
            Assert.True(m.IsWaitingForApp);
            Assert.False(m.IsOnline);
        }

        [Fact]
        public void Warmup_LockedThenUnlocked_Resets()
        {
            var m = Create();
            m.Tick(false, true, null);
            m.Tick(screenLocked: true, appRunning: false, null);
            m.Tick(screenLocked: false, appRunning: false, null);
            Assert.True(m.IsWaitingForApp);
        }

        [Fact]
        public void StaleOnline_IsForcedOffline()
        {
            var m = Create(offlineSeconds: 9);
            m.Tick(false, true, null);
            m.Tick(false, true, true);
            m.LastCheckSuccessTime = DateTime.Now.AddSeconds(-28); // 超过 9*3=27 秒无检查结果
            m.Tick(false, true, phoneConnected: null);
            Assert.False(m.IsOnline);
            Assert.Contains(_errors, e => e.Contains("检查结果过期"));
        }

        [Fact]
        public void ResetToWaiting_ClearsState()
        {
            var m = Create();
            m.Tick(false, true, null);
            m.Tick(false, true, true);
            m.ResetToWaiting();
            Assert.True(m.IsWaitingForApp);
            Assert.False(m.IsOnline);
            Assert.False(m.IsWarmup);
            Assert.Equal(0, m.OfflineSeconds);
            Assert.Equal(DateTime.MinValue, m.LastCheckSuccessTime);
        }
    }
}
