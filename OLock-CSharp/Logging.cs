using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Windows.Forms;

using static OLock.Localization;
using static OLock.Program;

namespace OLock
{
    // 日志：写文件 + 大小轮转 + 按保留天数自动清理 + 内置查看器
    internal static class Logging
    {
        private static readonly object logLock = new object();
        private static string? logFilePath;
        private static DateTime lastCleanupDate = DateTime.MinValue; // 上次日志清理的日期

        internal static void InitLogger()
        {
            logFilePath = Path.Combine(AppContext.BaseDirectory, "olock.log");
        }

        private static void Log(string level, string message)
        {
            if (logFilePath == null) return;
            var line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {level}: {message}";
            lock (logLock)
            {
                try
                {
                    // 跨天后首次写日志时顺带清理一次过期日志
                    CleanupIfNeeded();

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

        internal static void LogInfo(string msg) => Log("INFO", msg);
        internal static void LogError(string msg) => Log("ERROR", msg);

        // 删除超过保留期的日志行 (config.LogRetentionDays)。
        // 启动时和保存设置后由外壳调用，此后每天首次写日志时自动再清一次。
        internal static void CleanupExpiredEntries()
        {
            lastCleanupDate = DateTime.Today; // 无论结果如何，当天不再重复清理
            try
            {
                if (logFilePath == null || !File.Exists(logFilePath))
                    return;

                var cutoff = DateTime.Now.AddDays(-config.LogRetentionDays);
                string[] lines = File.ReadAllLines(logFilePath);
                List<string> kept = FilterLines(lines, cutoff);
                if (kept.Count == lines.Length)
                    return;

                File.WriteAllLines(logFilePath, kept);
                LogInfo($"日志清理: 删除 {lines.Length - kept.Count} 条过期记录 (保留最近 {config.LogRetentionDays} 天)");
            }
            catch (Exception ex)
            {
                LogError($"日志清理失败: {ex.Message}");
            }
        }

        // 日期变化后首次写日志时清理一次
        private static void CleanupIfNeeded()
        {
            var today = DateTime.Today;
            if (lastCleanupDate == today)
                return;
            lastCleanupDate = today;
            CleanupExpiredEntries();
        }

        // 过滤日志行：解析得出时间戳且早于 cutoff 的行被丢弃，解析不出的行保留
        internal static List<string> FilterLines(IEnumerable<string> lines, DateTime cutoff)
        {
            var kept = new List<string>();
            foreach (var line in lines)
            {
                if (TryParseTimestamp(line, out DateTime ts) && ts < cutoff)
                    continue;
                kept.Add(line);
            }
            return kept;
        }

        // 行格式: "[yyyy-MM-dd HH:mm:ss] LEVEL: message"
        private static bool TryParseTimestamp(string line, out DateTime timestamp)
        {
            timestamp = DateTime.MinValue;
            if (line.Length < 21 || line[0] != '[')
                return false;
            return DateTime.TryParseExact(line.Substring(1, 19), "yyyy-MM-dd HH:mm:ss",
                CultureInfo.InvariantCulture, DateTimeStyles.None, out timestamp);
        }

        internal static void ShowLogViewer()
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
                        textBox.Text = File.ReadAllText(logFilePath!);
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
                    lock (logLock) { File.WriteAllText(logFilePath!, string.Empty); }
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
    }
}
