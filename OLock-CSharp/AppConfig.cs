using System;
using System.IO;
using System.Text.Json;
using Microsoft.Win32;

using static OLock.Logging;
using static OLock.Program;

namespace OLock
{
    // 应用配置模型
    internal class AppConfig
    {
        public string AppProcessName { get; set; } = "";
        public int OfflineSeconds { get; set; }
        public int WarmupSeconds { get; set; }
        public int MinWarmupSeconds { get; set; }
        public int MaxWarmupSeconds { get; set; }
        public string[] AllowedRemoteIpPrefixes { get; set; } = Array.Empty<string>();
        public string[] IgnoredRemoteIpPrefixes { get; set; } = Array.Empty<string>();
        public string SleepCommand { get; set; } = "";
        public string SleepArguments { get; set; } = "";

        public static AppConfig CreateDefault()
        {
            return new AppConfig
            {
                AppProcessName = "O+Connect",
                OfflineSeconds = 9,
                WarmupSeconds = 60,
                MinWarmupSeconds = 30,
                MaxWarmupSeconds = 600,
                AllowedRemoteIpPrefixes = new[] { "192.168.", "10." },
                IgnoredRemoteIpPrefixes = new string[0],
                SleepCommand = "rundll32.exe",
                SleepArguments = "powrprof.dll,SetSuspendState 0,1,0"
            };
        }

        public void Normalize()
        {
            if (string.IsNullOrWhiteSpace(AppProcessName))
                AppProcessName = "O+Connect";

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
    }

    // 配置持久化：注册表 (HKCU\Software\OLock) + 可选 JSON 文件覆盖
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
                                int interval = oldInterval != null ? Convert.ToInt32(oldInterval) : 3;
                                config.OfflineSeconds = Convert.ToInt32(oldThreshold) * interval;
                            }
                        }
                        v = key.GetValue("WarmupSeconds"); if (v != null) config.WarmupSeconds = Convert.ToInt32(v);
                        v = key.GetValue("MinWarmupSeconds"); if (v != null) config.MinWarmupSeconds = Convert.ToInt32(v);
                        v = key.GetValue("MaxWarmupSeconds"); if (v != null) config.MaxWarmupSeconds = Convert.ToInt32(v);
                        v = key.GetValue("AllowedRemoteIpPrefixes"); if (v != null) config.AllowedRemoteIpPrefixes = v.ToString()!.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries);
                        v = key.GetValue("IgnoredRemoteIpPrefixes"); if (v != null) config.IgnoredRemoteIpPrefixes = v.ToString()!.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries);
                        v = key.GetValue("SleepCommand"); if (v != null) config.SleepCommand = v.ToString()!;
                        v = key.GetValue("SleepArguments"); if (v != null) config.SleepArguments = v.ToString()!;

                        // 用户偏好
                        var sleepVal = key.GetValue("AutoSleep");
                        if (sleepVal != null) autoSleep = Convert.ToBoolean(sleepVal);
                        var screenOffVal = key.GetValue("AutoScreenOff");
                        if (screenOffVal != null) autoScreenOff = Convert.ToBoolean(screenOffVal);
                    }
                }
                config.Normalize();
            }
            catch { }
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
                        key.SetValue("OfflineSeconds", config.OfflineSeconds);
                        key.SetValue("WarmupSeconds", config.WarmupSeconds);
                        key.SetValue("MinWarmupSeconds", config.MinWarmupSeconds);
                        key.SetValue("MaxWarmupSeconds", config.MaxWarmupSeconds);
                        key.SetValue("AllowedRemoteIpPrefixes", string.Join(",", config.AllowedRemoteIpPrefixes));
                        key.SetValue("IgnoredRemoteIpPrefixes", string.Join(",", config.IgnoredRemoteIpPrefixes));
                        key.SetValue("SleepCommand", config.SleepCommand);
                        key.SetValue("SleepArguments", config.SleepArguments);

                        // 用户偏好
                        key.SetValue("AutoSleep", autoSleep);
                        key.SetValue("AutoScreenOff", autoScreenOff);
                    }
                }
            }
            catch { }
        }

        // 如果存在 JSON 配置文件则加载覆盖，返回 true 表示已加载
        internal static bool LoadJsonConfigIfExists()
        {
            string configPath = Path.Combine(AppContext.BaseDirectory, CONFIG_FILE_NAME);
            try
            {
                if (!File.Exists(configPath))
                    configPath = Path.Combine(Environment.CurrentDirectory, CONFIG_FILE_NAME);

                if (File.Exists(configPath))
                {
                    var options = new JsonSerializerOptions
                    {
                        PropertyNameCaseInsensitive = true,
                        ReadCommentHandling = JsonCommentHandling.Skip,
                        AllowTrailingCommas = true
                    };

                    AppConfig? fileConfig = JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(configPath), options);
                    if (fileConfig != null)
                    {
                        // 用 JSON 值覆盖当前配置（但不覆盖 AutoSleep/AutoScreenOff，它们由菜单控制）
                        config.AppProcessName = fileConfig.AppProcessName ?? config.AppProcessName;
                        config.OfflineSeconds = fileConfig.OfflineSeconds > 0 ? fileConfig.OfflineSeconds : config.OfflineSeconds;
                        config.MinWarmupSeconds = fileConfig.MinWarmupSeconds > 0 ? fileConfig.MinWarmupSeconds : config.MinWarmupSeconds;
                        config.MaxWarmupSeconds = fileConfig.MaxWarmupSeconds > 0 ? fileConfig.MaxWarmupSeconds : config.MaxWarmupSeconds;
                        config.WarmupSeconds = fileConfig.WarmupSeconds > 0 ? fileConfig.WarmupSeconds : config.WarmupSeconds;
                        if (fileConfig.AllowedRemoteIpPrefixes != null && fileConfig.AllowedRemoteIpPrefixes.Length > 0)
                            config.AllowedRemoteIpPrefixes = fileConfig.AllowedRemoteIpPrefixes;
                        if (fileConfig.IgnoredRemoteIpPrefixes != null)
                            config.IgnoredRemoteIpPrefixes = fileConfig.IgnoredRemoteIpPrefixes;
                        if (!string.IsNullOrWhiteSpace(fileConfig.SleepCommand))
                            config.SleepCommand = fileConfig.SleepCommand;
                        if (fileConfig.SleepArguments != null)
                            config.SleepArguments = fileConfig.SleepArguments;
                        LogInfo($"配置文件覆盖成功: {configPath}");
                        return true;
                    }
                }
            }
            catch (Exception ex)
            {
                LogError($"配置文件加载失败: {ex.Message}");
            }
            return false;
        }
    }
}
