using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Microsoft.Win32;

using static OLock.AppConfigStorage;
using static OLock.Localization;
using static OLock.Logging;
using static OLock.Program;
using static OLock.SettingsForm;

namespace OLock
{
    // 托盘图标、右键菜单与状态显示
    internal static class TrayUI
    {
        private static NotifyIcon trayIcon = null!;
        private static string? lastIconState;  // 缓存：上次图标状态，避免无变化时重复创建 Icon

        // 与 shell 强制重新同步的间隔 (秒)。NotifyIcon 只在自身缓存变化时才通知 shell，
        // 所以一次丢失的通知 (例如别的托盘程序崩溃扰动通知区域) 会让托盘永久停在旧状态：
        // 缓存认为"已经是最新的"，于是再也不向 shell 发任何东西。定期无条件重发，
        // 把这种卡死的持续时间限制在这个间隔内。
        private const int ShellResyncSeconds = 30;
        private static int ticksSinceShellSync;

        private static DateTime lastTrayErrorLog = DateTime.MinValue;  // 托盘报错限流

        [DllImport("user32.dll")]
        private static extern bool DestroyIcon(IntPtr handle);

        internal static void InitTrayIcon()
        {
            trayIcon = new NotifyIcon
            {
                Icon = CreateIcon("waiting"),
                Text = Tr("tray_init", APP_NAME),
                Visible = true,
                ContextMenuStrip = CreateContextMenu()
            };
        }

        private static ContextMenuStrip CreateContextMenu()
        {
            var menu = new ContextMenuStrip();

            // 版本号 (菜单顶部，不可点击)
            var versionItem = new ToolStripMenuItem($"{APP_NAME} {AppVersion}")
            {
                Enabled = false
            };

            var autostartItem = new ToolStripMenuItem(Tr("tray_autostart"))
            {
                Checked = IsAutostartEnabled()
            };
            autostartItem.Click += (s, e) =>
            {
                ToggleAutostart();
                autostartItem.Checked = IsAutostartEnabled();
                LogInfo("应用", $"开机自启: {(autostartItem.Checked ? "开启" : "关闭")}");
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
                LogInfo("应用", $"自动睡眠: {(autoSleep ? "开启" : "关闭")}");
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
                LogInfo("应用", $"自动关屏: {(autoScreenOff ? "开启" : "关闭")}");
            };

            var settingsItem = new ToolStripMenuItem(Tr("tray_settings"));
            settingsItem.Click += (s, e) => ShowSettingsWindow();

            var logItem = new ToolStripMenuItem(Tr("tray_log"));
            logItem.Click += (s, e) => ShowLogViewer();

            var quitItem = new ToolStripMenuItem(Tr("tray_quit"));
            quitItem.Click += (s, e) =>
            {
                LogInfo("应用", "用户退出");
                SystemEvents.PowerModeChanged -= OnPowerModeChanged;
                monitorTimer?.Stop();
                monitorTimer?.Dispose();
                trayIcon.Visible = false;
                Application.Exit();
            };

            menu.Items.Add(versionItem);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(autostartItem);
            menu.Items.Add(autoSleepItem);
            menu.Items.Add(autoScreenOffItem);
            menu.Items.Add(settingsItem);
            menu.Items.Add(logItem);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(quitItem);

            return menu;
        }

        private static void UpdateContextMenu()
        {
            if (trayIcon == null) return;
            var oldMenu = trayIcon.ContextMenuStrip;
            trayIcon.ContextMenuStrip = CreateContextMenu();
            oldMenu?.Dispose();
        }

        private static Icon CreateIcon(string state)
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
                g.SmoothingMode = SmoothingMode.AntiAlias;
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

        private static string GetIconState()
        {
            if (monitor.IsWaitingForApp) return "waiting";
            if (monitor.IsWarmup) return "warmup";
            if (monitor.IsOnline) return "online";
            return "offline";
        }

        private static string GetStatusText()
        {
            if (monitor.IsWaitingForApp)
                return Tr("tray_waiting", APP_NAME, config.AppProcessName);
            if (monitor.IsWarmup)
                return Tr("tray_warmup", APP_NAME, monitor.WarmupRemaining);
            if (monitor.IsOnline)
                return Tr("tray_online", APP_NAME);
            return Tr("tray_offline", APP_NAME, monitor.OfflineSeconds, config.OfflineSeconds);
        }

        // UpdateIcon 只在 UI 线程上调用 (由 Timer Tick 触发)，不再有跨线程问题
        // 仅在状态变化时重建图标，避免 GDI handle 泄漏
        internal static void UpdateIcon()
        {
            if (trayIcon == null) return;
            try
            {
                string currentState = GetIconState();
                string statusText = GetStatusText();
                if (statusText.Length > 63) statusText = statusText.Substring(0, 63);

                // 兜底：定期无条件重发一次图标与文字，防止某次通知丢失后永久卡死
                if (++ticksSinceShellSync >= ShellResyncSeconds)
                {
                    ticksSinceShellSync = 0;
                    lastIconState = null;                 // 迫使下面重建图标
                    if (trayIcon.Text.Length > 0)
                        trayIcon.Text = string.Empty;     // 迫使文字重新下发
                }

                // 图标只取决于状态。文字变化 (离线倒计时每秒都在变) 不该触发图标重建，
                // 否则等于每秒创建一个 GDI 图标
                if (currentState != lastIconState)
                {
                    var oldIcon = trayIcon.Icon;
                    trayIcon.Icon = CreateIcon(currentState);
                    oldIcon?.Dispose();
                    lastIconState = currentState;
                }

                trayIcon.Text = statusText;
            }
            catch (Exception ex)
            {
                // 绝不静默吞掉 (托盘卡死曾经就是从这里查不出原因)，但失败会每秒重试，
                // 所以限流到每分钟最多一条
                if ((DateTime.Now - lastTrayErrorLog).TotalSeconds >= 60)
                {
                    lastTrayErrorLog = DateTime.Now;
                    LogError("托盘", $"托盘图标更新失败: {Describe(ex)}");
                }
            }
        }
    }
}
