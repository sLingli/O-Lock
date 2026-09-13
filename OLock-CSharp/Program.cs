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

        // 全局状态
        internal static int offlineSeconds = 0;
        internal static bool isOnline = false;
        internal static bool isWarmup = false;
        internal static bool isWaitingForApp = true;
        internal static int warmupRemaining = 0;
        internal static bool wasLocked = false;
        internal static bool autoSleep = false;
        internal static bool autoScreenOff = false;

        // 定时器 (替代后台线程)
        internal static System.Windows.Forms.Timer monitorTimer;
        internal static bool isChecking = false;    // 防止并发执行连接检查
        internal static DateTime lastCheckSuccessTime = DateTime.MinValue; // 最后一次成功检查的时间

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

            LogInfo($"{APP_NAME} 启动, 进程: {config.AppProcessName}, 语言: {currentLang}");
            LogInfo($"设置加载完成 - 自动睡眠: {autoSleep}, 自动关屏: {autoScreenOff}");

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
            isWaitingForApp = true;
            isWarmup = false;
            isOnline = false;
            offlineSeconds = 0;
            isChecking = false;
            lastCheckSuccessTime = DateTime.MinValue;
            LogInfo("状态: 等待应用启动");
            UpdateIcon();

            // 确保定时器在运行
            if (monitorTimer != null && !monitorTimer.Enabled)
                monitorTimer.Start();
        }

        static void StartWarmup()
        {
            int warmupTime = Math.Max(config.MinWarmupSeconds, Math.Min(config.MaxWarmupSeconds, config.WarmupSeconds));
            isWarmup = true;
            isWaitingForApp = false;
            warmupRemaining = warmupTime;
            offlineSeconds = 0;
            isOnline = false;
            isChecking = false;
            LogInfo($"状态: 缓冲期开始 ({warmupTime}秒)");
            UpdateIcon();
        }

        // ==================== 核心：定时器驱动的监控逻辑 ====================

        // MonitorTick 在 UI 线程上由 Forms.Timer 触发 (每 1 秒)
        // 所有 UI 操作 (UpdateIcon) 天然在 UI 线程上，无跨线程问题
        // 阻塞 I/O (IsAppRunning, CheckPhoneConnection) 通过 Task.Run + await 在后台执行
        // async void 仅用于事件处理器，WinForms Timer 可以安全使用
        static async void MonitorTick(object sender, EventArgs e)
        {
            try
            {
                // 0. isOnline 过期保护：如果太久没有成功检查结果，强制标记离线
                if (isOnline && lastCheckSuccessTime != DateTime.MinValue)
                {
                    double staleSeconds = config.OfflineSeconds * 3;
                    if ((DateTime.Now - lastCheckSuccessTime).TotalSeconds > staleSeconds)
                    {
                        LogError($"检查结果过期 ({(int)(DateTime.Now - lastCheckSuccessTime).TotalSeconds}秒无结果)，强制标记离线");
                        isOnline = false;
                        UpdateIcon();
                    }
                }

                // 1. 检测屏幕锁定状态 (非阻塞 API 调用)
                bool currentlyLocked = IsScreenLocked();

                // 从锁定变为解锁
                if (wasLocked && !currentlyLocked)
                {
                    wasLocked = false;
                    LogInfo("屏幕解锁");
                    StartWaitingForApp();
                    return; // StartWaitingForApp 已更新图标
                }

                wasLocked = currentlyLocked;

                // 屏幕锁定时暂停监控
                if (currentlyLocked)
                {
                    if (isChecking) isChecking = false; // 锁屏时强制重置，避免解锁后卡死
                    return;
                }

                // 2. 等待主程序启动阶段 (灰色) — IsAppRunning 移到后台线程
                if (isWaitingForApp)
                {
                    if (await Task.Run(IsAppRunning))
                    {
                        StartWarmup();
                    }
                    else
                    {
                        UpdateIcon();
                    }
                    return;
                }

                // 3. 缓冲期阶段 (黄色)
                if (isWarmup)
                {
                    // IsAppRunning 移到后台线程，避免阻塞 UI
                    if (!await Task.Run(IsAppRunning))
                    {
                        StartWaitingForApp();
                        return;
                    }

                    // 每秒检查一次手机连接，warmupRemaining 按真实秒数倒计时
                    if (!isChecking)
                    {
                        isChecking = true;
                        try
                        {
                            bool connected = await CheckPhoneConnectionAsync();

                            // 检查结果有效，更新时间戳
                            lastCheckSuccessTime = DateTime.Now;

                            if (connected)
                            {
                                isWarmup = false;
                                warmupRemaining = 0;
                                offlineSeconds = 0;
                                isOnline = true;
                                LogInfo("状态: 手机已连接 (缓冲期内)");
                            }
                            else
                            {
                                warmupRemaining--;
                                if (warmupRemaining <= 0)
                                {
                                    LogInfo("缓冲期超时，手机未连接");
                                    if (autoSleep)
                                        ExecuteSleep();
                                    else
                                        TriggerLock();
                                    StartWaitingForApp();
                                    return;
                                }
                            }
                            UpdateIcon();
                        }
                        finally
                        {
                            isChecking = false;
                        }
                    }
                    return;
                }

                // 4. 正常监控阶段 (绿色/红色) — IsAppRunning 移到后台线程
                if (!await Task.Run(IsAppRunning))
                {
                    StartWaitingForApp();
                    UpdateIcon();
                    return;
                }

                // 每秒检查一次连接，离线按秒累计，达到容忍秒数后锁屏
                if (!isChecking)
                {
                    isChecking = true;
                    try
                    {
                        bool phoneConnected = await CheckPhoneConnectionAsync();

                        // 检查结果有效，更新时间戳
                        lastCheckSuccessTime = DateTime.Now;

                        if (phoneConnected)
                        {
                            if (!isOnline) LogInfo("状态: 手机在线");
                            isOnline = true;
                            offlineSeconds = 0;
                        }
                        else
                        {
                            if (isOnline) LogInfo("状态: 手机离线");
                            isOnline = false;
                            offlineSeconds++;

                            if (offlineSeconds >= config.OfflineSeconds)
                            {
                                LogInfo($"手机已离线 {offlineSeconds} 秒，执行锁屏");
                                if (autoSleep)
                                    ExecuteSleep();
                                else
                                    TriggerLock();
                                offlineSeconds = 0;
                            }
                        }

                        UpdateIcon();
                    }
                    finally
                    {
                        isChecking = false;
                    }
                }
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
                        string exePath = Process.GetCurrentProcess().MainModule.FileName;
                        key.SetValue(APP_NAME, $"\"{exePath}\"");
                    }
                }
            }
            catch { }
        }
    }
}
