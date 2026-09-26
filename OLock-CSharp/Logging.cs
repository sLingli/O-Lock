using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;
using System.Windows.Forms;

using static OLock.Localization;
using static OLock.Program;

namespace OLock
{
    // 日志级别（查看器过滤用）
    internal enum LogLevel
    {
        Debug,
        Info,
        Error,
        Unknown
    }

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

        // 从日志行解析级别，解析不出返回 Unknown（如轮转截断的半行）
        internal static LogLevel ParseLevel(string line)
        {
            if (line.Length < 22 || line[0] != '[')
                return LogLevel.Unknown;
            int close = line.IndexOf("] ", StringComparison.Ordinal);
            if (close < 0)
                return LogLevel.Unknown;
            string rest = line.Substring(close + 2);
            if (rest.StartsWith("ERROR:", StringComparison.Ordinal)) return LogLevel.Error;
            if (rest.StartsWith("INFO:", StringComparison.Ordinal)) return LogLevel.Info;
            if (rest.StartsWith("DEBUG:", StringComparison.Ordinal)) return LogLevel.Debug;
            return LogLevel.Unknown;
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
                Width = 780,
                Height = 560,
                StartPosition = FormStartPosition.CenterScreen,
                MinimizeBox = false,
                MaximizeBox = false,
                FormBorderStyle = FormBorderStyle.FixedDialog
            };

            // 工具条：搜索 + 级别过滤 + 操作按钮
            var toolPanel = new FlowLayoutPanel
            {
                Dock = DockStyle.Top,
                Height = 36,
                Padding = new Padding(4),
                FlowDirection = FlowDirection.LeftToRight
            };

            var searchBox = new TextBox { Width = 180, PlaceholderText = Tr("tray_log_search") };
            var levelCombo = new ComboBox { Width = 120, DropDownStyle = ComboBoxStyle.DropDownList };
            levelCombo.Items.AddRange(new object[]
            {
                Tr("tray_log_level_all"), Tr("tray_log_level_info"), Tr("tray_log_level_error")
            });
            levelCombo.SelectedIndex = 0;
            var refreshBtn = new Button { Text = Tr("tray_log_refresh"), Width = 70 };
            var copyBtn = new Button { Text = Tr("tray_log_copy"), Width = 80 };
            var clearBtn = new Button { Text = Tr("tray_log_clear"), Width = 70 };
            toolPanel.Controls.AddRange(new Control[] { searchBox, levelCombo, refreshBtn, copyBtn, clearBtn });

            var logBox = new RichTextBox
            {
                Dock = DockStyle.Fill,
                ReadOnly = true,
                Font = new Font("Consolas", 9f),
                WordWrap = false,
                BackColor = Color.White
            };

            string currentFilteredText = string.Empty;

            void AppendLine(string line)
            {
                var color = ParseLevel(line) switch
                {
                    LogLevel.Error => Color.Firebrick,
                    LogLevel.Debug => Color.Gray,
                    _ => Color.Black
                };
                logBox.SelectionStart = logBox.TextLength;
                logBox.SelectionLength = 0;
                logBox.SelectionColor = color;
                logBox.AppendText(line + Environment.NewLine);
            }

            void LoadLog()
            {
                logBox.Clear();
                var sb = new StringBuilder();
                try
                {
                    int levelFilter = levelCombo.SelectedIndex; // 0=全部 1=INFO及以上 2=仅ERROR
                    string needle = searchBox.Text.Trim();

                    string[] lines = File.Exists(logFilePath)
                        ? File.ReadAllLines(logFilePath!)
                        : new[] { Tr("tray_log_empty") };

                    foreach (var line in lines)
                    {
                        var level = ParseLevel(line);
                        if (levelFilter == 2 && level != LogLevel.Error) continue;
                        if (levelFilter == 1 && level == LogLevel.Debug) continue;
                        if (needle.Length > 0 && !line.Contains(needle, StringComparison.OrdinalIgnoreCase)) continue;
                        AppendLine(line);
                        sb.AppendLine(line);
                    }
                    currentFilteredText = sb.ToString();
                }
                catch (Exception ex)
                {
                    AppendLine($"Error: {ex.Message}");
                }
            }

            refreshBtn.Click += (s, e) => LoadLog();
            levelCombo.SelectedIndexChanged += (s, e) => LoadLog();
            searchBox.TextChanged += (s, e) => LoadLog();

            copyBtn.Click += (s, e) =>
            {
                if (currentFilteredText.Length > 0)
                    Clipboard.SetText(currentFilteredText);
            };

            clearBtn.Click += (s, e) =>
            {
                try
                {
                    lock (logLock) { File.WriteAllText(logFilePath!, string.Empty); }
                    LoadLog();
                }
                catch { }
            };

            LoadLog();
            form.Controls.Add(logBox);
            form.Controls.Add(toolPanel);
            form.Show();
        }
    }
}
