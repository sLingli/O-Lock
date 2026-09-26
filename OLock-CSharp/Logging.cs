using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows.Forms;

using static OLock.Localization;
using static OLock.Program;

namespace OLock
{
    // 日志：写文件 + 大小轮转 + 按保留天数自动清理 + 内置查看器
    // 格式: [时间戳] LEVEL: [组件] 消息 (源码位置，仅 ERROR)
    internal static class Logging
    {
        private static readonly object logLock = new object();
        private static string? logFilePath;
        private static DateTime lastCleanupDate = DateTime.MinValue; // 上次日志清理的日期

        internal static void InitLogger()
        {
            logFilePath = Path.Combine(AppContext.BaseDirectory, "olock.log");
        }

        // 写入核心
        private static void Write(string level, string component, string message, string? location)
        {
            if (logFilePath == null) return;
            var line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {level}: [{component}] {message}";
            if (!string.IsNullOrEmpty(location))
                line += $" ({location})";
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
                catch { } // 日志自身的失败保持静默，避免递归
            }
        }

        // 供状态机等经委托调用的路径：不捕获源码位置 (委托调用会拿到错误的捕获点)
        internal static void Log(string component, string level, string message)
            => Write(level, component, message, null);

        internal static void LogInfo(string component, string message)
            => Write("INFO", component, message, null);

        // 详细日志：仅在设置开启"详细日志"后写入
        internal static void LogDebug(string component, string message)
        {
            if (!config.LogVerbose) return;
            Write("DEBUG", component, message, null);
        }

        // ERROR 自动附带源码位置，便于定位问题代码
        internal static void LogError(string component, string message,
            [CallerFilePath] string file = "", [CallerLineNumber] int line = 0)
        {
            string? location = null;
            if (!string.IsNullOrEmpty(file) && line > 0)
                location = $"{Path.GetFileName(file)}:{line}";
            Write("ERROR", component, message, location);
        }

        // 异常 → 完整文本 (类型 + 消息 + 堆栈)，超长截断
        internal static string Describe(Exception ex)
        {
            var text = ex.ToString();
            return text.Length <= 2000 ? text : text.Substring(0, 2000) + "…(已截断)";
        }

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
                LogInfo("日志", $"清理完成: 删除 {lines.Length - kept.Count} 条过期记录 (保留最近 {config.LogRetentionDays} 天)");
            }
            catch (Exception ex)
            {
                LogError("日志", $"日志清理失败: {Describe(ex)}");
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
