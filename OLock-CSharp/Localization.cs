using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.InteropServices;

namespace OLock
{
    // 多语言文本 (en / zh-Hans / zh-Hant)
    internal static class Localization
    {
        internal static string currentLang = "en";

        private static Dictionary<string, Dictionary<string, string>> Texts = new Dictionary<string, Dictionary<string, string>>
        {
            ["en"] = new Dictionary<string, string>
            {
                ["tray_waiting"] = "{0}: ⚪ Waiting for {1}",
                ["tray_warmup"] = "{0}: 🟡 Connecting... ({1}s)",
                ["tray_online"] = "{0}: 🟢 Phone online",
                ["tray_offline"] = "{0}: 🔴 Not detected ({1}/{2}s)",
                ["tray_autostart"] = "Start with Windows",
                ["tray_autosleep"] = "Sleep",
                ["tray_autoscreenoff"] = "Turn off screen",
                ["tray_settings"] = "Settings",
                ["tray_quit"] = "Quit",
                ["tray_log"] = "View log",
                ["tray_log_refresh"] = "Refresh",
                ["tray_log_clear"] = "Clear",
                ["tray_log_empty"] = "(No log entries yet)",
                ["tray_init"] = "{0}: Initializing...",
                ["tray_already_running"] = "{0} is already running."
            },
            ["zh-Hans"] = new Dictionary<string, string>
            {
                ["tray_waiting"] = "{0}: ⚪ 等待 {1}",
                ["tray_warmup"] = "{0}: 🟡 正在连接... ({1}秒)",
                ["tray_online"] = "{0}: 🟢 手机在线",
                ["tray_offline"] = "{0}: 🔴 未检测到 ({1}/{2} 秒)",
                ["tray_autostart"] = "开机自启",
                ["tray_autosleep"] = "睡眠",
                ["tray_autoscreenoff"] = "关闭屏幕",
                ["tray_settings"] = "设置",
                ["tray_quit"] = "退出",
                ["tray_log"] = "查看日志",
                ["tray_log_refresh"] = "刷新",
                ["tray_log_clear"] = "清空",
                ["tray_log_empty"] = "（暂无日志）",
                ["tray_init"] = "{0}: 初始化中...",
                ["tray_already_running"] = "{0} 已经在运行了。"
            },
            ["zh-Hant"] = new Dictionary<string, string>
            {
                ["tray_waiting"] = "{0}: ⚪ 等待 {1}",
                ["tray_warmup"] = "{0}: 🟡 正在連線... ({1}秒)",
                ["tray_online"] = "{0}: 🟢 手機在線",
                ["tray_offline"] = "{0}: 🔴 未偵測到 ({1}/{2} 秒)",
                ["tray_autostart"] = "開機自啟",
                ["tray_autosleep"] = "睡眠",
                ["tray_autoscreenoff"] = "關閉螢幕",
                ["tray_settings"] = "設定",
                ["tray_quit"] = "退出",
                ["tray_log"] = "檢視日誌",
                ["tray_log_refresh"] = "重新整理",
                ["tray_log_clear"] = "清空",
                ["tray_log_empty"] = "（暫無日誌）",
                ["tray_init"] = "{0}: 初始化中...",
                ["tray_already_running"] = "{0} 已經在執行了。"
            }
        };

        internal static string GetUILanguage()
        {
            HashSet<ushort> hansLangIds = new HashSet<ushort> { 0x0804, 0x1004 };
            HashSet<ushort> hantLangIds = new HashSet<ushort> { 0x0404, 0x0C04, 0x1404 };

            try
            {
                ushort langId = GetUserDefaultUILanguage();
                if (hansLangIds.Contains(langId)) return "zh-Hans";
                if (hantLangIds.Contains(langId)) return "zh-Hant";
            }
            catch { }

            try
            {
                string lang = CultureInfo.CurrentUICulture.Name.ToLower();
                if (lang.StartsWith("zh"))
                {
                    if (lang.Contains("tw") || lang.Contains("hk") || lang.Contains("mo") || lang.Contains("hant"))
                        return "zh-Hant";
                    return "zh-Hans";
                }
            }
            catch { }

            return "en";
        }

        [DllImport("kernel32.dll")]
        private static extern ushort GetUserDefaultUILanguage();

        internal static string Tr(string key, params object[] args)
        {
            var texts = Texts.ContainsKey(currentLang) ? Texts[currentLang] : Texts["en"];
            if (texts.TryGetValue(key, out string? template))
                return string.Format(template!, args);
            return key;
        }
    }
}
