// OLock - Phone offline auto-lock tool (C# Edition)
// 手机离线自动锁屏工具 - 通过检测 OPPO 互联软件的网络连接状态判断手机是否在线

using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
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

        // 用户偏好
        internal static bool autoSleep = false;
        internal static bool autoScreenOff = false;

        // 监控状态机（纯逻辑，见 MonitorState.cs）
        internal static MonitorStateMachine monitor = null!;

        // 定时器 (替代后台线程)
        internal static System.Windows.Forms.Timer monitorTimer = null!;
        internal static bool isChecking = false;    // 防止并发执行连接检查

        [DllImport("kernel32.dll")]
        private static extern IntPtr GetConsoleWindow();

        [DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        [STAThread]
        private static void Main(string[] args)
        {
            InitLogger();
            config = AppConfig.CreateDefault();

            // 隐藏控制台窗口
            var handle = GetConsoleWindow();
            if (handle != IntPtr.Zero)
                ShowWindow(handle, 0); // SW_HIDE = 0

            // 检测系统语言
            currentLang = GetUILanguage();

            // 先从注册表加载全部设置，再用 JSON 覆盖（如果存在）
            LoadSettings();
            if (LoadJsonConfigIfExists())
                config.Normalize();

            // 清理超过保留期的日志
            CleanupExpiredEntries();

            LogInfo($"{APP_NAME} 启动, 进程: {config.AppProcessName}, 语言: {currentLang}");
            LogInfo($"设置加载完成 - 自动睡眠: {autoSleep}, 自动关屏: {autoScreenOff}");

            // 初始化监控状态机
            monitor = new MonitorStateMachine(config, LogInfo, LogError);

            // 初始化托盘图标
            Application.EnableVisualStyles();
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
            if (e.Mode == PowerModes.Resume)
            {
                LogInfo("系统唤醒 (S3 Resume)");
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
            }
            catch (Exception ex)
            {
                LogError($"MonitorTick 异常: {ex.Message}");
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
            catch { return false; }
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
            catch { }
        }
    }
}
