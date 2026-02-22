// OLock - Phone offline auto-lock tool (C# Edition)
// 手机离线自动锁屏工具 - 通过检测 OPPO 互联软件的网络连接状态判断手机是否在线

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

namespace OLock
{
    class Program
    {
        // ================== 配置项 ==================
        const string APP_NAME = "OLock";
        const string APP_PROCESS_NAME = "O+Connect";           // 主程序进程（检测网络连接）
        const int CHECK_INTERVAL = 3;                          // 检测间隔（秒）
        const int OFFLINE_THRESHOLD = 3;                       // 连续多少次离线才锁屏
        const int WARMUP_TIME = 60;                            // 缓冲期时间（秒）
        // ============================================

        // 全局状态
        static int offlineCount = 0;
        static bool isOnline = false;
        static volatile bool running = true;
        static NotifyIcon trayIcon;
        static bool isWarmup = false;
        static bool isWaitingForApp = true;
        static int warmupRemaining = 0;
        static bool wasLocked = false;
        static string currentLang;

        // Windows API
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
                ["tray_quit"] = "退出",
                ["tray_init"] = "{0}: 初始化中..."
            }
        };

        [STAThread]
        static void Main(string[] args)
        {
            // 隐藏控制台窗口
            var handle = GetConsoleWindow();
            if (handle != IntPtr.Zero)
                ShowWindow(handle, 0); // SW_HIDE = 0

            // 检测系统语言
            currentLang = GetUILanguage();

            // 初始化托盘图标
            Application.EnableVisualStyles();
            InitTrayIcon();

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

            var quitItem = new ToolStripMenuItem(Tr("tray_quit"));
            quitItem.Click += (s, e) =>
            {
                running = false;
                trayIcon.Visible = false;
                Application.Exit();
            };

            menu.Items.Add(autostartItem);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(quitItem);

            return menu;
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
                return Icon.FromHandle(bitmap.GetHicon());
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
                return Tr("tray_waiting", APP_NAME, APP_PROCESS_NAME);
            if (isWarmup)
                return Tr("tray_warmup", APP_NAME, warmupRemaining);
            if (isOnline)
                return Tr("tray_online", APP_NAME);
            return Tr("tray_offline", APP_NAME, offlineCount, OFFLINE_THRESHOLD);
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
                var processes = Process.GetProcessesByName(APP_PROCESS_NAME);
                return processes.Length > 0;
            }
            catch { return false; }
        }

        /// <summary>
        /// 检测手机是否连接
        /// 遍历所有 O+Connect.exe 进程的网络连接
        /// 如果存在 ESTABLISHED 状态且远程 IP 是 192.168.x.x 或 10.x.x.x，则判定手机在线
        /// 完全按照 Python 版本的逻辑实现
        /// </summary>
        static bool CheckPhoneConnection()
        {
            try
            {
                // 只获取 O+Connect.exe 进程的 PID（与 Python 版本一致）
                var pids = new HashSet<string>();
                foreach (var proc in Process.GetProcessesByName(APP_PROCESS_NAME))
                {
                    pids.Add(proc.Id.ToString());
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

                        // 解析行: TCP    192.168.1.100:12345    192.168.1.1:5678    ESTABLISHED    1234
                        var parts = line.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                        if (parts.Length < 5) continue;

                        string pid = parts[parts.Length - 1];
                        if (!pids.Contains(pid)) continue;

                        // 获取远程地址 (第3列)
                        string remoteAddr = parts[2];
                        int lastColon = remoteAddr.LastIndexOf(':');
                        if (lastColon <= 0) continue;

                        string remoteIP = remoteAddr.Substring(0, lastColon);

                        // 检查是否是局域网IP (192.168.x.x 或 10.x.x.x)
                        // 与 Python 版本完全一致的判断逻辑
                        if (remoteIP.StartsWith("192.168.") || remoteIP.StartsWith("10."))
                        {
                            // 排除本机IP 10.161.156.1（与 Python 版本一致）
                            if (remoteIP.StartsWith("10.161.156.1")) continue;
                            return true;
                        }
                    }
                }
            }
            catch { }
            return false;
        }

        static void StartWaitingForApp()
        {
            isWaitingForApp = true;
            isWarmup = false;
            isOnline = false;
            offlineCount = 0;
        }

        static void StartWarmup()
        {
            int warmupTime = Math.Max(30, Math.Min(600, WARMUP_TIME));
            isWarmup = true;
            isWaitingForApp = false;
            warmupRemaining = warmupTime;
            offlineCount = 0;
            isOnline = false;
        }

        static void MonitorLoop()
        {
            StartWaitingForApp();

            while (running)
            {
                // 检测屏幕锁定状态
                bool currentlyLocked = IsScreenLocked();

                // 从锁定变为解锁
                if (wasLocked && !currentlyLocked)
                    StartWaitingForApp();

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
                            LockWorkStation();
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

                    if (offlineCount >= OFFLINE_THRESHOLD)
                    {
                        LockWorkStation();
                        offlineCount = 0;
                    }
                }

                UpdateIcon();

                // 等待下次检测
                for (int i = 0; i < CHECK_INTERVAL * 10 && running; i++)
                    Thread.Sleep(100);
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
    }
}
