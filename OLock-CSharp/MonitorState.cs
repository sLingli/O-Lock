using System;

namespace OLock
{
    // 状态机每秒决策后需要外壳执行的动作
    internal enum MonitorAction
    {
        None,
        // 离线处置：外壳根据"自动睡眠"开关映射为执行睡眠或锁屏
        Lock
    }

    // 监控状态机：纯逻辑，不接触 UI / WinForms / 网络，可单元测试。
    // 外壳 (Program.MonitorTick) 每秒收集一次事实 (屏幕锁定/进程存活/手机连接) 并调用 Tick，
    // 按返回的动作执行锁屏/睡眠，并负责刷新托盘图标。
    internal class MonitorStateMachine
    {
        private readonly AppConfig config;
        private readonly Action<string> info;
        private readonly Action<string> error;

        public MonitorStateMachine(AppConfig config, Action<string> info, Action<string> error)
        {
            this.config = config;
            this.info = info ?? (_ => { });
            this.error = error ?? (_ => { });

            // 初始为灰色状态，不写日志（由外壳在启动时显式调用 ResetToWaiting）
            IsWaitingForApp = true;
            IsWarmup = false;
            IsOnline = false;
            OfflineSeconds = 0;
            LastCheckSuccessTime = DateTime.MinValue;
        }

        // 状态快照（可读写，便于测试构造场景）
        public bool IsWaitingForApp { get; set; }
        public bool IsWarmup { get; set; }
        public bool IsOnline { get; set; }
        public bool WaitReconnectAfterDrill { get; set; }
        public int WarmupRemaining { get; set; }
        public int OfflineSeconds { get; set; }
        public bool WasLocked { get; set; }
        public DateTime LastCheckSuccessTime { get; set; }

        // 回到灰色状态（等待应用启动）：解锁、S3 唤醒、进程退出等场景
        public void ResetToWaiting()
        {
            IsWaitingForApp = true;
            IsWarmup = false;
            IsOnline = false;
            WaitReconnectAfterDrill = false;
            OfflineSeconds = 0;
            LastCheckSuccessTime = DateTime.MinValue;
            info("State: Waiting for app to start");
        }

        // 进入黄色缓冲期
        public void StartWarmup()
        {
            int warmupTime = Math.Max(config.MinWarmupSeconds, Math.Min(config.MaxWarmupSeconds, config.WarmupSeconds));
            IsWarmup = true;
            IsWaitingForApp = false;
            WaitReconnectAfterDrill = false;
            WarmupRemaining = warmupTime;
            OfflineSeconds = 0;
            IsOnline = false;
            info($"State: Warmup started ({warmupTime}s)");
        }

        // 断开测试开始：强制对齐为在线监控态，确保无论此前处于等待期还是缓冲期，
        // 一旦手机断开即可直接进入 1..N 秒离线倒数，不会卡在缓冲期。
        public void ForceOnlineForDrill()
        {
            IsWaitingForApp = false;
            IsWarmup = false;
            IsOnline = true;
            WaitReconnectAfterDrill = false;
            WarmupRemaining = 0;
            OfflineSeconds = 0;
            LastCheckSuccessTime = DateTime.Now;
            info("State: Drill started, aligned to online monitoring state");
        }

        // 断开测试结束（手机仍处于断开状态）：进入安全的等待重连态，
        // 期间不累加离线计数、不走缓冲期超时，直到手机重新连接后再恢复正常守护，防止测完后误锁屏/误睡眠。
        public void EnterPostDrillWaitReconnect()
        {
            IsWaitingForApp = false;
            IsWarmup = false;
            IsOnline = false;
            WaitReconnectAfterDrill = true;
            WarmupRemaining = 0;
            OfflineSeconds = 0;
            LastCheckSuccessTime = DateTime.MinValue;
            info("State: Drill ended, waiting for phone reconnect before resuming lock guard");
        }

        // 每秒推进一次。
        // phoneConnected: true/false = 本秒的检查结果；null = 本秒未执行检查（检查在途/等待阶段），不计入离线
        // 返回本秒需要外壳执行的动作
        public MonitorAction Tick(bool screenLocked, bool appRunning, bool? phoneConnected)
        {
            // 0. isOnline 过期保护：太久没有成功检查结果，强制标记离线
            if (IsOnline && LastCheckSuccessTime != DateTime.MinValue &&
                (DateTime.Now - LastCheckSuccessTime).TotalSeconds > config.OfflineSeconds * 3)
            {
                error($"Check result expired ({(int)(DateTime.Now - LastCheckSuccessTime).TotalSeconds}s without result), forcing offline");
                IsOnline = false;
            }

            // 1. 解锁瞬间：重置回灰色状态，本秒不再做其他事
            if (WasLocked && !screenLocked)
            {
                WasLocked = false;
                info("Screen unlocked");
                if (WaitReconnectAfterDrill)
                {
                    // 演练后若手机仍未重连，屏幕解锁保持等待重连保护，不降级为缓冲期/常规等待，防止误锁
                    return MonitorAction.None;
                }
                ResetToWaiting();
                return MonitorAction.None;
            }
            if (!WasLocked && screenLocked)
                info("Screen locked");
            WasLocked = screenLocked;

            // 2. 锁屏期间暂停监控
            if (screenLocked)
                return MonitorAction.None;

            // 3. 非空检查结果视为有效检查，盖时间戳（检查完成即更新，与结果无关）
            if (phoneConnected.HasValue)
                LastCheckSuccessTime = DateTime.Now;

            // 3.5 演练结束后的等待重连保护：手机未重连前保持静默，不触发缓冲期超时或离线锁屏
            if (WaitReconnectAfterDrill)
            {
                if (!appRunning)
                {
                    ResetToWaiting();
                    return MonitorAction.None;
                }

                if (phoneConnected == true)
                {
                    WaitReconnectAfterDrill = false;
                    IsOnline = true;
                    OfflineSeconds = 0;
                    info("State: Phone reconnected, normal monitoring resumed");
                }
                return MonitorAction.None;
            }

            // 4. 等待主程序启动阶段 (灰色)
            if (IsWaitingForApp)
            {
                if (appRunning)
                    StartWarmup();
                return MonitorAction.None;
            }

            // 5. 缓冲期阶段 (黄色)：每秒检查，倒计时按真实秒数递减
            if (IsWarmup)
            {
                if (!appRunning)
                {
                    ResetToWaiting();
                    return MonitorAction.None;
                }

                if (phoneConnected == true)
                {
                    IsWarmup = false;
                    WarmupRemaining = 0;
                    OfflineSeconds = 0;
                    IsOnline = true;
                    info("State: Phone connected (during warmup)");
                }
                else if (phoneConnected == false)
                {
                    WarmupRemaining--;
                    if (WarmupRemaining <= 0)
                    {
                        info("Warmup timed out, phone not connected");
                        ResetToWaiting();
                        return MonitorAction.Lock;
                    }
                }
                return MonitorAction.None;
            }

            // 6. 正常监控阶段 (绿色/红色)
            if (!appRunning)
            {
                ResetToWaiting();
                return MonitorAction.None;
            }

            if (phoneConnected == true)
            {
                if (!IsOnline) info("State: Phone online");
                IsOnline = true;
                OfflineSeconds = 0;
            }
            else if (phoneConnected == false)
            {
                if (IsOnline) info("Phone offline (process running, but no matching connection)");
                IsOnline = false;
                OfflineSeconds++;

                if (OfflineSeconds >= config.OfflineSeconds)
                {
                    info($"Phone offline for {OfflineSeconds}s, executing lock");
                    OfflineSeconds = 0;
                    return MonitorAction.Lock;
                }
            }

            return MonitorAction.None;
        }
    }
}
