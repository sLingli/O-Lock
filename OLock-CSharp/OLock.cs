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
using System.Threading.Tasks;
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

        // 日志
        static readonly object logLock = new object();
        static string logFilePath;

        static void InitLogger()
        {
            logFilePath = Path.Combine(AppContext.BaseDirectory, "olock.log");
        }

        static void Log(string level, string message)
        {
            if (logFilePath == null) return;
            var line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {level}: {message}";
            lock (logLock)
            {
                try
                {
                    if (File.Exists(logFilePath) && new FileInfo(logFilePath).Length > 500 * 1024)
                    {
                        var content = File.ReadAllText(logFilePath);
                        int start = Math.Max(0, content.Length - 200 * 1024);
                        int nl = content.IndexOf('\n', start);
                        if (nl >= 0) start = nl + 1;
                        File.WriteAllText(logFilePath, content.Substring(start));
                    }
                    File.AppendAllText(logFilePath, line + Environment.NewLine);
                }
                catch { }
            }
        }

        static void LogInfo(string msg) => Log("INFO", msg);
        static void LogError(string msg) => Log("ERROR", msg);

        static void ShowLogViewer()
        {
            var form = new Form
            {
                Text = $"{APP_NAME} Log",
                Width = 720,
                Height = 460,
                StartPosition = FormStartPosition.CenterScreen,
                MinimizeBox = false,
                MaximizeBox = false,
                FormBorderStyle = FormBorderStyle.FixedDialog
            };

            var textBox = new TextBox
            {
                Multiline = true,
                ReadOnly = true,
                ScrollBars = ScrollBars.Vertical,
                Font = new Font("Consolas", 9f),
                Dock = DockStyle.Top,
                Height = 380,
                WordWrap = false
            };

            var refreshBtn = new Button { Text = Tr("tray_log_refresh"), Width = 80, Left = 520, Top = 390 };
            var clearBtn = new Button { Text = Tr("tray_log_clear"), Width = 80, Left = 610, Top = 390 };

            Action loadLog = () =>
            {
                try
                {
                    if (File.Exists(logFilePath))
                        textBox.Text = File.ReadAllText(logFilePath);
                    else
                        textBox.Text = Tr("tray_log_empty");
                }
                catch (Exception ex)
                {
                    textBox.Text = $"Error: {ex.Message}";
                }
            };

            refreshBtn.Click += (s, e) => loadLog();
            clearBtn.Click += (s, e) =>
            {
                try
                {
                    lock (logLock) { File.WriteAllText(logFilePath, string.Empty); }
                    textBox.Clear();
                }
                catch { }
            };

            loadLog();
            form.Controls.Add(textBox);
            form.Controls.Add(refreshBtn);
            form.Controls.Add(clearBtn);
            form.Show();
        }

        static void ShowSettingsWindow()
        {
            var form = new Form
            {
                Text = Tr("tray_settings"),
                Width = 480,
                Height = 420,
                StartPosition = FormStartPosition.CenterScreen,
                MinimizeBox = false,
                MaximizeBox = false,
                FormBorderStyle = FormBorderStyle.FixedDialog
            };

            int y = 15, labelW = 120, inputX = 140, inputW = 300;

            // 监控进程名
            form.Controls.Add(new Label { Text = "进程名", Left = 10, Top = y + 3, Width = labelW });
            var txtProcess = new TextBox { Left = inputX, Top = y, Width = inputW, Text = config.AppProcessName };
            form.Controls.Add(txtProcess);
            y += 32;

            // 检测间隔
            form.Controls.Add(new Label { Text = "检测间隔 (秒)", Left = 10, Top = y + 3, Width = labelW });
            var numInterval = new NumericUpDown { Left = inputX, Top = y, Width = inputW, Minimum = 1, Maximum = 60, Value = config.CheckIntervalSeconds };
            form.Controls.Add(numInterval);
            y += 32;

            // 离线阈值
            form.Controls.Add(new Label { Text = "离线阈值 (次)", Left = 10, Top = y + 3, Width = labelW });
            var numThreshold = new NumericUpDown { Left = inputX, Top = y, Width = inputW, Minimum = 1, Maximum = 20, Value = config.OfflineThreshold };
            form.Controls.Add(numThreshold);
            y += 32;

            // 缓冲期
            form.Controls.Add(new Label { Text = "缓冲期 (秒)", Left = 10, Top = y + 3, Width = labelW });
            var numWarmup = new NumericUpDown { Left = inputX, Top = y, Width = inputW, Minimum = 1, Maximum = 3600, Value = config.WarmupSeconds };
            form.Controls.Add(numWarmup);
            y += 32;

            // 允许的 IP 前缀
            form.Controls.Add(new Label { Text = "允许 IP 前缀", Left = 10, Top = y + 3, Width = labelW });
            var txtAllowed = new TextBox { Left = inputX, Top = y, Width = inputW, Text = string.Join(",", config.AllowedRemoteIpPrefixes) };
            form.Controls.Add(txtAllowed);
            y += 32;

            // 忽略的 IP 前缀
            form.Controls.Add(new Label { Text = "忽略 IP 前缀", Left = 10, Top = y + 3, Width = labelW });
            var txtIgnored = new TextBox { Left = inputX, Top = y, Width = inputW, Text = string.Join(",", config.IgnoredRemoteIpPrefixes) };
            form.Controls.Add(txtIgnored);
            y += 32;

            // 睡眠命令
            form.Controls.Add(new Label { Text = "睡眠命令", Left = 10, Top = y + 3, Width = labelW });
            var txtSleepCmd = new TextBox { Left = inputX, Top = y, Width = inputW, Text = config.SleepCommand };
            form.Controls.Add(txtSleepCmd);
            y += 32;

            // 睡眠参数
            form.Controls.Add(new Label { Text = "睡眠参数", Left = 10, Top = y + 3, Width = labelW });
            var txtSleepArgs = new TextBox { Left = inputX, Top = y, Width = inputW, Text = config.SleepArguments };
            form.Controls.Add(txtSleepArgs);
            y += 40;

            // 按钮
            var saveBtn = new Button { Text = "保存", Width = 80, Left = inputX + inputW - 170, Top = y };
            var cancelBtn = new Button { Text = "取消", Width = 80, Left = inputX + inputW - 80, Top = y };

            saveBtn.Click += (s, e) =>
            {
                config.AppProcessName = txtProcess.Text.Trim();
                config.CheckIntervalSeconds = (int)numInterval.Value;
                config.OfflineThreshold = (int)numThreshold.Value;
                config.WarmupSeconds = (int)numWarmup.Value;
                config.AllowedRemoteIpPrefixes = txtAllowed.Text.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries);
                config.IgnoredRemoteIpPrefixes = txtIgnored.Text.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries);
                config.SleepCommand = txtSleepCmd.Text.Trim();
                config.SleepArguments = txtSleepArgs.Text;
                config.Normalize();
                SaveSettings();
                LogInfo("设置已保存");
                form.Close();
            };

            cancelBtn.Click += (s, e) => form.Close();

            form.Controls.Add(saveBtn);
            form.Controls.Add(cancelBtn);
            form.Show();
        }

        // 全局状态
        static int offlineCount = 0;
        static bool isOnline = false;
        static NotifyIcon trayIcon;
        static Form messageForm;
        static bool isWarmup = false;
        static bool isWaitingForApp = true;
        static int warmupRemaining = 0;
        static bool wasLocked = false;
        static string currentLang;

        // 定时器 (替代后台线程)
        static System.Windows.Forms.Timer monitorTimer;
        static int elapsedTicks = 0;       // 自上次 netstat 检查以来的 tick 数
        static bool isChecking = false;    // 防止并发执行 netstat 检查
        static int checkGeneration = 0;    // 异步检查代次号，状态重置时递增
        static DateTime isCheckingSince = DateTime.MinValue; // isChecking 开始时间

        static bool autoSleep = false;
        static bool autoScreenOff = false;
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
                ["tray_settings"] = "Settings",
                ["tray_quit"] = "Quit",
                ["tray_log"] = "View log",
                ["tray_log_refresh"] = "Refresh",
                ["tray_log_clear"] = "Clear",
                ["tray_log_empty"] = "(No log entries yet)",
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
                ["tray_settings"] = "设置",
                ["tray_quit"] = "退出",
                ["tray_log"] = "查看日志",
                ["tray_log_refresh"] = "刷新",
                ["tray_log_clear"] = "清空",
                ["tray_log_empty"] = "（暂无日志）",
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
                ["tray_settings"] = "設定",
                ["tray_quit"] = "退出",
                ["tray_log"] = "檢視日誌",
                ["tray_log_refresh"] = "重新整理",
                ["tray_log_clear"] = "清空",
                ["tray_log_empty"] = "（暫無日誌）",
                ["tray_init"] = "{0}: 初始化中..."
            }
        };

        // 如果存在 JSON 配置文件则加载覆盖，返回 true 表示已加载
        static bool LoadJsonConfigIfExists()
        {
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
                    {
                        // 用 JSON 值覆盖当前配置（但不覆盖 AutoSleep/AutoScreenOff，它们由菜单控制）
                        config.AppProcessName = fileConfig.AppProcessName ?? config.AppProcessName;
                        config.CheckIntervalSeconds = fileConfig.CheckIntervalSeconds > 0 ? fileConfig.CheckIntervalSeconds : config.CheckIntervalSeconds;
                        config.OfflineThreshold = fileConfig.OfflineThreshold > 0 ? fileConfig.OfflineThreshold : config.OfflineThreshold;
                        config.WarmupSeconds = fileConfig.WarmupSeconds > 0 ? fileConfig.WarmupSeconds : config.WarmupSeconds;
                        config.MinWarmupSeconds = fileConfig.MinWarmupSeconds > 0 ? fileConfig.MinWarmupSeconds : config.MinWarmupSeconds;
                        config.MaxWarmupSeconds = fileConfig.MaxWarmupSeconds > 0 ? fileConfig.MaxWarmupSeconds : config.MaxWarmupSeconds;
                        if (fileConfig.AllowedRemoteIpPrefixes != null && fileConfig.AllowedRemoteIpPrefixes.Length > 0)
                            config.AllowedRemoteIpPrefixes = fileConfig.AllowedRemoteIpPrefixes;
                        if (fileConfig.IgnoredRemoteIpPrefixes != null)
                            config.IgnoredRemoteIpPrefixes = fileConfig.IgnoredRemoteIpPrefixes;
                        if (!string.IsNullOrWhiteSpace(fileConfig.SleepCommand))
                            config.SleepCommand = fileConfig.SleepCommand;
                        if (fileConfig.SleepArguments != null)
                            config.SleepArguments = fileConfig.SleepArguments;
                        LogInfo($"配置文件覆盖成功: {configPath}");
                        return true;
                    }
                }
            }
            catch (Exception ex)
            {
                LogError($"配置文件加载失败: {ex.Message}");
            }
            return false;
        }

        [STAThread]
        static void Main(string[] args)
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
                LogInfo($"开机自启: {(autostartItem.Checked ? "开启" : "关闭")}");
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
                LogInfo($"自动睡眠: {(autoSleep ? "开启" : "关闭")}");
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
                LogInfo($"自动关屏: {(autoScreenOff ? "开启" : "关闭")}");
            };

            var settingsItem = new ToolStripMenuItem(Tr("tray_settings"));
            settingsItem.Click += (s, e) => ShowSettingsWindow();

            var logItem = new ToolStripMenuItem(Tr("tray_log"));
            logItem.Click += (s, e) => ShowLogViewer();

            var quitItem = new ToolStripMenuItem(Tr("tray_quit"));
            quitItem.Click += (s, e) =>
            {
                LogInfo("用户退出");
                SystemEvents.PowerModeChanged -= OnPowerModeChanged;
                monitorTimer?.Stop();
                monitorTimer?.Dispose();
                trayIcon.Visible = false;
                Application.Exit();
            };

            menu.Items.Add(autostartItem);
            menu.Items.Add(autoSleepItem);
            menu.Items.Add(autoScreenOffItem);
            menu.Items.Add(settingsItem);
            menu.Items.Add(logItem);
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

        // UpdateIcon 现在只在 UI 线程上调用 (由 Timer Tick 触发)，不再有跨线程问题
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

        // 以下两个方法在后台线程上通过 Task.Run 执行，结果通过回调返回 UI 线程

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
        // 在后台线程执行，带超时保护
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

                // 执行 netstat -ano 命令 (带超时保护)
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
                    // 异步读取 stdout，带 10 秒超时
                    var readTask = process.StandardOutput.ReadToEndAsync();
                    if (!readTask.Wait(TimeSpan.FromSeconds(10)))
                    {
                        try { process.Kill(); } catch { }
                        return false; // 超时视为未检测到
                    }

                    string output = readTask.Result;

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

        // ==================== 状态管理 (均在 UI 线程执行) ====================

        static void StartWaitingForApp()
        {
            isWaitingForApp = true;
            isWarmup = false;
            isOnline = false;
            offlineCount = 0;
            elapsedTicks = 0;
            isChecking = false;
            checkGeneration++;  // 使旧的异步回调失效
            LogInfo("状态: 等待应用启动");
            UpdateIcon();

            // 确保定时器在运行
            if (monitorTimer != null && !monitorTimer.Enabled)
                monitorTimer.Start();
        }

        static void OnPowerModeChanged(object sender, PowerModeChangedEventArgs e)
        {
            if (e.Mode == PowerModes.Resume)
            {
                LogInfo("系统唤醒 (S3 Resume)");
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
            elapsedTicks = 0;
            isChecking = false;
            checkGeneration++;  // 使旧的异步回调失效
            LogInfo($"状态: 缓冲期开始 ({warmupTime}秒)");
            UpdateIcon();
        }

        static void TriggerLock()
        {
            LogInfo("执行锁屏");
            LockWorkStation();

            if (autoScreenOff)
            {
                // 在后台线程延迟后关屏，避免阻塞 UI
                Task.Run(() =>
                {
                    Thread.Sleep(500);
                    SendMessage((IntPtr)HWND_BROADCAST, WM_SYSCOMMAND, (IntPtr)SC_MONITORPOWER, (IntPtr)MONITOR_OFF);
                    LogInfo("执行关闭屏幕");
                });
            }
        }

        static void ExecuteSleep()
        {
            LogInfo("执行睡眠命令");
            // 在后台线程执行，避免阻塞 UI
            Task.Run(() =>
            {
                try
                {
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
                catch (Exception ex)
                {
                    LogError($"睡眠命令执行失败: {ex.Message}");
                }
            });
        }

        // ==================== 核心：定时器驱动的监控逻辑 ====================

        // MonitorTick 在 UI 线程上由 Forms.Timer 触发 (每 1 秒)
        // 所有 UI 操作 (UpdateIcon) 天然在 UI 线程上，无跨线程问题
        // 阻塞 I/O (IsAppRunning, CheckPhoneConnection) 通过 Task.Run 在后台执行
        static void MonitorTick(object sender, EventArgs e)
        {
            try
            {
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

                // isChecking 超时保护 (30秒)
                if (isChecking && (DateTime.Now - isCheckingSince).TotalSeconds > 30)
                {
                    LogError("isChecking 超时，强制重置");
                    isChecking = false;
                }

                // 2. 等待主程序启动阶段 (灰色)
                if (isWaitingForApp)
                {
                    if (IsAppRunning())
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
                    // IsAppRunning 在缓冲期可以同步调用 (只检查进程名，很快)
                    if (!IsAppRunning())
                    {
                        StartWaitingForApp();
                        return;
                    }

                    // 按配置的间隔执行手机连接检查 (与正常监控阶段一致)
                    elapsedTicks++;
                    if (elapsedTicks < config.CheckIntervalSeconds)
                        return;
                    elapsedTicks = 0;

                    if (!isChecking)
                    {
                        isChecking = true;
                        isCheckingSince = DateTime.Now;
                        int gen = checkGeneration;
                        Task.Run(() =>
                        {
                            try { return CheckPhoneConnection(); }
                            catch { return false; }
                        }).ContinueWith(task =>
                        {
                            // 回到 UI 线程处理结果
                            try
                            {
                                isChecking = false;
                                if (gen != checkGeneration) return; // 状态已重置，丢弃旧结果

                                bool connected = task.IsCompleted ? task.Result : false;

                                if (connected)
                                {
                                    isWarmup = false;
                                    warmupRemaining = 0;
                                    offlineCount = 0;
                                    isOnline = true;
                                    elapsedTicks = 0;
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
                            catch (Exception ex)
                            {
                                LogError($"缓冲期检查回调异常: {ex.Message}");
                                isChecking = false;
                            }
                        }, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.FromCurrentSynchronizationContext());
                    }
                    return;
                }

                // 4. 正常监控阶段 (绿色/红色)
                if (!IsAppRunning())
                {
                    StartWaitingForApp();
                    UpdateIcon();
                    return;
                }

                elapsedTicks++;

                // 按配置的间隔执行手机连接检查
                if (elapsedTicks >= config.CheckIntervalSeconds)
                {
                    elapsedTicks = 0;

                    if (!isChecking)
                    {
                        isChecking = true;
                        isCheckingSince = DateTime.Now;
                        int gen = checkGeneration;
                        Task.Run(() =>
                        {
                            try { return CheckPhoneConnection(); }
                            catch { return false; }
                        }).ContinueWith(task =>
                        {
                            // 回到 UI 线程处理结果
                            try
                            {
                                isChecking = false;
                                if (gen != checkGeneration) return; // 状态已重置，丢弃旧结果

                                bool phoneConnected = task.IsCompleted ? task.Result : false;

                                if (phoneConnected)
                                {
                                    if (!isOnline) LogInfo("状态: 手机在线");
                                    isOnline = true;
                                    offlineCount = 0;
                                }
                                else
                                {
                                    if (isOnline) LogInfo("状态: 手机离线");
                                    isOnline = false;
                                    offlineCount++;

                                    if (offlineCount >= config.OfflineThreshold)
                                    {
                                        LogInfo($"连续 {offlineCount} 次未检测到手机");
                                        if (autoSleep)
                                            ExecuteSleep();
                                        else
                                            TriggerLock();
                                        offlineCount = 0;
                                    }
                                }

                                UpdateIcon();
                            }
                            catch (Exception ex)
                            {
                                LogError($"监控检查回调异常: {ex.Message}");
                                isChecking = false;
                            }
                        }, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.FromCurrentSynchronizationContext());
                    }
                }
            }
            catch (Exception ex)
            {
                LogError($"MonitorTick 异常: {ex.Message}");
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
                        // 配置项
                        var v = key.GetValue("AppProcessName"); if (v != null) config.AppProcessName = v.ToString();
                        v = key.GetValue("CheckIntervalSeconds"); if (v != null) config.CheckIntervalSeconds = Convert.ToInt32(v);
                        v = key.GetValue("OfflineThreshold"); if (v != null) config.OfflineThreshold = Convert.ToInt32(v);
                        v = key.GetValue("WarmupSeconds"); if (v != null) config.WarmupSeconds = Convert.ToInt32(v);
                        v = key.GetValue("MinWarmupSeconds"); if (v != null) config.MinWarmupSeconds = Convert.ToInt32(v);
                        v = key.GetValue("MaxWarmupSeconds"); if (v != null) config.MaxWarmupSeconds = Convert.ToInt32(v);
                        v = key.GetValue("AllowedRemoteIpPrefixes"); if (v != null) config.AllowedRemoteIpPrefixes = v.ToString().Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries);
                        v = key.GetValue("IgnoredRemoteIpPrefixes"); if (v != null) config.IgnoredRemoteIpPrefixes = v.ToString().Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries);
                        v = key.GetValue("SleepCommand"); if (v != null) config.SleepCommand = v.ToString();
                        v = key.GetValue("SleepArguments"); if (v != null) config.SleepArguments = v.ToString();

                        // 用户偏好
                        var sleepVal = key.GetValue("AutoSleep");
                        if (sleepVal != null) autoSleep = Convert.ToBoolean(sleepVal);
                        var screenOffVal = key.GetValue("AutoScreenOff");
                        if (screenOffVal != null) autoScreenOff = Convert.ToBoolean(screenOffVal);
                    }
                }
                config.Normalize();
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
                        // 配置项
                        key.SetValue("AppProcessName", config.AppProcessName);
                        key.SetValue("CheckIntervalSeconds", config.CheckIntervalSeconds);
                        key.SetValue("OfflineThreshold", config.OfflineThreshold);
                        key.SetValue("WarmupSeconds", config.WarmupSeconds);
                        key.SetValue("MinWarmupSeconds", config.MinWarmupSeconds);
                        key.SetValue("MaxWarmupSeconds", config.MaxWarmupSeconds);
                        key.SetValue("AllowedRemoteIpPrefixes", string.Join(",", config.AllowedRemoteIpPrefixes));
                        key.SetValue("IgnoredRemoteIpPrefixes", string.Join(",", config.IgnoredRemoteIpPrefixes));
                        key.SetValue("SleepCommand", config.SleepCommand);
                        key.SetValue("SleepArguments", config.SleepArguments);

                        // 用户偏好
                        key.SetValue("AutoSleep", autoSleep);
                        key.SetValue("AutoScreenOff", autoScreenOff);
                    }
                }
            }
            catch { }
        }
    }
}
