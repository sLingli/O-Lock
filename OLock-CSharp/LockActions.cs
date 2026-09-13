using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

using static OLock.Logging;
using static OLock.Program;

namespace OLock
{
    // 锁屏 / 关屏 / 睡眠动作 + user32 P/Invoke
    internal static class LockActions
    {
        [DllImport("user32.dll")]
        private static extern IntPtr SendMessage(IntPtr hWnd, int Msg, IntPtr wParam, IntPtr lParam);

        private const int HWND_BROADCAST = 0xFFFF;
        private const int WM_SYSCOMMAND = 0x0112;
        private const int SC_MONITORPOWER = 0xF170;
        private const int MONITOR_OFF = 2;

        [DllImport("user32.dll")]
        private static extern bool LockWorkStation();

        [DllImport("user32.dll")]
        private static extern IntPtr OpenInputDesktop(uint dwFlags, bool fInherit, uint dwDesiredAccess);

        [DllImport("user32.dll")]
        private static extern bool CloseDesktop(IntPtr hDesktop);

        internal static bool IsScreenLocked()
        {
            IntPtr hDesktop = OpenInputDesktop(0, false, 0x0001);
            if (hDesktop != IntPtr.Zero)
            {
                CloseDesktop(hDesktop);
                return false;
            }
            return true;
        }

        internal static void TriggerLock()
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

        internal static void ExecuteSleep()
        {
            LogInfo("执行睡眠命令");
            // 在后台线程执行，避免阻塞 UI
            Task.Run(() =>
            {
                try
                {
                    using (var proc = Process.Start(new ProcessStartInfo
                    {
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
    }
}
