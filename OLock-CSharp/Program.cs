// OLock - Phone offline auto-lock tool (C# Edition)
// 手机离线自动锁屏工具 - 通过检测 OPPO 互联软件的网络连接状态判断手机是否在线

using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Win32;

using static OLock.AppConfigStorage;
using static OLock.LockActions;
using static OLock.Localization;
using static OLock.Logging;
using static OLock.TcpConnectionChecker;
using static OLock.TrayUI;

namespace OLock
{
    // 程序外壳：负责初始化、每秒收集事实 (屏幕锁定/进程存活/手机连接) 并执行监控决策的动作
    internal static class Program
    {
        // ================== 配置项 ==================
        internal const string APP_NAME = "OLock";
        internal const string CONFIG_FILE_NAME = "olock.config.json";
        internal static volatile AppConfig config = AppConfig.CreateDefault();
        // ============================================

        // 应用版本号 (来自 csproj 的 <Version>)，如 "1.2.0"；完整版本含提交哈希，见 Application.ProductVersion
        internal static string AppVersion { get; } = Application.ProductVersion.Split('+')[0];

        // 用户偏好
        internal static bool autoSleep = false;
        internal static bool autoScreenOff = false;

        // 监控状态机（纯逻辑，见 MonitorState.cs）
        internal static MonitorStateMachine monitor = null!;

        // 锁屏/睡眠执行器：使用环境 = RealLockExecutor，测试环境（断开演练）= DryRunLockExecutor
        internal static ILockExecutor lockExecutor = new RealLockExecutor();

        private static readonly ILockExecutor realExecutor = new RealLockExecutor();

        // ==================== 断开测试（由连接测试窗口驱动） ====================

        // 断开测试进行中标志；DrillReported 在模拟执行器被调用时触发（UI 线程），供测试窗口展示报告
        internal static bool DrillActive { get; private set; }
        internal static bool IsShuttingDown { get; private set; }
        internal static event Action<string>? DrillReported;

        internal static void ResetShutdownStateForTest() => IsShuttingDown = false;

        // 开始断开测试：暂停自动监控，锁屏/睡眠切换为模拟执行，并将状态机强制对齐为在线监控态。
        // 测试期间由测试窗口的定时器作为唯一驱动源 await TickCoreAsync()，杜绝双重 Tick。
        internal static void StartDrill()
        {
            if (DrillActive) return;
            DrillActive = true;
            monitorTimer?.Stop();
            monitorGeneration++;
            lockExecutor = new DryRunLockExecutor(msg =>
            {
                LogInfo("Drill", msg);
                DrillReported?.Invoke(msg);
            });
            monitor.ForceOnlineForDrill();
            UpdateIcon();
            LogInfo("Drill", "Disconnect drill started (auto-monitoring paused, lock/sleep simulated)");
        }

        // 结束断开测试：恢复真实执行器与自动监控。幂等，任意时刻可安全调用。
        // 若因离线触发模拟锁屏而结束，进入等待手机重连的安全态，防止测完后未重连被真睡眠/真锁屏。
        internal static void EndDrill(bool waitReconnect = true)
        {
            if (!DrillActive) return;
            DrillActive = false;
            monitorGeneration++;
            lockExecutor = realExecutor;
            if (!monitor.IsOnline)
            {
                if (waitReconnect)
                    monitor.EnterPostDrillWaitReconnect();
                else
                    monitor.ResetToWaiting();
            }
            if (!IsShuttingDown)
            {
                UpdateIcon();
                monitorTimer?.Start();
            }
            LogInfo("Drill", "Disconnect drill ended (monitoring resumed)");
        }

        // 定时器 (替代后台线程)
        internal static System.Windows.Forms.Timer monitorTimer = null!;
        internal static bool isChecking = false;    // 当前是否处于后台连接检测中（供心跳诊断）
        private static int heartbeatTicks = 0;      // 详细模式心跳计数
        private static bool inFlightHeartbeatCounted = false; // 慢扫描跨秒时防止在途跳与完成跳重复计次
        private static Task<(bool AppRunning, bool? PhoneConnected)>? inFlightFactTask;
        private static int monitorGeneration = 0;   // 模式切换代次，丢弃状态变化前的在途检查结果
        private static SynchronizationContext? mainUiContext;
        private static Mutex? singleInstanceMutex;
        private static bool ownsSingleInstanceMutex;
        private static readonly CancellationTokenSource startupUpdateCts = new CancellationTokenSource();

        // 可注入测试钩子（默认直连真实系统查询）
        internal static Func<bool, Task<(bool AppRunning, bool? PhoneConnected)>> monitorFactsProvider = CheckMonitorFactsAsync;
        internal static Func<bool> screenLockedProvider = IsScreenLocked;

        internal static void SetMainUiContextForTest(SynchronizationContext? ctx) => mainUiContext = ctx;

        internal static void ResetTickStateForTest()
        {
            inFlightFactTask = null;
            inFlightHeartbeatCounted = false;
            isChecking = false;
            heartbeatTicks = 0;
        }

        // 状态机当前状态的英文描述 (心跳日志用)
        private static string DescribeMonitorState()
        {
            if (monitor.IsWaitingForApp) return "WaitingForApp";
            if (monitor.WaitReconnectAfterDrill) return "WaitReconnectAfterDrill";
            if (monitor.IsWarmup) return $"Warmup({monitor.WarmupRemaining}s)";
            if (monitor.IsOnline) return "Online";
            return $"Offline({monitor.OfflineSeconds}s)";
        }

        [DllImport("kernel32.dll")]
        private static extern IntPtr GetConsoleWindow();

        [DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        [STAThread]
        private static void Main(string[] args)
        {
            InitLogger();

            // 全局异常兜底：任何未捕获异常都写入日志，避免静默崩溃无迹可查
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            Application.ThreadException += (s, e) =>
                LogError("App", $"Unhandled UI exception: {Describe(e.Exception)}");
            AppDomain.CurrentDomain.UnhandledException += (s, e) =>
                LogError("App", $"Unhandled exception: {Describe(e.ExceptionObject as Exception ?? new Exception(e.ExceptionObject?.ToString()))}");

            Application.EnableVisualStyles();

            // 检测系统语言 (供多语言提示使用)
            currentLang = GetUILanguage();

            // 单实例保护：已有实例在运行则发信号唤起其「连接测试」窗口并退出
            using EventWaitHandle showTestEvent = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\OLock_ShowTestWindow");
            bool isUpdatedRestart = Array.Exists(args, a => string.Equals(a, "--updated", StringComparison.OrdinalIgnoreCase));
            using Mutex singleInstance = new Mutex(false, @"Local\OLock_SingleInstance");
            singleInstanceMutex = singleInstance;
            try
            {
                ownsSingleInstanceMutex = singleInstance.WaitOne(isUpdatedRestart ? TimeSpan.FromSeconds(10) : TimeSpan.Zero);
            }
            catch (AbandonedMutexException)
            {
                ownsSingleInstanceMutex = true;
            }
            if (!ownsSingleInstanceMutex)
            {
                if (!isUpdatedRestart) showTestEvent.Set();
                return;
            }

            config = AppConfig.CreateDefault();

            // 隐藏控制台窗口
            var handle = GetConsoleWindow();
            if (handle != IntPtr.Zero)
                ShowWindow(handle, 0); // SW_HIDE = 0

            // 旧版 olock.config.json 已不再生效，存在时提示一次
            LogIfLegacyConfigPresent();

            // 注册表是唯一配置源；Normalize 无条件执行，不因读取异常被跳过
            LoadSettings();
            config.Normalize();

            // 清理超过保留期的日志
            CleanupExpiredEntries();
            UpdateService.CleanupUpdateLeftovers();
            if (isUpdatedRestart)
                LogInfo("Update", $"Restarted into updated version v{AppVersion}");

            LogInfo("App", $"{APP_NAME} v{Application.ProductVersion} started, process: {config.AppProcessName}, language: {currentLang}");
            LogInfo("App", $"Settings loaded - AutoSleep: {autoSleep}, AutoScreenOff: {autoScreenOff}");

            // 初始化监控状态机
            monitor = new MonitorStateMachine(config, msg => LogInfo("Monitor", msg), msg => Log("Monitor", "ERROR", msg));

            // 初始化托盘图标
            InitTrayIcon();

            // 监听系统电源事件 (S3唤醒)
            SystemEvents.PowerModeChanged += OnPowerModeChanged;

            // 启动监控定时器 (UI 线程，1秒一跳)
            monitorTimer = new System.Windows.Forms.Timer();
            monitorTimer.Interval = 1000;
            monitorTimer.Tick += MonitorTick;
            StartWaitingForApp();

            // 监听二次启动信号：当托盘被 Win11 隐藏在溢出区时，再次双击 OLock.exe 即可直接唤起连接测试窗口
            var uiContext = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();
            SynchronizationContext.SetSynchronizationContext(uiContext);
            mainUiContext = uiContext;
            RegisteredWaitHandle waitHandle = ThreadPool.RegisterWaitForSingleObject(
                showTestEvent,
                (_, timedOut) =>
                {
                    if (!timedOut)
                    {
                        uiContext.Post(_ =>
                        {
                            LogInfo("App", "Received secondary launch signal, opening connection test window");
                            ConnectionTestForm.ShowTestWindow();
                        }, null);
                    }
                },
                null,
                -1,
                executeOnlyOnce: false);

            if (Array.Exists(args, a => string.Equals(a, "--test", StringComparison.OrdinalIgnoreCase)))
            {
                uiContext.Post(_ => ConnectionTestForm.ShowTestWindow(), null);
            }

            if (config.CheckUpdatesOnStartup)
            {
                _ = CheckUpdatesOnStartupAsync(uiContext);
            }

            // 运行消息循环
            try
            {
                Application.Run();
            }
            finally
            {
                waitHandle.Unregister(null);
                ReleaseSingleInstanceMutex();
            }
        }

        internal static void OnPowerModeChanged(object sender, PowerModeChangedEventArgs e)
        {
            if (e.Mode == PowerModes.Suspend)
            {
                LogInfo("Power", "System entering suspend");
            }
            else if (e.Mode == PowerModes.Resume)
            {
                LogInfo("Power", "System resumed (S3 Resume) -> resetting monitor");
                if (mainUiContext != null && SynchronizationContext.Current != mainUiContext)
                    mainUiContext.Post(_ => StartWaitingForApp(), null);
                else
                    StartWaitingForApp();
            }
        }

        static void StartWaitingForApp()
        {
            monitorGeneration++;
            monitor.ResetToWaiting();
            UpdateIcon();

            // 演练期间由测试窗口的 drillTimer 独占驱动（测试窗口下一拍检测到 WaitingForApp 会自行结束演练并恢复 monitorTimer）
            if (!DrillActive && monitorTimer != null && !monitorTimer.Enabled)
                monitorTimer.Start();
        }

        // ==================== 核心：定时器驱动的监控外壳 ====================

        // MonitorTick 在 UI 线程上由 Forms.Timer 触发 (每 1 秒)，是断开演练之外唯一的驱动源。
        static async void MonitorTick(object? sender, EventArgs e) => await TickCoreAsync();

        private static void AdvanceMonitorAndHeartbeat(bool screenLocked, bool appRunning, bool? phoneConnected, bool countHeartbeat = true)
        {
            var action = monitor.Tick(screenLocked, appRunning, phoneConnected);
            if (action == MonitorAction.Lock)
            {
                if (autoSleep)
                    lockExecutor.ExecuteSleep();
                else
                    lockExecutor.ExecuteLock();
            }
            UpdateIcon();

            // 详细模式心跳：每 30 秒一条状态摘要，用于判断"卡住没有、卡在哪一步"
            if (countHeartbeat && config.LogVerbose && !screenLocked && ++heartbeatTicks >= 30)
            {
                heartbeatTicks = 0;
                int staleSeconds = monitor.GetSecondsSinceLastCheck();
                LogDebug("Monitor", $"Heartbeat: {DescribeMonitorState()}, last success check: {staleSeconds}s ago, checking: {isChecking}");
            }
        }

        // TickCoreAsync 是监控内核：收集事实 (屏幕锁定/进程存活/手机连接) → 交给状态机决策 → 经执行器执行动作 → 刷新图标。
        // 返回 Task 供测试窗口 await，保证每次 Tick 完整走完后再读取最新状态。
        // 上一轮检测未结束（含已完成但 UI 延续尚在排队）时不重叠发起新扫描，但仍以 phoneConnected=null 推进状态机以保障过期保护与 30s 心跳诊断。
        internal static async Task TickCoreAsync()
        {
            if (IsShuttingDown)
                return;

            try
            {
                bool screenLocked = screenLockedProvider();
                if (screenLocked)
                {
                    if (!monitor.WasLocked)
                        monitorGeneration++;
                    AdvanceMonitorAndHeartbeat(screenLocked: true, appRunning: false, phoneConnected: null, countHeartbeat: false);
                    return;
                }

                // 解锁首拍：状态机将直接复位回等待态，无需做一次注定被丢弃的后台扫描
                if (monitor.WasLocked)
                {
                    monitorGeneration++;
                    AdvanceMonitorAndHeartbeat(screenLocked: false, appRunning: false, phoneConnected: null, countHeartbeat: false);
                    return;
                }

                // 上一轮任务尚未完成或 UI 延续尚未收尾：绝不重叠启动第二轮扫描，同时继续驱动过期保护与 30s 心跳
                if (inFlightFactTask != null)
                {
                    inFlightHeartbeatCounted = true;
                    AdvanceMonitorAndHeartbeat(screenLocked: false, appRunning: !monitor.IsWaitingForApp, phoneConnected: null, countHeartbeat: true);
                    return;
                }

                int tickGen = monitorGeneration;
                bool needConnectionCheck = !monitor.IsWaitingForApp;
                Task<(bool AppRunning, bool? PhoneConnected)>? factTask = null;
                bool appRunning;
                bool? phoneConnected;
                try
                {
                    isChecking = needConnectionCheck;
                    inFlightHeartbeatCounted = false;
                    factTask = monitorFactsProvider(needConnectionCheck);
                    inFlightFactTask = factTask;
                    (appRunning, phoneConnected) = await factTask;
                }
                finally
                {
                    if (factTask == null || ReferenceEquals(inFlightFactTask, factTask))
                    {
                        inFlightFactTask = null;
                        isChecking = false;
                    }
                }

                // 后台检测期间若发生了锁屏、演练启停、休眠唤醒或程序退出，丢弃已过时的在途结果
                if (tickGen != monitorGeneration || IsShuttingDown)
                    return;

                AdvanceMonitorAndHeartbeat(screenLocked: false, appRunning, phoneConnected, countHeartbeat: !inFlightHeartbeatCounted);
            }
            catch (Exception ex)
            {
                LogError("Monitor", $"MonitorTick exception: {Describe(ex)}");
            }
        }

        // 旧版 olock.config.json 自 v1.3.2 起不再生效。存在时提示一次，
        // 免得用户以为设置是从那个文件读的（这正是旧版「改了设置不生效」的症结）。
        internal static void LogIfLegacyConfigPresent()
        {
            try
            {
                if (File.Exists(Path.Combine(AppContext.BaseDirectory, CONFIG_FILE_NAME)) ||
                    File.Exists(Path.Combine(Environment.CurrentDirectory, CONFIG_FILE_NAME)))
                {
                    LogInfo("Config", $"Legacy config file {CONFIG_FILE_NAME} detected and ignored (settings are stored in HKCU\\Software\\OLock)");
                }
            }
            catch (Exception ex)
            {
                LogError("Config", $"Failed to probe legacy config file: {Describe(ex)}");
            }
        }

        private static async Task CheckUpdatesOnStartupAsync(SynchronizationContext uiContext)
        {
            try
            {
                CancellationToken token = startupUpdateCts.Token;
                await Task.Delay(TimeSpan.FromSeconds(5), token).ConfigureAwait(false);
                var result = await UpdateService.CheckForUpdateAsync(token).ConfigureAwait(false);
                if (!token.IsCancellationRequested && result.Status == UpdateCheckStatus.UpdateAvailable)
                {
                    uiContext.Post(_ => NotifyUpdateAvailable(result), null);
                }
            }
            catch (OperationCanceledException) when (startupUpdateCts.IsCancellationRequested)
            {
            }
            catch (Exception ex)
            {
                LogError("Update", $"Startup update check failed unexpectedly: {Describe(ex)}");
            }
        }

        internal static void CancelStartupUpdateCheck()
        {
            try
            {
                startupUpdateCts.Cancel();
            }
            catch
            {
            }
        }

        internal static void ShutdownForExit()
        {
            IsShuttingDown = true;
            monitorGeneration++;
            CancelStartupUpdateCheck();
            if (DrillActive)
                EndDrill(waitReconnect: false);
            SystemEvents.PowerModeChanged -= OnPowerModeChanged;
            var timer = monitorTimer;
            monitorTimer = null!;
            if (timer != null)
            {
                try { timer.Stop(); } catch { }
                try { timer.Dispose(); } catch { }
            }
            DisposeTrayIcon();
        }

        private static void ReleaseSingleInstanceMutex()
        {
            if (!ownsSingleInstanceMutex || singleInstanceMutex == null)
                return;
            ownsSingleInstanceMutex = false;
            try
            {
                singleInstanceMutex.ReleaseMutex();
            }
            catch
            {
            }
        }

        private static void ScheduleForcedExitWatchdog(int delayMs = 2000)
        {
            var watchdog = new Thread(() =>
            {
                Thread.Sleep(delayMs);
                Environment.Exit(0);
            })
            {
                IsBackground = true,
                Name = "OLockUpdateExitWatchdog"
            };
            watchdog.Start();
        }

        internal static Func<ProcessStartInfo, Process?> processStarter = Process.Start;

        internal static void RestartIntoUpdatedExecutable(string exePath)
        {
            LogInfo("Update", $"Restarting into updated executable: {exePath}");
            // 先拉起新进程（--updated 最多等待 10s 接管单实例锁），确认启动成功后再销毁旧进程托盘与监控，
            // 避免新版被拦截拉起失败时旧进程变成无托盘孤儿
            using var started = processStarter(new ProcessStartInfo
            {
                FileName = exePath,
                Arguments = "--updated",
                WorkingDirectory = Path.GetDirectoryName(exePath) ?? AppContext.BaseDirectory,
                UseShellExecute = false
            });
            if (started == null)
                throw new InvalidOperationException($"Failed to start updated executable: {exePath}");

            ShutdownForExit();
            ReleaseSingleInstanceMutex();
            Application.Exit();
            ScheduleForcedExitWatchdog();
        }

        internal static void ExitForExternalUpdater()
        {
            LogInfo("Update", "Exiting current process for external update script");
            ShutdownForExit();
            ReleaseSingleInstanceMutex();
            Application.Exit();
            ScheduleForcedExitWatchdog();
        }

        internal static bool IsAutostartEnabled()
        {
            try
            {
                using (var key = Registry.CurrentUser.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Run", false))
                {
                    return key?.GetValue(APP_NAME) != null;
                }
            }
            catch (Exception ex)
            {
                LogError("App", $"Failed to read autostart status: {Describe(ex)}");
                return false;
            }
        }

        internal static void ToggleAutostart()
        {
            try
            {
                using (var key = Registry.CurrentUser.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Run", true))
                {
                    if (key == null) return;

                    if (IsAutostartEnabled())
                    {
                        key.DeleteValue(APP_NAME, false);
                    }
                    else
                    {
                        string? exePath = UpdateService.GetCurrentExecutablePath();
                        if (!string.IsNullOrWhiteSpace(exePath))
                            key.SetValue(APP_NAME, $"\"{exePath}\"");
                    }
                }
            }
            catch (Exception ex)
            {
                LogError("App", $"Failed to toggle autostart: {Describe(ex)}");
            }
        }
    }
}
