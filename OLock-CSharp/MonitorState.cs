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
            OfflineSeconds = 0;
            LastCheckSuccessTime = DateTime.MinValue;
            info("状态: 等待应用启动");
        }

        // 进入黄色缓冲期
        public void StartWarmup()
        {
            int warmupTime = Math.Max(config.MinWarmupSeconds, Math.Min(config.MaxWarmupSeconds, config.WarmupSeconds));
            IsWarmup = true;
            IsWaitingForApp = false;
            WarmupRemaining = warmupTime;
            OfflineSeconds = 0;
            IsOnline = false;
            info($"状态: 缓冲期开始 ({warmupTime}秒)");
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
                error($"检查结果过期 ({(int)(DateTime.Now - LastCheckSuccessTime).TotalSeconds}秒无结果)，强制标记离线");
                IsOnline = false;
            }

            // 1. 解锁瞬间：重置回灰色状态，本秒不再做其他事
            if (WasLocked && !screenLocked)
            {
                WasLocked = false;
                info("屏幕解锁");
                ResetToWaiting();
                return MonitorAction.None;
            }
            WasLocked = screenLocked;

            // 2. 锁屏期间暂停监控
            if (screenLocked)
                return MonitorAction.None;

            // 3. 非空检查结果视为有效检查，盖时间戳（检查完成即更新，与结果无关）
            if (phoneConnected.HasValue)
                LastCheckSuccessTime = DateTime.Now;

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
                    info("状态: 手机已连接 (缓冲期内)");
                }
                else if (phoneConnected == false)
                {
                    WarmupRemaining--;
                    if (WarmupRemaining <= 0)
                    {
                        info("缓冲期超时，手机未连接");
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
                if (!IsOnline) info("状态: 手机在线");
                IsOnline = true;
                OfflineSeconds = 0;
            }
            else if (phoneConnected == false)
            {
                if (IsOnline) info("状态: 手机离线");
                IsOnline = false;
                OfflineSeconds++;

                if (OfflineSeconds >= config.OfflineSeconds)
                {
                    info($"手机已离线 {OfflineSeconds} 秒，执行锁屏");
                    OfflineSeconds = 0;
                    return MonitorAction.Lock;
                }
            }

            return MonitorAction.None;
        }
    }
}
