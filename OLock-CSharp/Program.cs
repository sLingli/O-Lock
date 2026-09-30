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

        // 定时器 (替代后台线程)
        internal static System.Windows.Forms.Timer monitorTimer = null!;
        internal static bool isChecking = false;    // 防止并发执行连接检查
        private static int heartbeatTicks = 0;      // 详细模式心跳计数

        // 状态机当前状态的中文描述 (心跳日志用)
        private static string DescribeMonitorState()
        {
            if (monitor.IsWaitingForApp) return "灰色(等待进程)";
            if (monitor.IsWarmup) return $"黄色(缓冲期剩 {monitor.WarmupRemaining} 秒)";
            if (monitor.IsOnline) return "绿色(在线)";
            return $"红色(离线 {monitor.OfflineSeconds} 秒)";
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
                LogError("应用", $"UI 线程未处理异常: {Describe(e.Exception)}");
            AppDomain.CurrentDomain.UnhandledException += (s, e) =>
                LogError("应用", $"未处理异常: {Describe(e.ExceptionObject as Exception ?? new Exception(e.ExceptionObject?.ToString()))}");

            Application.EnableVisualStyles();

            // 检测系统语言 (供多语言提示使用)
            currentLang = GetUILanguage();

            // 单实例保护：已有实例在运行则提示并退出
            using Mutex singleInstance = new Mutex(true, @"Local\OLock_SingleInstance", out bool createdNew);
            if (!createdNew)
            {
                MessageBox.Show(Tr("tray_already_running", APP_NAME), APP_NAME, MessageBoxButtons.OK, MessageBoxIcon.Information);
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

            LogInfo("应用", $"{APP_NAME} v{Application.ProductVersion} 启动, 进程: {config.AppProcessName}, 语言: {currentLang}");
            LogInfo("应用", $"设置加载完成 - 自动睡眠: {autoSleep}, 自动关屏: {autoScreenOff}");

            // 初始化监控状态机
            monitor = new MonitorStateMachine(config, msg => LogInfo("监控", msg), msg => Log("监控", "ERROR", msg));

            // 初始化托盘图标
            InitTrayIcon();

            // 监听系统电源事件 (S3唤醒)
            SystemEvents.PowerModeChanged += OnPowerModeChanged;

            // 启动监控定时器 (UI 线程，1秒一跳)
            monitorTimer = new System.Windows.Forms.Timer();
            monitorTimer.Interval = 1000;
            monitorTimer.Tick += MonitorTick;
            StartWaitingForApp();

            // 运行消息循环
            Application.Run();
        }

        internal static void OnPowerModeChanged(object sender, PowerModeChangedEventArgs e)
        {
            if (e.Mode == PowerModes.Suspend)
            {
                LogInfo("电源", "系统进入睡眠 (Suspend)");
            }
            else if (e.Mode == PowerModes.Resume)
            {
                LogInfo("电源", "系统唤醒 (S3 Resume) → 重置监控");
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

        // MonitorTick 在 UI 线程上由 Forms.Timer 触发 (每 1 秒)。
        // 职责：收集事实 (屏幕锁定/进程存活/手机连接) → 交给状态机决策 → 执行动作 → 刷新图标。
        // 状态流转逻辑全部在 MonitorStateMachine (MonitorState.cs) 中，可单元测试。
        // 锁屏期间不收集事实 (与旧行为一致)；连接检查用 isChecking 防止上一秒的检查尚未完成。
        static async void MonitorTick(object? sender, EventArgs e)
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
                        ExecuteSleep();
                    else
                        TriggerLock();
                }
                UpdateIcon();

                // 详细模式心跳：每 30 秒一条状态摘要，用于判断"卡住没有、卡在哪一步"
                if (config.LogVerbose && !screenLocked && ++heartbeatTicks >= 30)
                {
                    heartbeatTicks = 0;
                    int staleSeconds = monitor.LastCheckSuccessTime == DateTime.MinValue
                        ? -1
                        : (int)(DateTime.Now - monitor.LastCheckSuccessTime).TotalSeconds;
                    LogDebug("监控", $"心跳: {DescribeMonitorState()}, 距上次成功检查 {staleSeconds} 秒, 检查进行中: {isChecking}");
                }
            }
            catch (Exception ex)
            {
                LogError("监控", $"MonitorTick 异常: {Describe(ex)}");
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
                    LogInfo("配置", $"检测到旧版配置文件 {CONFIG_FILE_NAME}，该文件已不再生效 (设置请用托盘右键 → 设置, 存于 HKCU\\Software\\OLock)");
                }
            }
            catch (Exception ex)
            {
                LogError("配置", $"旧版配置文件探测失败: {Describe(ex)}");
            }
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
                LogError("应用", $"读取开机自启状态失败: {Describe(ex)}");
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
                LogError("应用", $"开机自启切换失败: {Describe(ex)}");
            }
        }
    }
}
