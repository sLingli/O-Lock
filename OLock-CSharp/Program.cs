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
        internal static AppConfig config = AppConfig.CreateDefault();
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
        internal static event Action<string>? DrillReported;

        // 开始断开测试：暂停自动监控，锁屏/睡眠切换为模拟执行，并将状态机强制对齐为在线监控态。
        // 测试期间由测试窗口的定时器作为唯一驱动源 await TickCoreAsync()，杜绝双重 Tick。
        internal static void StartDrill()
        {
            if (DrillActive) return;
            DrillActive = true;
            monitorTimer?.Stop();
            isChecking = false;
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
            lockExecutor = realExecutor;
            if (!monitor.IsOnline)
            {
                if (waitReconnect)
                    monitor.EnterPostDrillWaitReconnect();
                else
                    monitor.ResetToWaiting();
            }
            UpdateIcon();
            monitorTimer?.Start();
            LogInfo("Drill", "Disconnect drill ended (monitoring resumed)");
        }

        // 定时器 (替代后台线程)
        internal static System.Windows.Forms.Timer monitorTimer = null!;
        internal static bool isChecking = false;    // 防止并发执行连接检查
        private static int heartbeatTicks = 0;      // 详细模式心跳计数
        private static Mutex? singleInstanceMutex;
        private static bool ownsSingleInstanceMutex;

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
                StartWaitingForApp();
            }
        }

        static void StartWaitingForApp()
        {
            monitor.ResetToWaiting();
            UpdateIcon();

            // 确保定时器在运行
            if (monitorTimer != null && !monitorTimer.Enabled)
                monitorTimer.Start();
        }

        // ==================== 核心：定时器驱动的监控外壳 ====================

        // MonitorTick 在 UI 线程上由 Forms.Timer 触发 (每 1 秒)，是断开演练之外唯一的驱动源。
        static async void MonitorTick(object? sender, EventArgs e) => await TickCoreAsync();

        // TickCoreAsync 是监控内核：收集事实 (屏幕锁定/进程存活/手机连接) → 交给状态机决策 → 经执行器执行动作 → 刷新图标。
        // 返回 Task 供测试窗口 await，保证每次 Tick 完整走完后再读取最新状态。
        // 锁屏/睡眠的真实动作由 lockExecutor 决定：演练期间被替换为 DryRunLockExecutor，决策链照走但不动真格。
        internal static async Task TickCoreAsync()
        {
            try
            {
                bool screenLocked = IsScreenLocked();

                bool appRunning = false;
                bool? phoneConnected = null;

                if (screenLocked)
                {
                    // 锁屏时强制重置防重入标志，避免解锁后卡死
                    isChecking = false;
                }
                else
                {
                    appRunning = await Task.Run(IsAppRunning);

                    // 等待阶段 (灰色) 只关心进程是否启动，不做连接检查
                    if (!isChecking && !monitor.IsWaitingForApp)
                    {
                        isChecking = true;
                        try
                        {
                            phoneConnected = await CheckPhoneConnectionAsync();
                        }
                        finally
                        {
                            isChecking = false;
                        }
                    }
                }

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
                if (config.LogVerbose && !screenLocked && ++heartbeatTicks >= 30)
                {
                    heartbeatTicks = 0;
                    int staleSeconds = monitor.LastCheckSuccessTime == DateTime.MinValue
                        ? -1
                        : (int)(DateTime.Now - monitor.LastCheckSuccessTime).TotalSeconds;
                    LogDebug("Monitor", $"Heartbeat: {DescribeMonitorState()}, last success check: {staleSeconds}s ago, checking: {isChecking}");
                }
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

        internal static void ShutdownForExit()
        {
            SystemEvents.PowerModeChanged -= OnPowerModeChanged;
            monitorTimer?.Stop();
            monitorTimer?.Dispose();
            HideTrayIcon();
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

        internal static void RestartIntoUpdatedExecutable(string exePath)
        {
            LogInfo("Update", $"Restarting into updated executable: {exePath}");
            ShutdownForExit();
            ReleaseSingleInstanceMutex();
            Process.Start(new ProcessStartInfo
            {
                FileName = exePath,
                Arguments = "--updated",
                WorkingDirectory = Path.GetDirectoryName(exePath) ?? AppContext.BaseDirectory,
                UseShellExecute = false
            });
            Application.Exit();
        }

        internal static void ExitForExternalUpdater()
        {
            LogInfo("Update", "Exiting current process for external update script");
            ShutdownForExit();
            ReleaseSingleInstanceMutex();
            Application.Exit();
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
                        string exePath = Process.GetCurrentProcess().MainModule!.FileName;
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
