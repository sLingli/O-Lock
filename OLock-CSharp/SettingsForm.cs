using System;
using System.Drawing;
using System.Windows.Forms;

using static OLock.AppConfigStorage;
using static OLock.Localization;
using static OLock.Logging;
using static OLock.Program;

namespace OLock
{
    // 内置设置窗口（单实例持窗，避免多开互相覆盖配置）
    internal static class SettingsForm
    {
        private static Form? activeForm;

        internal static void ShowSettingsWindow()
        {
            if (activeForm != null && !activeForm.IsDisposed)
            {
                if (activeForm.WindowState == FormWindowState.Minimized)
                    activeForm.WindowState = FormWindowState.Normal;
                activeForm.Show();
                activeForm.Activate();
                activeForm.BringToFront();
                return;
            }

            var currentConfig = config;

            var form = new Form
            {
                Text = $"{Tr("tray_settings")} - {APP_NAME} {AppVersion}",
                Width = 480,
                Height = 484,
                StartPosition = FormStartPosition.CenterScreen,
                MinimizeBox = false,
                MaximizeBox = false,
                FormBorderStyle = FormBorderStyle.FixedDialog
            };
            activeForm = form;
            form.FormClosed += (_, _) =>
            {
                if (ReferenceEquals(activeForm, form))
                    activeForm = null;
            };

            int y = 15, labelW = 120, inputX = 140, inputW = 300;

            // 监控进程名
            form.Controls.Add(new Label { Text = Tr("settings_process_name"), Left = 10, Top = y + 3, Width = labelW });
            var txtProcess = new TextBox { Left = inputX, Top = y, Width = inputW, Text = currentConfig.AppProcessName };
            form.Controls.Add(txtProcess);
            y += 32;

            // 离线容忍
            form.Controls.Add(new Label { Text = Tr("settings_offline_threshold"), Left = 10, Top = y + 3, Width = labelW });
            var numThreshold = new NumericUpDown
            {
                Left = inputX,
                Top = y,
                Width = inputW,
                Minimum = 1,
                Maximum = 120,
                Value = Math.Clamp(currentConfig.OfflineSeconds, 1, 120)
            };
            form.Controls.Add(numThreshold);
            y += 32;

            // 缓冲期（上下限与 AppConfig.Normalize 保持一致，避免保存时被静默钳制）
            int minWarmup = Math.Clamp(currentConfig.MinWarmupSeconds, 1, 3600);
            int maxWarmup = Math.Clamp(currentConfig.MaxWarmupSeconds, minWarmup, 3600);
            form.Controls.Add(new Label { Text = Tr("settings_warmup"), Left = 10, Top = y + 3, Width = labelW });
            var numWarmup = new NumericUpDown
            {
                Left = inputX,
                Top = y,
                Width = inputW,
                Minimum = minWarmup,
                Maximum = maxWarmup,
                Value = Math.Clamp(currentConfig.WarmupSeconds, minWarmup, maxWarmup)
            };
            form.Controls.Add(numWarmup);
            y += 32;

            // 日志保留
            form.Controls.Add(new Label { Text = Tr("settings_retention"), Left = 10, Top = y + 3, Width = labelW });
            var numRetention = new NumericUpDown
            {
                Left = inputX,
                Top = y,
                Width = inputW,
                Minimum = 1,
                Maximum = 365,
                Value = Math.Clamp(currentConfig.LogRetentionDays, 1, 365)
            };
            form.Controls.Add(numRetention);
            y += 32;

            // 允许的 IP 前缀（留空 = 自动匹配所有私网与局域网 IP）
            form.Controls.Add(new Label { Text = Tr("settings_allowed_prefixes"), Left = 10, Top = y + 3, Width = labelW });
            var txtAllowed = new TextBox
            {
                Left = inputX,
                Top = y,
                Width = inputW,
                Text = string.Join(",", currentConfig.AllowedRemoteIpPrefixes),
                PlaceholderText = Tr("settings_allowed_prefixes_hint")
            };
            form.Controls.Add(txtAllowed);
            y += 32;

            // 忽略的 IP 前缀
            form.Controls.Add(new Label { Text = Tr("settings_ignored_prefixes"), Left = 10, Top = y + 3, Width = labelW });
            var txtIgnored = new TextBox { Left = inputX, Top = y, Width = inputW, Text = string.Join(",", currentConfig.IgnoredRemoteIpPrefixes) };
            form.Controls.Add(txtIgnored);
            y += 32;

            // 睡眠命令
            form.Controls.Add(new Label { Text = Tr("settings_sleep_command"), Left = 10, Top = y + 3, Width = labelW });
            var txtSleepCmd = new TextBox { Left = inputX, Top = y, Width = inputW, Text = currentConfig.SleepCommand };
            form.Controls.Add(txtSleepCmd);
            y += 32;

            // 睡眠参数
            form.Controls.Add(new Label { Text = Tr("settings_sleep_arguments"), Left = 10, Top = y + 3, Width = labelW });
            var txtSleepArgs = new TextBox { Left = inputX, Top = y, Width = inputW, Text = currentConfig.SleepArguments };
            form.Controls.Add(txtSleepArgs);
            y += 32;

            // 详细日志
            var chkVerbose = new CheckBox { Left = inputX, Top = y + 3, Width = inputW, Text = Tr("settings_verbose_log"), Checked = currentConfig.LogVerbose };
            form.Controls.Add(chkVerbose);
            y += 30;

            // 启动时自动检查更新
            var chkCheckUpdates = new CheckBox { Left = inputX, Top = y + 3, Width = inputW, Text = Tr("settings_check_updates"), Checked = currentConfig.CheckUpdatesOnStartup };
            form.Controls.Add(chkCheckUpdates);
            y += 40;

            // 按钮
            var saveBtn = new Button { Text = Tr("settings_save"), Width = 80, Left = inputX + inputW - 170, Top = y };
            var cancelBtn = new Button { Text = Tr("settings_cancel"), Width = 80, Left = inputX + inputW - 80, Top = y };

            saveBtn.Click += (s, e) =>
            {
                ApplySettingsFromFields(
                    currentConfig,
                    txtProcess.Text,
                    (int)numThreshold.Value,
                    (int)numWarmup.Value,
                    txtAllowed.Text,
                    txtIgnored.Text,
                    txtSleepCmd.Text,
                    txtSleepArgs.Text,
                    (int)numRetention.Value,
                    chkVerbose.Checked,
                    chkCheckUpdates.Checked);
                form.Close();
            };

            cancelBtn.Click += (s, e) => form.Close();

            form.Controls.Add(saveBtn);
            form.Controls.Add(cancelBtn);
            form.Show();
            form.Activate();
        }

        // 先构建并 Normalize 新配置，再原子替换全局快照并同步状态机，避免后台线程读到未规整的中间状态
        internal static AppConfig ApplySettingsFromFields(
            AppConfig baseConfig,
            string processName,
            int offlineSeconds,
            int warmupSeconds,
            string allowedPrefixesCsv,
            string ignoredPrefixesCsv,
            string sleepCommand,
            string sleepArguments,
            int logRetentionDays,
            bool logVerbose,
            bool checkUpdatesOnStartup,
            string registrySubKey = DefaultRegistrySubKey)
        {
            var nextConfig = new AppConfig
            {
                AppProcessName = processName.Trim(),
                OfflineSeconds = offlineSeconds,
                WarmupSeconds = warmupSeconds,
                MinWarmupSeconds = baseConfig.MinWarmupSeconds,
                MaxWarmupSeconds = baseConfig.MaxWarmupSeconds,
                AllowedRemoteIpPrefixes = allowedPrefixesCsv.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries),
                IgnoredRemoteIpPrefixes = ignoredPrefixesCsv.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries),
                SleepCommand = sleepCommand.Trim(),
                SleepArguments = sleepArguments,
                LogRetentionDays = logRetentionDays,
                LogVerbose = logVerbose,
                CheckUpdatesOnStartup = checkUpdatesOnStartup
            };
            nextConfig.Normalize();

            config = nextConfig;
            monitor?.UpdateConfig(nextConfig);
            SaveSettings(registrySubKey);
            CleanupExpiredEntries(); // 立即按新的保留天数清理
            LogInfo("Config", "Settings saved");
            return nextConfig;
        }
    }
}
