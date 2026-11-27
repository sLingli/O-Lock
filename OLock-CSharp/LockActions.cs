using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

using static OLock.Localization;
using static OLock.Logging;
using static OLock.Program;

namespace OLock
{
    // 锁屏 / 睡眠动作的执行层抽象：使用环境与测试环境的唯一分界点。
    // 监控与状态机只产出"该执行了"的决策，具体动不动作由注入的执行器决定——
    // 使用环境 = RealLockExecutor（真锁/真睡），测试环境 = DryRunLockExecutor（只报告，不动作）。
    internal interface ILockExecutor
    {
        // 锁屏（含可选的延迟关屏）
        void ExecuteLock();

        // 执行睡眠命令
        void ExecuteSleep();
    }

    // 使用环境执行器：真实调用 user32 / rundll32
    internal sealed class RealLockExecutor : ILockExecutor
    {
        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Auto)]
        private static extern IntPtr SendMessageTimeout(
            IntPtr hWnd,
            uint msg,
            IntPtr wParam,
            IntPtr lParam,
            uint fuFlags,
            uint uTimeout,
            out IntPtr lpdwResult);

        private const int HWND_BROADCAST = 0xFFFF;
        private const uint WM_SYSCOMMAND = 0x0112;
        private const int SC_MONITORPOWER = 0xF170;
        private const int MONITOR_OFF = 2;
        private const uint SMTO_ABORTIFHUNG = 0x0002;
        private const uint SCREEN_OFF_BROADCAST_TIMEOUT_MS = 2000;

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool LockWorkStation();

        private readonly Func<bool> lockWorkStationFn;
        private readonly Func<IntPtr, uint, IntPtr, IntPtr, uint, uint, IntPtr> sendMessageTimeoutFn;
        private readonly Func<int> getLastWin32ErrorFn;
        private readonly int screenOffDelayMs;
        private readonly Action? onScreenOffCompleted;

        public RealLockExecutor()
            : this(null, null, null, 500, null)
        {
        }

        internal RealLockExecutor(
            Func<bool>? lockWorkStation,
            Func<IntPtr, uint, IntPtr, IntPtr, uint, uint, IntPtr>? sendMessageTimeout,
            Func<int>? getLastWin32Error,
            int screenOffDelayMs = 500,
            Action? onScreenOffCompleted = null)
        {
            lockWorkStationFn = lockWorkStation ?? LockWorkStation;
            sendMessageTimeoutFn = sendMessageTimeout ?? ((hWnd, msg, wParam, lParam, flags, timeout) =>
                SendMessageTimeout(hWnd, msg, wParam, lParam, flags, timeout, out _));
            getLastWin32ErrorFn = getLastWin32Error ?? Marshal.GetLastWin32Error;
            this.screenOffDelayMs = Math.Max(0, screenOffDelayMs);
            this.onScreenOffCompleted = onScreenOffCompleted;
        }

        public void ExecuteLock()
        {
            LogInfo("Action", "Executing screen lock");
            if (!lockWorkStationFn())
            {
                int err = getLastWin32ErrorFn();
                LogError("Action", $"LockWorkStation failed (Win32 error {err})");
            }

            if (autoScreenOff)
            {
                // 在后台线程延迟后关屏，使用 SendMessageTimeout(SMTO_ABORTIFHUNG) 防止任意无响应窗口导致线程永久挂起
                Task.Run(() =>
                {
                    try
                    {
                        if (screenOffDelayMs > 0)
                            Thread.Sleep(screenOffDelayMs);
                        IntPtr result = sendMessageTimeoutFn(
                            (IntPtr)HWND_BROADCAST,
                            WM_SYSCOMMAND,
                            (IntPtr)SC_MONITORPOWER,
                            (IntPtr)MONITOR_OFF,
                            SMTO_ABORTIFHUNG,
                            SCREEN_OFF_BROADCAST_TIMEOUT_MS);
                        if (result == IntPtr.Zero)
                        {
                            int err = getLastWin32ErrorFn();
                            LogError("Action", $"Screen-off broadcast timed out or failed (Win32 error {err})");
                        }
                        else
                        {
                            LogInfo("Action", "Turning off screen");
                        }
                    }
                    catch (Exception ex)
                    {
                        LogError("Action", $"Screen-off command failed: {Describe(ex)}");
                    }
                    finally
                    {
                        onScreenOffCompleted?.Invoke();
                    }
                });
            }
        }

        public void ExecuteSleep()
        {
            LogInfo("Action", "Executing sleep command");
            var cfg = config;
            // 在后台线程执行，避免阻塞 UI
            Task.Run(() =>
            {
                try
                {
                    using (var proc = Process.Start(new ProcessStartInfo
                    {
                        FileName = cfg.SleepCommand,
                        Arguments = cfg.SleepArguments,
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
                    LogError("Action", $"Sleep command execution failed: {Describe(ex)}");
                }
            });
        }
    }

    // 测试环境执行器：只报告，不执行任何真实动作（不碰 user32 / rundll32）。
    // 断开测试用：指令路径走通、决策链完整，但屏幕不锁、电脑不睡。
    internal sealed class DryRunLockExecutor : ILockExecutor
    {
        private readonly Action<string> report;
        private readonly string sleepCommand;
        private readonly string sleepArguments;

        public DryRunLockExecutor(Action<string> report, string? sleepCommand = null, string? sleepArguments = null)
        {
            this.report = report ?? (_ => { });
            var cfg = config;
            this.sleepCommand = sleepCommand ?? cfg.SleepCommand;
            this.sleepArguments = sleepArguments ?? cfg.SleepArguments;
        }

        public void ExecuteLock()
        {
            LogInfo("Drill", "Simulating screen lock command");
            report(Tr("test_drill_report_lock"));
        }

        public void ExecuteSleep()
        {
            LogInfo("Drill", $"Simulating sleep command: {sleepCommand} {sleepArguments}");
            report(Tr("test_drill_report_sleep", sleepCommand, sleepArguments));
        }
    }

    // 屏幕锁定状态查询（查询不属于执行动作，与执行器分开）
    internal static class LockActions
    {
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
    }
}
