using System;
using System.Drawing;
using System.Windows.Forms;

using static OLock.AppConfigStorage;
using static OLock.Localization;
using static OLock.Logging;
using static OLock.Program;

namespace OLock
{
    // 内置设置窗口
    internal static class SettingsForm
    {
        internal static void ShowSettingsWindow()
        {
            var form = new Form
            {
                Text = Tr("tray_settings"),
                Width = 480,
                Height = 422,
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

            // 离线容忍
            form.Controls.Add(new Label { Text = "离线容忍 (秒)", Left = 10, Top = y + 3, Width = labelW });
            var numThreshold = new NumericUpDown { Left = inputX, Top = y, Width = inputW, Minimum = 1, Maximum = 120, Value = config.OfflineSeconds };
            form.Controls.Add(numThreshold);
            y += 32;

            // 缓冲期
            form.Controls.Add(new Label { Text = "缓冲期 (秒)", Left = 10, Top = y + 3, Width = labelW });
            var numWarmup = new NumericUpDown { Left = inputX, Top = y, Width = inputW, Minimum = 1, Maximum = 3600, Value = config.WarmupSeconds };
            form.Controls.Add(numWarmup);
            y += 32;

            // 日志保留
            form.Controls.Add(new Label { Text = "日志保留 (天)", Left = 10, Top = y + 3, Width = labelW });
            var numRetention = new NumericUpDown { Left = inputX, Top = y, Width = inputW, Minimum = 1, Maximum = 365, Value = config.LogRetentionDays };
            form.Controls.Add(numRetention);
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
                config.OfflineSeconds = (int)numThreshold.Value;
                config.WarmupSeconds = (int)numWarmup.Value;
                config.LogRetentionDays = (int)numRetention.Value;
                config.AllowedRemoteIpPrefixes = txtAllowed.Text.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries);
                config.IgnoredRemoteIpPrefixes = txtIgnored.Text.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries);
                config.SleepCommand = txtSleepCmd.Text.Trim();
                config.SleepArguments = txtSleepArgs.Text;
                config.Normalize();
                SaveSettings();
                CleanupExpiredEntries(); // 立即按新的保留天数清理
                LogInfo("设置已保存");
                form.Close();
            };

            cancelBtn.Click += (s, e) => form.Close();

            form.Controls.Add(saveBtn);
            form.Controls.Add(cancelBtn);
            form.Show();
        }
    }
}
