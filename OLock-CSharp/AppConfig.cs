using System;
using Microsoft.Win32;

using static OLock.Logging;
using static OLock.Program;

namespace OLock
{
    // 应用配置模型
    internal class AppConfig
    {
        public string AppProcessName { get; set; } = "";
        public string[] ConnectionProcessNames { get; set; } = Array.Empty<string>();
        public int OfflineSeconds { get; set; }
        public int WarmupSeconds { get; set; }
        public int MinWarmupSeconds { get; set; }
        public int MaxWarmupSeconds { get; set; }
        public string[] AllowedRemoteIpPrefixes { get; set; } = Array.Empty<string>();
        public string[] IgnoredRemoteIpPrefixes { get; set; } = Array.Empty<string>();
        public string SleepCommand { get; set; } = "";
        public string SleepArguments { get; set; } = "";
        public int LogRetentionDays { get; set; }
        public bool LogVerbose { get; set; }

        public static AppConfig CreateDefault()
        {
            return new AppConfig
            {
                AppProcessName = "O+Connect",
                ConnectionProcessNames = new[] { "pantaChannelService", "O+Connect" },
                OfflineSeconds = 9,
                WarmupSeconds = 60,
                MinWarmupSeconds = 30,
                MaxWarmupSeconds = 600,
                AllowedRemoteIpPrefixes = new[] { "192.168.", "10." },
                IgnoredRemoteIpPrefixes = new string[0],
                SleepCommand = "rundll32.exe",
                SleepArguments = "powrprof.dll,SetSuspendState 0,1,0",
                LogRetentionDays = 7,
                LogVerbose = false
            };
        }

        public void Normalize()
        {
            if (string.IsNullOrWhiteSpace(AppProcessName))
                AppProcessName = "O+Connect";

            // 手机连接可能归 OPPO 家族任一进程所有，列表为空时回退到已知成员
            if (ConnectionProcessNames == null || ConnectionProcessNames.Length == 0)
                ConnectionProcessNames = new[] { "pantaChannelService", "O+Connect" };

            OfflineSeconds = Clamp(OfflineSeconds, 1, 120, 9);
            MinWarmupSeconds = Clamp(MinWarmupSeconds, 1, 3600, 30);
            MaxWarmupSeconds = Clamp(MaxWarmupSeconds, MinWarmupSeconds, 3600, 600);
            WarmupSeconds = Clamp(WarmupSeconds, MinWarmupSeconds, MaxWarmupSeconds, 60);

            if (AllowedRemoteIpPrefixes == null || AllowedRemoteIpPrefixes.Length == 0)
                AllowedRemoteIpPrefixes = new[] { "192.168.", "10." };

            if (IgnoredRemoteIpPrefixes == null)
                IgnoredRemoteIpPrefixes = new string[0];

            if (string.IsNullOrWhiteSpace(SleepCommand))
                SleepCommand = "rundll32.exe";

            if (SleepArguments == null)
                SleepArguments = string.Empty;

            LogRetentionDays = Clamp(LogRetentionDays, 1, 365, 7);
        }

        private static int Clamp(int value, int min, int max, int fallback)
        {
            if (value <= 0)
                value = fallback;
            if (value < min)
                return min;
            if (value > max)
                return max;
            return value;
        }

        // 旧版设置迁移: 离线次数 × 检测间隔(注册表未记录时按默认 3 秒) = 容忍秒数
        internal static int MigrateOfflineSeconds(int oldOfflineThreshold, int? oldCheckIntervalSeconds)
        {
            return oldOfflineThreshold * (oldCheckIntervalSeconds ?? 3);
        }
    }

    // 配置持久化：注册表 (HKCU\Software\OLock) 是唯一配置源。
    // 钳制不放在 LoadSettings 里，由调用方在其后调用 config.Normalize()，
    // 否则读取过程中的异常会把钳制一起跳过。
    internal static class AppConfigStorage
    {
        internal static void LoadSettings()
        {
            try
            {
                using (var key = Registry.CurrentUser.OpenSubKey(@"Software\OLock", false))
                {
                    if (key != null)
                    {
                        // 配置项
                        var v = key.GetValue("AppProcessName"); if (v != null) config.AppProcessName = v.ToString()!;
                        v = key.GetValue("ConnectionProcessNames"); if (v != null) config.ConnectionProcessNames = v.ToString()!.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries);
                        v = key.GetValue("OfflineSeconds");
                        if (v != null)
                        {
                            config.OfflineSeconds = Convert.ToInt32(v);
                        }
                        else
                        {
                            // 迁移旧版设置: 次数 × 检测间隔(默认3秒) = 容忍秒数
                            var oldThreshold = key.GetValue("OfflineThreshold");
                            if (oldThreshold != null)
                            {
                                var oldInterval = key.GetValue("CheckIntervalSeconds");
                                config.OfflineSeconds = AppConfig.MigrateOfflineSeconds(
                                    Convert.ToInt32(oldThreshold),
                                    oldInterval != null ? Convert.ToInt32(oldInterval) : null);
                            }
                        }
                        v = key.GetValue("WarmupSeconds"); if (v != null) config.WarmupSeconds = Convert.ToInt32(v);
                        v = key.GetValue("MinWarmupSeconds"); if (v != null) config.MinWarmupSeconds = Convert.ToInt32(v);
                        v = key.GetValue("MaxWarmupSeconds"); if (v != null) config.MaxWarmupSeconds = Convert.ToInt32(v);
                        v = key.GetValue("AllowedRemoteIpPrefixes"); if (v != null) config.AllowedRemoteIpPrefixes = v.ToString()!.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries);
                        v = key.GetValue("IgnoredRemoteIpPrefixes"); if (v != null) config.IgnoredRemoteIpPrefixes = v.ToString()!.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries);
                        v = key.GetValue("SleepCommand"); if (v != null) config.SleepCommand = v.ToString()!;
                        v = key.GetValue("SleepArguments"); if (v != null) config.SleepArguments = v.ToString()!;
                        v = key.GetValue("LogRetentionDays"); if (v != null) config.LogRetentionDays = Convert.ToInt32(v);

                        // 用户偏好
                        var sleepVal = key.GetValue("AutoSleep");
                        if (sleepVal != null) autoSleep = Convert.ToBoolean(sleepVal);
                        var screenOffVal = key.GetValue("AutoScreenOff");
                        if (screenOffVal != null) autoScreenOff = Convert.ToBoolean(screenOffVal);
                        var verboseVal = key.GetValue("LogVerbose");
                        if (verboseVal != null) config.LogVerbose = Convert.ToBoolean(verboseVal);
                    }
                }
            }
            catch (Exception ex)
            {
                LogError("配置", $"注册表设置加载失败: {Describe(ex)}");
            }
        }

        internal static void SaveSettings()
        {
            try
            {
                using (var key = Registry.CurrentUser.CreateSubKey(@"Software\OLock"))
                {
                    if (key != null)
                    {
                        // 配置项
                        key.SetValue("AppProcessName", config.AppProcessName);
                        key.SetValue("ConnectionProcessNames", string.Join(",", config.ConnectionProcessNames));
                        key.SetValue("OfflineSeconds", config.OfflineSeconds);
                        key.SetValue("WarmupSeconds", config.WarmupSeconds);
                        key.SetValue("MinWarmupSeconds", config.MinWarmupSeconds);
                        key.SetValue("MaxWarmupSeconds", config.MaxWarmupSeconds);
                        key.SetValue("AllowedRemoteIpPrefixes", string.Join(",", config.AllowedRemoteIpPrefixes));
                        key.SetValue("IgnoredRemoteIpPrefixes", string.Join(",", config.IgnoredRemoteIpPrefixes));
                        key.SetValue("SleepCommand", config.SleepCommand);
                        key.SetValue("SleepArguments", config.SleepArguments);
                        key.SetValue("LogRetentionDays", config.LogRetentionDays);

                        // 用户偏好
                        key.SetValue("AutoSleep", autoSleep);
                        key.SetValue("AutoScreenOff", autoScreenOff);
                        key.SetValue("LogVerbose", config.LogVerbose);
                    }
                }
            }
            catch (Exception ex)
            {
                LogError("配置", $"设置保存失败: {Describe(ex)}");
            }
        }
    }
}
