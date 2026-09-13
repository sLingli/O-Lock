using System;
using System.Drawing;
using System.IO;
using System.Windows.Forms;

using static OLock.Localization;
using static OLock.Program;

namespace OLock
{
    // 日志：写文件 + 大小轮转 + 内置查看器
    internal static class Logging
    {
        private static readonly object logLock = new object();
        private static string? logFilePath;

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
