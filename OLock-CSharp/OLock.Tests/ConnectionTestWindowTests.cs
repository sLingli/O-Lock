using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Win32;
using OLock;

namespace OLock.Tests
{
    // 测试连接测试窗口生命周期、监控内核防重入/代次丢弃、休眠唤醒跨线程封送及托盘菜单延迟重建
    [Collection("GlobalState")]
    public class ConnectionTestWindowTests
    {
        [Fact]
        public void ClosingShownWindowDuringDrill_TriggersOnFormClosingAndRestoresMonitor()
        {
            var prevConfig = Program.config;
            var prevMonitor = Program.monitor;
            var prevTimer = Program.monitorTimer;
            var prevExecutor = Program.lockExecutor;
            using var testTimer = new System.Windows.Forms.Timer();
            try
            {
                Program.ResetShutdownStateForTest();
                Program.config = AppConfig.CreateDefault();
                Program.monitor = new MonitorStateMachine(Program.config, _ => { }, _ => { });
                Program.monitorTimer = testTimer;
                testTimer.Start();
                Assert.True(testTimer.Enabled);

                using var window = new ConnectionTestWindow
                {
                    ShowInTaskbar = false,
                    Opacity = 0
                };
                bool formClosingFired = false;
                window.FormClosing += (_, _) => formClosingFired = true;

                // 真实创建句柄并显示窗口，确保 Close() 走 OnFormClosing 而非跳过事件直接 Dispose
                window.Show();
                Assert.True(window.IsHandleCreated);

                window.BeginDisconnectTest();
                Assert.True(Program.DrillActive);
                Assert.False(testTimer.Enabled);
                Assert.IsType<DryRunLockExecutor>(Program.lockExecutor);

                window.Close();

                Assert.True(formClosingFired);
                Assert.False(Program.DrillActive);
                Assert.True(testTimer.Enabled);
                Assert.IsType<RealLockExecutor>(Program.lockExecutor);
            }
            finally
            {
                if (Program.DrillActive) Program.EndDrill(waitReconnect: false);
                testTimer.Stop();
                Program.ResetShutdownStateForTest();
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
            ConnectionTestWindow? window = null;
            try
            {
                Program.ResetShutdownStateForTest();
                Program.config = AppConfig.CreateDefault();
                Program.monitor = new MonitorStateMachine(Program.config, _ => { }, _ => { });
                Program.monitorTimer = testTimer;
                testTimer.Start();
                Assert.True(testTimer.Enabled);

                window = new ConnectionTestWindow();
                window.BeginDisconnectTest();
                Assert.True(Program.DrillActive);

                window.Dispose();
                window = null;

                Assert.False(Program.DrillActive);
                Assert.True(testTimer.Enabled);
                Assert.IsType<RealLockExecutor>(Program.lockExecutor);
            }
            finally
            {
                window?.Dispose();
                if (Program.DrillActive) Program.EndDrill(waitReconnect: false);
                testTimer.Stop();
                Program.ResetShutdownStateForTest();
                Program.monitorTimer = prevTimer;
                Program.monitor = prevMonitor;
                Program.config = prevConfig;
                Program.lockExecutor = prevExecutor;
            }
        }

        [Fact]
        public async Task PowerResumeDuringDrill_DoesNotRestartTimerAndInterruptsOnNextDrillTick()
        {
            var prevConfig = Program.config;
            var prevMonitor = Program.monitor;
            var prevTimer = Program.monitorTimer;
            var prevExecutor = Program.lockExecutor;
            var prevFacts = Program.monitorFactsProvider;
            var prevLock = Program.screenLockedProvider;
            string prevLang = Localization.currentLang;
            using var testTimer = new System.Windows.Forms.Timer();
            try
            {
                Program.ResetShutdownStateForTest();
                Program.ResetTickStateForTest();
                Localization.currentLang = "en";
                Program.config = AppConfig.CreateDefault();
                Program.monitor = new MonitorStateMachine(Program.config, _ => { }, _ => { });
                Program.monitorTimer = testTimer;
                Program.screenLockedProvider = () => false;
                // 模拟 S3 唤醒后主程序仍在运行，验证 DrillTick 会前置拦截而不会把状态推进到 Warmup
                Program.monitorFactsProvider = _ => Task.FromResult<(bool, bool?)>((true, false));
                testTimer.Start();

                using var window = new ConnectionTestWindow
                {
                    ShowInTaskbar = false,
                    Opacity = 0
                };
                window.Show();
                window.BeginDisconnectTest();
                Assert.True(Program.DrillActive);
                Assert.False(testTimer.Enabled);

                // Act 1: 演练期间模拟 S3 唤醒
                Program.OnPowerModeChanged(null!, new PowerModeChangedEventArgs(PowerModes.Resume));
                Assert.True(Program.monitor.IsWaitingForApp);
                Assert.False(testTimer.Enabled);

                // Act 2: 测试窗口下一拍 DrillTick 必须在 TickCoreAsync 前拦截并终止演练、复位状态栏文案
                await window.DrillTickAsync();

                Assert.False(Program.DrillActive);
                Assert.True(Program.monitor.IsWaitingForApp);
                Assert.False(Program.monitor.IsWarmup);
                Assert.True(testTimer.Enabled);
                Assert.Equal(Localization.Tr("test_disconnected"), window.StatusLabelTextForTest);
                Assert.Equal(Localization.Tr("test_drill_interrupted"), window.GuideLabelTextForTest);
            }
            finally
            {
                if (Program.DrillActive) Program.EndDrill(waitReconnect: false);
                testTimer.Stop();
                Program.ResetTickStateForTest();
                Program.ResetShutdownStateForTest();
                Program.monitorFactsProvider = prevFacts;
                Program.screenLockedProvider = prevLock;
                Program.monitorTimer = prevTimer;
                Program.monitor = prevMonitor;
                Program.config = prevConfig;
                Program.lockExecutor = prevExecutor;
                Localization.currentLang = prevLang;
            }
        }

        [Fact]
        public void ShutdownForExitDuringDrill_ThenClosingWindow_DoesNotThrowOnDisposedTimer()
        {
            var prevConfig = Program.config;
            var prevMonitor = Program.monitor;
            var prevTimer = Program.monitorTimer;
            var prevExecutor = Program.lockExecutor;
            var testTimer = new System.Windows.Forms.Timer();
            try
            {
                Program.ResetShutdownStateForTest();
                Program.config = AppConfig.CreateDefault();
                Program.monitor = new MonitorStateMachine(Program.config, _ => { }, _ => { });
                Program.monitorTimer = testTimer;
                testTimer.Start();

                using var window = new ConnectionTestWindow
                {
                    ShowInTaskbar = false,
                    Opacity = 0
                };
                window.Show();
                window.BeginDisconnectTest();
                Assert.True(Program.DrillActive);

                // 模拟退出流程先调用 ShutdownForExit()，随后 Application.Exit() 关闭演练窗口
                Program.ShutdownForExit();
                var closeEx = Record.Exception(() => window.Close());

                Assert.Null(closeEx);
                Assert.True(Program.IsShuttingDown);
                Assert.False(Program.DrillActive);
                Assert.Null(Program.monitorTimer);
            }
            finally
            {
                Program.ResetShutdownStateForTest();
                if (Program.DrillActive) Program.EndDrill(waitReconnect: false);
                try { testTimer.Dispose(); } catch { }
                Program.monitorTimer = prevTimer;
                Program.monitor = prevMonitor;
                Program.config = prevConfig;
                Program.lockExecutor = prevExecutor;
            }
        }

        [Fact]
        public async Task TickCoreAsync_InFlightCheck_SkipsDuplicateScanAndStillDrivesStaleExpiryAndHeartbeat()
        {
            string tempLog = Path.Combine(Path.GetTempPath(), $"olock_tick_inflight_{Guid.NewGuid():N}.log");
            string? prevLogPath = Logging.SwapLogFilePath(tempLog);
            var prevConfig = Program.config;
            var prevMonitor = Program.monitor;
            var prevFacts = Program.monitorFactsProvider;
            var prevLock = Program.screenLockedProvider;
            var errors = new List<string>();
            long nowMs = 10_000;
            int providerCalls = 0;
            var pendingCheck = new TaskCompletionSource<(bool, bool?)>(TaskCreationOptions.RunContinuationsAsynchronously);
            try
            {
                Program.ResetShutdownStateForTest();
                Program.ResetTickStateForTest();
                var cfg = AppConfig.CreateDefault();
                cfg.OfflineSeconds = 9;
                cfg.LogVerbose = true;
                Program.config = cfg;
                Program.monitor = new MonitorStateMachine(cfg, _ => { }, errors.Add, () => nowMs);
                Program.monitor.ForceOnlineForDrill(); // 对齐在线态并盖下 nowMs=10_000 时间戳
                Program.screenLockedProvider = () => false;
                Program.monitorFactsProvider = _ =>
                {
                    Interlocked.Increment(ref providerCalls);
                    return pendingCheck.Task;
                };

                // 第 1 拍发起后台检测并保持挂起
                Task firstTick = Program.TickCoreAsync();
                Assert.Equal(1, providerCalls);
                Assert.True(Program.isChecking);

                // 模拟后台检测挂起 30 秒，期间收到 30 个重叠 Tick
                for (int i = 1; i <= 30; i++)
                {
                    nowMs += 1000;
                    await Program.TickCoreAsync();
                }

                // 绝不重叠启动第二次后台扫描，但过期保护与 30s 心跳必须照常生效
                Assert.Equal(1, providerCalls);
                Assert.False(Program.monitor.IsOnline);
                Assert.Contains(errors, e => e.Contains("expired"));

                string[] logLines = Logging.ReadLogSnapshotLines();
                Assert.Contains(logLines, l => l.Contains("Heartbeat:") && l.Contains("checking: True"));

                pendingCheck.SetResult((true, true));
                await firstTick;
            }
            finally
            {
                pendingCheck.TrySetResult((false, null));
                Program.ResetTickStateForTest();
                Program.ResetShutdownStateForTest();
                Program.monitorFactsProvider = prevFacts;
                Program.screenLockedProvider = prevLock;
                Program.monitor = prevMonitor;
                Program.config = prevConfig;
                Logging.SwapLogFilePath(prevLogPath);
                try { if (File.Exists(tempLog)) File.Delete(tempLog); } catch { }
            }
        }

        [Fact]
        public async Task TickCoreAsync_GenerationChangeWhileInFlight_DiscardsStaleResult()
        {
            var prevConfig = Program.config;
            var prevMonitor = Program.monitor;
            var prevFacts = Program.monitorFactsProvider;
            var prevLock = Program.screenLockedProvider;
            var pendingCheck = new TaskCompletionSource<(bool, bool?)>(TaskCreationOptions.RunContinuationsAsynchronously);
            try
            {
                Program.ResetShutdownStateForTest();
                Program.ResetTickStateForTest();
                var cfg = AppConfig.CreateDefault();
                Program.config = cfg;
                Program.monitor = new MonitorStateMachine(cfg, _ => { }, _ => { });
                Program.monitor.ForceOnlineForDrill();
                Program.screenLockedProvider = () => false;
                Program.monitorFactsProvider = _ => pendingCheck.Task;

                Task inFlightTick = Program.TickCoreAsync();

                // 在途检测尚未返回时发生 S3 唤醒，推进 monitorGeneration 并复位回等待态
                Program.OnPowerModeChanged(null!, new PowerModeChangedEventArgs(PowerModes.Resume));
                Assert.True(Program.monitor.IsWaitingForApp);

                // 旧的在途检测此刻才返回“在线”，必须被代次检查丢弃，不能污染已复位状态
                pendingCheck.SetResult((true, true));
                await inFlightTick;

                Assert.True(Program.monitor.IsWaitingForApp);
                Assert.False(Program.monitor.IsOnline);
            }
            finally
            {
                pendingCheck.TrySetResult((false, null));
                Program.ResetTickStateForTest();
                Program.ResetShutdownStateForTest();
                Program.monitorFactsProvider = prevFacts;
                Program.screenLockedProvider = prevLock;
                Program.monitor = prevMonitor;
                Program.config = prevConfig;
            }
        }

        [Fact]
        public async Task OnPowerModeChanged_FromBackgroundThread_MarshalsToMainUiContext()
        {
            var prevConfig = Program.config;
            var prevMonitor = Program.monitor;
            var recordingCtx = new RecordingSyncContext();
            try
            {
                Program.ResetShutdownStateForTest();
                Program.config = AppConfig.CreateDefault();
                Program.monitor = new MonitorStateMachine(Program.config, _ => { }, _ => { });
                Program.monitor.ForceOnlineForDrill();
                Program.SetMainUiContextForTest(recordingCtx);

                // 模拟系统电源事件在后台线程回调
                await Task.Run(() =>
                    Program.OnPowerModeChanged(null!, new PowerModeChangedEventArgs(PowerModes.Resume)));

                Assert.Equal(1, recordingCtx.PostCount);
                Assert.True(Program.monitor.IsWaitingForApp);
            }
            finally
            {
                Program.SetMainUiContextForTest(null);
                Program.monitor = prevMonitor;
                Program.config = prevConfig;
            }
        }

        [Fact]
        public void TrayUI_UpdateContextMenu_WhenMenuVisible_DefersRebuildUntilClosed()
        {
            var prevConfig = Program.config;
            var prevMonitor = Program.monitor;
            var prevSyncCtx = SynchronizationContext.Current;
            var queuedCtx = new DeferredSyncContext();
            try
            {
                SynchronizationContext.SetSynchronizationContext(queuedCtx);
                Program.config = AppConfig.CreateDefault();
                Program.monitor = new MonitorStateMachine(Program.config, _ => { }, _ => { });
                TrayUI.InitTrayIcon();

                var tray = TrayUI.TrayIconForTest;
                Assert.NotNull(tray);
                var originalMenu = tray!.ContextMenuStrip;
                Assert.NotNull(originalMenu);

                // 在屏幕外展开菜单，模拟用户正打开托盘右键菜单
                originalMenu!.Show(new Point(-10000, -10000));
                Assert.True(originalMenu.Visible);

                // 菜单展开期间触发重建必须延迟，绝不能当场销毁正在显示的菜单
                TrayUI.UpdateContextMenu();
                Assert.True(TrayUI.HasPendingMenuRebuild);
                Assert.Same(originalMenu, tray.ContextMenuStrip);
                Assert.False(originalMenu.IsDisposed);

                // 关闭菜单时仅投递异步重建，不能在 Closed 事件栈内同步销毁自身
                originalMenu.Close();
                Assert.False(TrayUI.HasPendingMenuRebuild);
                Assert.Equal(1, queuedCtx.PendingCount);
                Assert.False(originalMenu.IsDisposed);

                // 模拟消息循环下一拍消费 Post 回调，完成新菜单替换与旧菜单释放
                queuedCtx.FlushAll();

                Assert.NotSame(originalMenu, tray.ContextMenuStrip);
                Assert.True(originalMenu.IsDisposed);
            }
            finally
            {
                TrayUI.DisposeTrayIcon();
                SynchronizationContext.SetSynchronizationContext(prevSyncCtx);
                Program.monitor = prevMonitor;
                Program.config = prevConfig;
            }
        }

        private sealed class DeferredSyncContext : SynchronizationContext
        {
            private readonly Queue<(SendOrPostCallback Callback, object? State)> queue = new();
            public int PendingCount => queue.Count;

            public override void Post(SendOrPostCallback d, object? state)
                => queue.Enqueue((d, state));

            public void FlushAll()
            {
                while (queue.Count > 0)
                {
                    var item = queue.Dequeue();
                    item.Callback(item.State);
                }
            }
        }

        private sealed class RecordingSyncContext : SynchronizationContext
        {
            public int PostCount { get; private set; }

            public override void Post(SendOrPostCallback d, object? state)
            {
                PostCount++;
                d(state);
            }
        }
    }
}
