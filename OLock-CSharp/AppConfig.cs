using System;
using System.Globalization;
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
            else
                AppProcessName = AppProcessName.Trim();

            // 手机连接可能归 OPPO 家族任一进程所有，列表为空时回退到已知成员
            ConnectionProcessNames = NormalizeList(ConnectionProcessNames, new[] { "pantaChannelService", "O+Connect" });

            OfflineSeconds = Clamp(OfflineSeconds, 1, 120, 9);
            MinWarmupSeconds = Clamp(MinWarmupSeconds, 1, 3600, 30);
            MaxWarmupSeconds = Clamp(MaxWarmupSeconds, MinWarmupSeconds, 3600, 600);
            WarmupSeconds = Clamp(WarmupSeconds, MinWarmupSeconds, MaxWarmupSeconds, 60);

            AllowedRemoteIpPrefixes = NormalizeList(AllowedRemoteIpPrefixes, new[] { "192.168.", "10." });
            IgnoredRemoteIpPrefixes = NormalizeList(IgnoredRemoteIpPrefixes, Array.Empty<string>());

            if (string.IsNullOrWhiteSpace(SleepCommand))
                SleepCommand = "rundll32.exe";
            else
                SleepCommand = SleepCommand.Trim();

            if (SleepArguments == null)
                SleepArguments = string.Empty;

            LogRetentionDays = Clamp(LogRetentionDays, 1, 365, 7);
        }

        // 去掉每项首尾空白并丢弃空项；清理后为空则回退到 fallback。
        // 逗号分隔的输入（设置窗口、注册表、JSON 时代遗留的值）常见 "a, b" 这种带空格写法，
        // 不 Trim 会让进程名永远匹配不上，而且不会有任何报错。
        private static string[] NormalizeList(string[]? values, string[] fallback)
        {
            if (values == null)
                return fallback;

            var cleaned = new List<string>(values.Length);
            foreach (string value in values)
            {
                if (string.IsNullOrWhiteSpace(value))
                    continue;
                cleaned.Add(value.Trim());
            }

            return cleaned.Count == 0 ? fallback : cleaned.ToArray();
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
                    if (key == null)
                        return;

                    // 配置项：每一项独立读取。单项类型异常只跳过该项，
                    // 不再中断后续全部设置（旧版一个坏值会让其余配置静默回退默认值）。
                    string? text = ReadString(key, "AppProcessName");
                    if (text != null) config.AppProcessName = text;

                    text = ReadString(key, "ConnectionProcessNames");
                    if (text != null) config.ConnectionProcessNames = SplitList(text);

                    int? number = ReadInt(key, "OfflineSeconds");
                    if (number != null)
                    {
                        config.OfflineSeconds = number.Value;
                    }
                    else
                    {
                        // 迁移旧版设置: 次数 × 检测间隔(默认3秒) = 容忍秒数
                        int? oldThreshold = ReadInt(key, "OfflineThreshold");
                        if (oldThreshold != null)
                        {
                            config.OfflineSeconds = AppConfig.MigrateOfflineSeconds(
                                oldThreshold.Value, ReadInt(key, "CheckIntervalSeconds"));
                        }
                    }

                    number = ReadInt(key, "WarmupSeconds"); if (number != null) config.WarmupSeconds = number.Value;
                    number = ReadInt(key, "MinWarmupSeconds"); if (number != null) config.MinWarmupSeconds = number.Value;
                    number = ReadInt(key, "MaxWarmupSeconds"); if (number != null) config.MaxWarmupSeconds = number.Value;

                    text = ReadString(key, "AllowedRemoteIpPrefixes");
                    if (text != null) config.AllowedRemoteIpPrefixes = SplitList(text);

                    text = ReadString(key, "IgnoredRemoteIpPrefixes");
                    if (text != null) config.IgnoredRemoteIpPrefixes = SplitList(text);

                    text = ReadString(key, "SleepCommand"); if (text != null) config.SleepCommand = text;
                    text = ReadString(key, "SleepArguments"); if (text != null) config.SleepArguments = text;

                    number = ReadInt(key, "LogRetentionDays"); if (number != null) config.LogRetentionDays = number.Value;

                    // 用户偏好
                    bool? flag = ReadBool(key, "AutoSleep"); if (flag != null) autoSleep = flag.Value;
                    flag = ReadBool(key, "AutoScreenOff"); if (flag != null) autoScreenOff = flag.Value;
                    flag = ReadBool(key, "LogVerbose"); if (flag != null) config.LogVerbose = flag.Value;
                }
            }
            catch (Exception ex)
            {
                LogError("配置", $"注册表设置加载失败: {Describe(ex)}");
            }
        }

        private static string[] SplitList(string value)
            => value.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries);

        // 下面三个读取器把「该项不存在」表达为 null，并保证单项损坏只影响该项自身
        private static string? ReadString(RegistryKey key, string name)
        {
            try
            {
                return key.GetValue(name)?.ToString();
            }
            catch (Exception ex)
            {
                LogError("配置", $"读取注册表项 {name} 失败，已跳过该项: {Describe(ex)}");
                return null;
            }
        }

        private static int? ReadInt(RegistryKey key, string name)
        {
            try
            {
                object? raw = key.GetValue(name);
                if (raw == null)
                    return null;
                if (raw is int intValue)
                    return intValue;
                if (int.TryParse(raw.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed))
                    return parsed;

                LogError("配置", $"注册表项 {name} 不是合法整数 (实际值: {raw})，已跳过该项");
                return null;
            }
            catch (Exception ex)
            {
                LogError("配置", $"读取注册表项 {name} 失败，已跳过该项: {Describe(ex)}");
                return null;
            }
        }

        private static bool? ReadBool(RegistryKey key, string name)
        {
            try
            {
                object? raw = key.GetValue(name);
                if (raw == null)
                    return null;
                if (raw is int intValue)
                    return intValue != 0;
                if (bool.TryParse(raw.ToString(), out bool parsed))
                    return parsed;

                LogError("配置", $"注册表项 {name} 不是合法布尔值 (实际值: {raw})，已跳过该项");
                return null;
            }
            catch (Exception ex)
            {
                LogError("配置", $"读取注册表项 {name} 失败，已跳过该项: {Describe(ex)}");
                return null;
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
