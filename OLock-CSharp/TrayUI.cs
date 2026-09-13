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
        private static NotifyIcon trayIcon;
        private static Form messageForm;
        private static string lastIconState = null;  // 缓存：上次图标状态，避免无变化时重复创建 Icon

        [DllImport("user32.dll")]
        private static extern bool DestroyIcon(IntPtr handle);

        internal static void InitTrayIcon()
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

        private static ContextMenuStrip CreateContextMenu()
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
            if (isWaitingForApp) return "waiting";
            if (isWarmup) return "warmup";
            if (isOnline) return "online";
            return "offline";
        }

        private static string GetStatusText()
        {
            if (isWaitingForApp)
                return Tr("tray_waiting", APP_NAME, config.AppProcessName);
            if (isWarmup)
                return Tr("tray_warmup", APP_NAME, warmupRemaining);
            if (isOnline)
                return Tr("tray_online", APP_NAME);
            return Tr("tray_offline", APP_NAME, offlineSeconds, config.OfflineSeconds);
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

                // 只在状态或文本变化时更新，避免每秒重建 Icon
                if (currentState != lastIconState || statusText != trayIcon.Text)
                {
                    var oldIcon = trayIcon.Icon;
                    trayIcon.Icon = CreateIcon(currentState);
                    oldIcon?.Dispose();
                    lastIconState = currentState;
                }

                trayIcon.Text = statusText;
            }
            catch { }
        }
    }
}
