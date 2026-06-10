// OLock - Phone offline auto-lock tool (C# Edition)
// 手机离线自动锁屏工具 - 通过检测 OPPO 互联软件的网络连接状态判断手机是否在线

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

namespace OLock
{
    class Program
    {
        // ================== 配置项 ==================
        const string APP_NAME = "OLock";
        const string CONFIG_FILE_NAME = "olock.config.json";
        static AppConfig config = AppConfig.CreateDefault();
        // ============================================

        // 全局状态
        static volatile int offlineCount = 0;
        static volatile bool isOnline = false;
        static volatile bool running = true;
        static NotifyIcon trayIcon;
        static Form messageForm;
        static volatile bool isWarmup = false;
        static volatile bool isWaitingForApp = true;
        static volatile int warmupRemaining = 0;
        static volatile bool wasLocked = false;
        static string currentLang;

        static volatile bool autoSleep = false;
        static volatile bool autoScreenOff = false;
        static HashSet<string> localIpAddresses = new HashSet<string>();
        static DateTime localIpAddressesLoadedAt = DateTime.MinValue;

        class AppConfig
        {
            public string AppProcessName { get; set; }
            public int CheckIntervalSeconds { get; set; }
            public int OfflineThreshold { get; set; }
            public int WarmupSeconds { get; set; }
            public int MinWarmupSeconds { get; set; }
            public int MaxWarmupSeconds { get; set; }
            public string[] AllowedRemoteIpPrefixes { get; set; }
            public string[] IgnoredRemoteIpPrefixes { get; set; }
            public string SleepCommand { get; set; }
            public string SleepArguments { get; set; }

            public static AppConfig CreateDefault()
            {
                return new AppConfig
                {
                    AppProcessName = "O+Connect",
                    CheckIntervalSeconds = 3,
                    OfflineThreshold = 3,
                    WarmupSeconds = 60,
                    MinWarmupSeconds = 30,
                    MaxWarmupSeconds = 600,
                    AllowedRemoteIpPrefixes = new[] { "192.168.", "10." },
                    IgnoredRemoteIpPrefixes = new string[0],
                    SleepCommand = "rundll32.exe",
                    SleepArguments = "powrprof.dll,SetSuspendState 0,1,0"
                };
            }

            public void Normalize()
            {
                if (string.IsNullOrWhiteSpace(AppProcessName))
                    AppProcessName = "O+Connect";

                CheckIntervalSeconds = Clamp(CheckIntervalSeconds, 1, 60, 3);
                OfflineThreshold = Clamp(OfflineThreshold, 1, 20, 3);
                MinWarmupSeconds = Clamp(MinWarmupSeconds, 1, 3600, 30);
                MaxWarmupSeconds = Clamp(MaxWarmupSeconds, MinWarmupSeconds, 3600, 600);
                WarmupSeconds = Clamp(WarmupSeconds, MinWarmupSeconds, MaxWarmupSeconds, 60);

                if (AllowedRemoteIpPrefixes == null || AllowedRemoteIpPrefixes.Length == 0)
                    AllowedRemoteIpPrefixes = new[] { "192.168.", "10." };

                if (IgnoredRemoteIpPrefixes == null)
                    IgnoredRemoteIpPrefixes = new string[0];

                if (string.IsNullOrWhiteSpace(SleepCommand))
                    SleepCommand = "rundll32.exe";

                if (SleepArguments == null)
                    SleepArguments = string.Empty;
            }

            static int Clamp(int value, int min, int max, int fallback)
            {
                if (value <= 0)
                    value = fallback;
                if (value < min)
                    return min;
                if (value > max)
                    return max;
                return value;
            }
        }

        // Windows API
        [DllImport("user32.dll")]
        static extern IntPtr SendMessage(IntPtr hWnd, int Msg, IntPtr wParam, IntPtr lParam);

        const int HWND_BROADCAST = 0xFFFF;
        const int WM_SYSCOMMAND = 0x0112;
        const int SC_MONITORPOWER = 0xF170;
        const int MONITOR_OFF = 2;
        
        [DllImport("kernel32.dll")]
        static extern IntPtr GetConsoleWindow();

        [DllImport("user32.dll")]
        static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        [DllImport("user32.dll")]
        static extern bool LockWorkStation();

        [DllImport("user32.dll")]
        static extern IntPtr OpenInputDesktop(uint dwFlags, bool fInherit, uint dwDesiredAccess);

        [DllImport("user32.dll")]
        static extern bool CloseDesktop(IntPtr hDesktop);

        [DllImport("kernel32.dll")]
        static extern ushort GetUserDefaultUILanguage();

        [DllImport("user32.dll")]
        static extern bool DestroyIcon(IntPtr handle);


        // 多语言文本
        static Dictionary<string, Dictionary<string, string>> Texts = new Dictionary<string, Dictionary<string, string>>
        {
            ["en"] = new Dictionary<string, string>
            {
                ["tray_waiting"] = "{0}: ⚪ Waiting for {1}",
                ["tray_warmup"] = "{0}: 🟡 Connecting... ({1}s)",
                ["tray_online"] = "{0}: 🟢 Phone online",
                ["tray_offline"] = "{0}: 🔴 Not detected ({1}/{2})",
                ["tray_autostart"] = "Start with Windows",
                ["tray_autosleep"] = "Sleep",
                ["tray_autoscreenoff"] = "Turn off screen",
                ["tray_quit"] = "Quit",
                ["tray_init"] = "{0}: Initializing..."
            },
            ["zh-Hans"] = new Dictionary<string, string>
            {
                ["tray_waiting"] = "{0}: ⚪ 等待 {1}",
                ["tray_warmup"] = "{0}: 🟡 正在连接... ({1}秒)",
                ["tray_online"] = "{0}: 🟢 手机在线",
                ["tray_offline"] = "{0}: 🔴 未检测到 ({1}/{2})",
                ["tray_autostart"] = "开机自启",
                ["tray_autosleep"] = "睡眠",
                ["tray_autoscreenoff"] = "关闭屏幕",
                ["tray_quit"] = "退出",
                ["tray_init"] = "{0}: 初始化中..."
            },
            ["zh-Hant"] = new Dictionary<string, string>
            {
                ["tray_waiting"] = "{0}: ⚪ 等待 {1}",
                ["tray_warmup"] = "{0}: 🟡 正在連線... ({1}秒)",
                ["tray_online"] = "{0}: 🟢 手機在線",
                ["tray_offline"] = "{0}: 🔴 未偵測到 ({1}/{2})",
                ["tray_autostart"] = "開機自啟",
                ["tray_autosleep"] = "睡眠",
                ["tray_autoscreenoff"] = "關閉螢幕",
                ["tray_quit"] = "退出",
                ["tray_init"] = "{0}: 初始化中..."
            }
        };

        static AppConfig LoadAppConfig()
        {
            var loadedConfig = AppConfig.CreateDefault();
            string configPath = Path.Combine(AppContext.BaseDirectory, CONFIG_FILE_NAME);

            try
            {
                if (!File.Exists(configPath))
                    configPath = Path.Combine(Environment.CurrentDirectory, CONFIG_FILE_NAME);

                if (File.Exists(configPath))
                {
                    var options = new JsonSerializerOptions
                    {
                        PropertyNameCaseInsensitive = true,
                        ReadCommentHandling = JsonCommentHandling.Skip,
                        AllowTrailingCommas = true
                    };

                    AppConfig fileConfig = JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(configPath), options);
                    if (fileConfig != null)
                        loadedConfig = fileConfig;
                }
            }
            catch { }

            loadedConfig.Normalize();
            return loadedConfig;
        }

        [STAThread]
        static void Main(string[] args)
        {
            config = LoadAppConfig();

            // 隐藏控制台窗口
            var handle = GetConsoleWindow();
            if (handle != IntPtr.Zero)
                ShowWindow(handle, 0); // SW_HIDE = 0

            // 检测系统语言
            currentLang = GetUILanguage();
            LoadSettings();

            // 初始化托盘图标
            Application.EnableVisualStyles();
            InitTrayIcon();

            // 监听系统电源事件 (S3唤醒)
            SystemEvents.PowerModeChanged += OnPowerModeChanged;

            // 启动监控线程
            var monitorThread = new Thread(MonitorLoop) { IsBackground = true };
            monitorThread.Start();

            // 运行消息循环
            Application.Run();
        }

        static string GetUILanguage()
        {
            HashSet<ushort> hansLangIds = new HashSet<ushort> { 0x0804, 0x1004 };
            HashSet<ushort> hantLangIds = new HashSet<ushort> { 0x0404, 0x0C04, 0x1404 };

            try
            {
                ushort langId = GetUserDefaultUILanguage();
                if (hansLangIds.Contains(langId)) return "zh-Hans";
                if (hantLangIds.Contains(langId)) return "zh-Hant";
            }
            catch { }

            try
            {
                string lang = CultureInfo.CurrentUICulture.Name.ToLower();
                if (lang.StartsWith("zh"))
                {
                    if (lang.Contains("tw") || lang.Contains("hk") || lang.Contains("mo") || lang.Contains("hant"))
                        return "zh-Hant";
                    return "zh-Hans";
                }
            }
            catch { }

            return "en";
        }

        static string Tr(string key, params object[] args)
        {
            var texts = Texts.ContainsKey(currentLang) ? Texts[currentLang] : Texts["en"];
            if (texts.TryGetValue(key, out string template))
                return string.Format(template, args);
            return key;
        }

        static void InitTrayIcon()
        {
            // 创建一个隐藏窗口用于接收消息
            messageForm = new Form
            {
                ShowInTaskbar = false,
                WindowState = FormWindowState.Minimized,
                FormBorderStyle = FormBorderStyle.None,
                Opacity = 0
            };
            // 强制创建句柄，以便接收消息
            var h = messageForm.Handle; 
            
            messageForm.Load += (s, e) => messageForm.Visible = false;

            trayIcon = new NotifyIcon
            {
                Icon = CreateIcon("waiting"),
                Text = Tr("tray_init", APP_NAME),
                Visible = true,
                ContextMenuStrip = CreateContextMenu()
            };
        }

        static ContextMenuStrip CreateContextMenu()
        {
            var menu = new ContextMenuStrip();

            var autostartItem = new ToolStripMenuItem(Tr("tray_autostart"))
            {
                Checked = IsAutostartEnabled()
            };
            autostartItem.Click += (s, e) =>
            {
                ToggleAutostart();
                autostartItem.Checked = IsAutostartEnabled();
            };

            var autoSleepItem = new ToolStripMenuItem(Tr("tray_autosleep"))
            {
                Checked = autoSleep
            };

            autoSleepItem.Click += (s, e) =>
            {
                autoSleep = !autoSleep;
                if (autoSleep) autoScreenOff = false;
                UpdateContextMenu();
                SaveSettings();
            };

            var autoScreenOffItem = new ToolStripMenuItem(Tr("tray_autoscreenoff"))
            {
                Checked = autoScreenOff
            };

            autoScreenOffItem.Click += (s, e) =>
            {
                autoScreenOff = !autoScreenOff;
                if (autoScreenOff) autoSleep = false;
                UpdateContextMenu();
                SaveSettings();
            };

            var quitItem = new ToolStripMenuItem(Tr("tray_quit"));
            quitItem.Click += (s, e) =>
            {
                SystemEvents.PowerModeChanged -= OnPowerModeChanged;
                running = false;
                trayIcon.Visible = false;
                Application.Exit();
            };

            menu.Items.Add(autostartItem);
            menu.Items.Add(autoSleepItem);
            menu.Items.Add(autoScreenOffItem);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(quitItem);

            return menu;
        }

        static void UpdateContextMenu()
        {
            if (trayIcon == null) return;
            var oldMenu = trayIcon.ContextMenuStrip;
            trayIcon.ContextMenuStrip = CreateContextMenu();
            oldMenu?.Dispose();
        }

        static Icon CreateIcon(string state)
        {
            Color color;
            switch (state)
            {
                case "online": color = Color.FromArgb(0, 200, 0); break;
                case "warmup": color = Color.FromArgb(255, 200, 0); break;
                case "waiting": color = Color.FromArgb(128, 128, 128); break;
                default: color = Color.FromArgb(200, 0, 0); break;
            }

            using (var bitmap = new Bitmap(16, 16))
            using (var g = Graphics.FromImage(bitmap))
            {
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                using (var brush = new SolidBrush(color))
                {
                    g.FillEllipse(brush, 1, 1, 14, 14);
                }
                IntPtr hIcon = bitmap.GetHicon();
                var icon = Icon.FromHandle(hIcon);
                var clonedIcon = (Icon)icon.Clone();
                DestroyIcon(hIcon);
                icon.Dispose();
                return clonedIcon;
            }
        }

        static string GetIconState()
        {
            if (isWaitingForApp) return "waiting";
            if (isWarmup) return "warmup";
            if (isOnline) return "online";
            return "offline";
        }

        static string GetStatusText()
        {
            if (isWaitingForApp)
                return Tr("tray_waiting", APP_NAME, config.AppProcessName);
            if (isWarmup)
                return Tr("tray_warmup", APP_NAME, warmupRemaining);
            if (isOnline)
                return Tr("tray_online", APP_NAME);
            return Tr("tray_offline", APP_NAME, offlineCount, config.OfflineThreshold);
        }

        static void UpdateIcon()
        {
            if (trayIcon == null) return;
            try
            {
                var oldIcon = trayIcon.Icon;
                trayIcon.Icon = CreateIcon(GetIconState());
                trayIcon.Text = GetStatusText().Length > 63 ? GetStatusText().Substring(0, 63) : GetStatusText();
                oldIcon?.Dispose();
            }
            catch { }
        }

        static bool IsScreenLocked()
        {
            IntPtr hDesktop = OpenInputDesktop(0, false, 0x0001);
            if (hDesktop != IntPtr.Zero)
            {
                CloseDesktop(hDesktop);
                return false;
            }
            return true;
        }

        static bool IsAppRunning()
        {
            try
            {
                var processes = Process.GetProcessesByName(config.AppProcessName);
                try
                {
                    return processes.Length > 0;
                }
                finally
                {
                    foreach (var p in processes)
                        p.Dispose();
                }
            }
            catch { return false; }
        }

        // Checks whether the configured process has an established connection
        // to a configured remote IP prefix.
        static bool CheckPhoneConnection()
        {
            try
            {
                var pids = new HashSet<string>();
                foreach (var proc in Process.GetProcessesByName(config.AppProcessName))
                {
                    try { pids.Add(proc.Id.ToString()); }
                    finally { proc.Dispose(); }
                }

                if (pids.Count == 0) return false;

                // 执行 netstat -ano 命令
                var psi = new ProcessStartInfo
                {
                    FileName = "netstat",
                    Arguments = "-ano",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    CreateNoWindow = true
                };

                using (var process = Process.Start(psi))
                {
                    string output = process.StandardOutput.ReadToEnd();
                    process.WaitForExit();

                    // 逐行解析 netstat 输出
                    var lines = output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                    foreach (var line in lines)
                    {
                        // 只处理 ESTABLISHED 连接
                        if (!line.Contains("ESTABLISHED")) continue;

                        var parts = line.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                        if (parts.Length < 5) continue;

                        string pid = parts[parts.Length - 1];
                        if (!pids.Contains(pid)) continue;

                        // 获取远程地址 (第3列)
                        string remoteAddr = parts[2];
                        int lastColon = remoteAddr.LastIndexOf(':');
                        if (lastColon <= 0) continue;

                        string remoteIP = remoteAddr.Substring(0, lastColon);

                        if (HasAllowedRemoteIpPrefix(remoteIP))
                        {
                            return true;
                        }
                    }
                }
            }
            catch { }
            return false;
        }

        static bool HasAllowedRemoteIpPrefix(string remoteIP)
        {
            if (string.IsNullOrWhiteSpace(remoteIP))
                return false;

            if (IsLocalIpAddress(remoteIP))
                return false;

            foreach (string ignoredPrefix in config.IgnoredRemoteIpPrefixes)
            {
                if (!string.IsNullOrWhiteSpace(ignoredPrefix) &&
                    remoteIP.StartsWith(ignoredPrefix, StringComparison.OrdinalIgnoreCase))
                    return false;
            }

            foreach (string allowedPrefix in config.AllowedRemoteIpPrefixes)
            {
                if (!string.IsNullOrWhiteSpace(allowedPrefix) &&
                    remoteIP.StartsWith(allowedPrefix, StringComparison.OrdinalIgnoreCase))
                    return true;
            }

            // Fallback: any RFC 1918 private IP (not local) is treated as phone connection
            if (IsPrivateIpAddress(remoteIP))
                return true;

            return false;
        }

        // RFC 1918 private IPv4 address check
        static bool IsPrivateIpAddress(string ipStr)
        {
            if (!IPAddress.TryParse(ipStr, out IPAddress addr))
                return false;

            if (addr.AddressFamily != AddressFamily.InterNetwork)
                return false;

            byte[] bytes = addr.GetAddressBytes();
            if (bytes.Length != 4)
                return false;

            // 10.0.0.0/8
            if (bytes[0] == 10) return true;
            // 172.16.0.0/12
            if (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31) return true;
            // 192.168.0.0/16
            if (bytes[0] == 192 && bytes[1] == 168) return true;

            return false;
        }

        static bool IsLocalIpAddress(string ipAddress)
        {
            if (!IPAddress.TryParse(ipAddress, out IPAddress parsedAddress))
                return false;

            if (IPAddress.IsLoopback(parsedAddress))
                return true;

            return GetLocalIpAddresses().Contains(parsedAddress.ToString());
        }

        static HashSet<string> GetLocalIpAddresses()
        {
            if ((DateTime.UtcNow - localIpAddressesLoadedAt).TotalSeconds < 60 && localIpAddresses.Count > 0)
                return localIpAddresses;

            var addresses = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            try
            {
                foreach (NetworkInterface networkInterface in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (networkInterface.OperationalStatus != OperationalStatus.Up)
                        continue;

                    foreach (UnicastIPAddressInformation address in networkInterface.GetIPProperties().UnicastAddresses)
                    {
                        if (address.Address.AddressFamily == AddressFamily.InterNetwork)
                            addresses.Add(address.Address.ToString());
                    }
                }
            }
            catch { }

            localIpAddresses = addresses;
            localIpAddressesLoadedAt = DateTime.UtcNow;
            return localIpAddresses;
        }

        static void StartWaitingForApp()
        {
            isWaitingForApp = true;
            isWarmup = false;
            isOnline = false;
            offlineCount = 0;
        }

        static void OnPowerModeChanged(object sender, PowerModeChangedEventArgs e)
        {
            if (e.Mode == PowerModes.Resume)
            {
                StartWaitingForApp();
            }
        }

        static void StartWarmup()
        {
            int warmupTime = Math.Max(config.MinWarmupSeconds, Math.Min(config.MaxWarmupSeconds, config.WarmupSeconds));
            isWarmup = true;
            isWaitingForApp = false;
            warmupRemaining = warmupTime;
            offlineCount = 0;
            isOnline = false;
        }

        static void TriggerLock()
        {
            // 正常执行锁屏
            LockWorkStation();

            if (autoScreenOff)
            {
                // 延迟一会确保锁屏界面已加载
                Thread.Sleep(500);
                // 关闭屏幕
                SendMessage((IntPtr)HWND_BROADCAST, WM_SYSCOMMAND, (IntPtr)SC_MONITORPOWER, (IntPtr)MONITOR_OFF);
            }
        }

        static void MonitorLoop()
        {
            StartWaitingForApp();

            while (running)
            {
                try
                {
                    // 检测屏幕锁定状态
                    bool currentlyLocked = IsScreenLocked();

                    // 从锁定变为解锁
                    if (wasLocked && !currentlyLocked)
                    {
                        StartWaitingForApp();
                    }

                    wasLocked = currentlyLocked;

                    // 屏幕锁定时暂停
                    if (currentlyLocked)
                    {
                        Thread.Sleep(1000);
                        continue;
                    }

                    // 阶段1: 等待主程序启动 (灰色)
                    if (isWaitingForApp)
                    {
                        if (IsAppRunning())
                            StartWarmup();
                        else
                        {
                            UpdateIcon();
                            Thread.Sleep(1000);
                            continue;
                        }
                    }

                    // 阶段2: 缓冲期 (黄色)
                    if (isWarmup)
                    {
                        if (!IsAppRunning())
                        {
                            StartWaitingForApp();
                            continue;
                        }

                        bool connected = CheckPhoneConnection();
                        if (connected)
                        {
                            isWarmup = false;
                            warmupRemaining = 0;
                            offlineCount = 0;
                            isOnline = true;
                        }
                        else
                        {
                            warmupRemaining--;
                            UpdateIcon();

                            if (warmupRemaining <= 0)
                            {
                                if (autoSleep)
                                {
                                    // 缓冲期结束未连接，进入睡眠
                                    using (var proc = Process.Start(new ProcessStartInfo {
                                        FileName = config.SleepCommand,
                                        Arguments = config.SleepArguments,
                                        CreateNoWindow = true,
                                        WindowStyle = ProcessWindowStyle.Hidden,
                                        UseShellExecute = false
                                    }))
                                    {
                                        proc?.WaitForExit(5000);
                                    }
                                }
                                else
                                {
                                    TriggerLock();
                                }
                                Thread.Sleep(1000);
                                continue;
                            }
                            Thread.Sleep(1000);
                            continue;
                        }
                    }

                    // 阶段3: 正常监控 (绿色/红色)
                    if (!IsAppRunning())
                    {
                        StartWaitingForApp();
                        UpdateIcon();
                        continue;
                    }

                    bool phoneConnected = CheckPhoneConnection();
                    if (phoneConnected)
                    {
                        isOnline = true;
                        offlineCount = 0;
                    }
                    else
                    {
                        isOnline = false;
                        offlineCount++;

                        if (offlineCount >= config.OfflineThreshold)
                        {
                            if (autoSleep)
                            {
                                // 检测到三次未连接到手机，直接进入睡眠模式
                                using (var proc = Process.Start(new ProcessStartInfo {
                                    FileName = config.SleepCommand,
                                    Arguments = config.SleepArguments,
                                    CreateNoWindow = true,
                                    WindowStyle = ProcessWindowStyle.Hidden,
                                    UseShellExecute = false
                                }))
                                {
                                    proc?.WaitForExit(5000);
                                }
                            }
                            else if (autoScreenOff)
                            {
                                // 锁屏并关屏
                                TriggerLock();
                            }
                            else
                            {
                                TriggerLock();
                            }
                            offlineCount = 0;
                        }
                    }

                    UpdateIcon();

                    // 等待下次检测
                    for (int i = 0; i < config.CheckIntervalSeconds * 10 && running; i++)
                        Thread.Sleep(100);
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[OLock] MonitorLoop error: {ex.Message}");
                    Thread.Sleep(1000);
                }
            }
        }

        static bool IsAutostartEnabled()
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

        static void ToggleAutostart()
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

        static void LoadSettings()
        {
            try
            {
                using (var key = Registry.CurrentUser.OpenSubKey(@"Software\OLock", false))
                {
                    if (key != null)
                    {
                        var sleepVal = key.GetValue("AutoSleep");
                        if (sleepVal != null) autoSleep = Convert.ToBoolean(sleepVal);

                        var screenOffVal = key.GetValue("AutoScreenOff");
                        if (screenOffVal != null) autoScreenOff = Convert.ToBoolean(screenOffVal);
                    }
                }
            }
            catch { }
        }

        static void SaveSettings()
        {
            try
            {
                using (var key = Registry.CurrentUser.CreateSubKey(@"Software\OLock"))
                {
                    if (key != null)
                    {
                        key.SetValue("AutoSleep", autoSleep);
                        key.SetValue("AutoScreenOff", autoScreenOff);
                    }
                }
            }
            catch { }
        }
    }
}
