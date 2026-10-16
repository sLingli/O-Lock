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
                ["tray_wait_reconnect"] = "{0}: ⚪ Waiting for phone reconnect",
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
                ["tray_log_copy"] = "Copy all",
                ["tray_log_search"] = "Search...",
                ["tray_log_level_all"] = "All levels",
                ["tray_log_level_info"] = "INFO and above",
                ["tray_log_level_error"] = "ERROR only",
                ["tray_log_empty"] = "(No log entries yet)",
                ["tray_init"] = "{0}: Initializing...",
                ["tray_already_running"] = "{0} is already running.",
                ["tray_test_connection"] = "Connection test…",
                ["test_title"] = "Connection Test",
                ["test_idle"] = "Not checked — click the button on the right to start",
                ["test_checking"] = "Checking…",
                ["test_check_now"] = "Check now",
                ["test_recheck"] = "Check again",
                ["test_connected"] = "Connected — Phone online",
                ["test_disconnected"] = "Not connected — Phone not detected",
                ["test_detail_process"] = "Process: {0}",
                ["test_detail_connection"] = "TCP connection: {0}",
                ["test_detail_diagnosis"] = "Diagnosis: {0}",
                ["test_proc_running"] = "Running",
                ["test_proc_not_running"] = "Not running",
                ["test_conn_hit"] = "Matched {0} (PID {1})",
                ["test_conn_established"] = "{0} established, none matched",
                ["test_conn_none"] = "No established connections",
                ["test_reason_connected"] = "Phone session active",
                ["test_reason_process"] = "Main process not running — please start OPPO Connect first",
                ["test_reason_no_connection"] = "Main process is running, but has no established connections",
                ["test_reason_ip_mismatch"] = "{0} established connection(s), but remote IP doesn't look like phone (samples: {1})",
                ["test_reason_check_failed"] = "TCP table query failed — see log for details",
                ["test_guide_disconnected"] = "Phone not detected. Connect your phone first; the disconnect test will appear here once online.",
                ["test_guide_connected"] = "Phone is online. Want to verify disconnect detection?",
                ["test_drill_start"] = "Start disconnect test",
                ["test_drill_end"] = "Stop test",
                ["test_drill_waiting"] = "Testing — please disconnect your phone from PC Connect",
                ["test_drill_online"] = "Still online — waiting for disconnect…",
                ["test_drill_countdown"] = "Disconnected — offline countdown {0} / {1}s · Tray icon: red",
                ["test_drill_done"] = "Test complete — command simulated in test mode",
                ["test_drill_conclusion"] = "Monitoring restored (will resume lock guard once phone reconnects). Screen was not locked and PC did not sleep.",
                ["test_drill_interrupted"] = "Main process exited or screen state changed; test ended and monitoring restored.",
                ["test_drill_report_lock"] = "Simulated lock command LockWorkStation (test mode: screen not locked)",
                ["test_drill_report_sleep"] = "Simulated sleep command {0} {1} (test mode: PC did not sleep)",
                ["test_footer"] = "Check results are written to the log (Tray right-click → View log)."
            },
            ["zh-Hans"] = new Dictionary<string, string>
            {
                ["tray_waiting"] = "{0}: ⚪ 等待 {1}",
                ["tray_wait_reconnect"] = "{0}: ⚪ 等待手机重连",
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
                ["tray_log_copy"] = "复制全部",
                ["tray_log_search"] = "搜索...",
                ["tray_log_level_all"] = "全部级别",
                ["tray_log_level_info"] = "INFO 及以上",
                ["tray_log_level_error"] = "仅 ERROR",
                ["tray_log_empty"] = "（暂无日志）",
                ["tray_init"] = "{0}: 初始化中...",
                ["tray_already_running"] = "{0} 已经在运行了。",
                ["tray_test_connection"] = "连接测试…",
                ["test_title"] = "连接测试",
                ["test_idle"] = "未检测 — 点击右侧按钮开始",
                ["test_checking"] = "检测中…",
                ["test_check_now"] = "立即检测",
                ["test_recheck"] = "重新检测",
                ["test_connected"] = "已连接 — 手机在线",
                ["test_disconnected"] = "未连接 — 未检测到手机",
                ["test_detail_process"] = "进程状态：{0}",
                ["test_detail_connection"] = "TCP 连接：{0}",
                ["test_detail_diagnosis"] = "诊断：{0}",
                ["test_proc_running"] = "运行中",
                ["test_proc_not_running"] = "未运行",
                ["test_conn_hit"] = "命中 {0} (PID {1})",
                ["test_conn_established"] = "{0} 条已建立，均未命中",
                ["test_conn_none"] = "无已建立连接",
                ["test_reason_connected"] = "手机会话正常",
                ["test_reason_process"] = "主程序未运行，请先打开 OPPO 互联",
                ["test_reason_no_connection"] = "主程序在运行，但没有已建立的连接",
                ["test_reason_ip_mismatch"] = "有 {0} 条已建立连接，但远程 IP 均不像手机会话（样本: {1}）",
                ["test_reason_check_failed"] = "连接表查询失败，详情见日志",
                ["test_guide_disconnected"] = "未检测到手机。请先连接手机，连接成功后这里会出现断开测试入口。",
                ["test_guide_connected"] = "连接正常，手机在线。要验证一下断开检测吗？",
                ["test_drill_start"] = "开始断开测试",
                ["test_drill_end"] = "结束测试",
                ["test_drill_waiting"] = "测试中 — 请断开手机与电脑的互联连接",
                ["test_drill_online"] = "仍在线 — 等待断开…",
                ["test_drill_countdown"] = "已断开 — 离线倒数 {0} / {1} 秒 · 托盘图标：红色",
                ["test_drill_done"] = "测试完成 — 指令已在测试模式模拟执行",
                ["test_drill_conclusion"] = "监控已自动恢复（待手机重连后继续锁屏守护）。全程屏幕未锁、电脑未睡眠。",
                ["test_drill_interrupted"] = "主程序已退出或屏幕状态变化，测试已结束，监控已恢复。",
                ["test_drill_report_lock"] = "已模拟执行锁屏指令 LockWorkStation（测试模式：屏幕未锁定）",
                ["test_drill_report_sleep"] = "已模拟执行睡眠指令 {0} {1}（测试模式：电脑未睡眠）",
                ["test_footer"] = "检测结果会记入日志，可在托盘右键 → 查看日志 中追溯。"
            },
            ["zh-Hant"] = new Dictionary<string, string>
            {
                ["tray_waiting"] = "{0}: ⚪ 等待 {1}",
                ["tray_wait_reconnect"] = "{0}: ⚪ 等待手機重連",
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
                ["tray_log_copy"] = "複製全部",
                ["tray_log_search"] = "搜尋...",
                ["tray_log_level_all"] = "全部層級",
                ["tray_log_level_info"] = "INFO 及以上",
                ["tray_log_level_error"] = "僅 ERROR",
                ["tray_log_empty"] = "（暫無日誌）",
                ["tray_init"] = "{0}: 初始化中...",
                ["tray_already_running"] = "{0} 已經在執行了。",
                ["tray_test_connection"] = "連線測試…",
                ["test_title"] = "連線測試",
                ["test_idle"] = "未檢測 — 點擊右側按鈕開始",
                ["test_checking"] = "檢測中…",
                ["test_check_now"] = "立即檢測",
                ["test_recheck"] = "重新檢測",
                ["test_connected"] = "已連線 — 手機在線",
                ["test_disconnected"] = "未連線 — 未偵測到手機",
                ["test_detail_process"] = "處理程序狀態：{0}",
                ["test_detail_connection"] = "TCP 連線：{0}",
                ["test_detail_diagnosis"] = "診斷：{0}",
                ["test_proc_running"] = "執行中",
                ["test_proc_not_running"] = "未執行",
                ["test_conn_hit"] = "命中 {0} (PID {1})",
                ["test_conn_established"] = "{0} 條已建立，均未命中",
                ["test_conn_none"] = "無已建立連線",
                ["test_reason_connected"] = "手機會話正常",
                ["test_reason_process"] = "主程式未執行，請先開啟 OPPO 互聯",
                ["test_reason_no_connection"] = "主程式在執行，但沒有已建立的連線",
                ["test_reason_ip_mismatch"] = "有 {0} 條已建立連線，但遠端 IP 均不像手機會話（樣本: {1}）",
                ["test_reason_check_failed"] = "連線表查詢失敗，詳情見日誌",
                ["test_guide_disconnected"] = "未偵測到手機。請先連線手機，連線成功後這裡會出現斷開測試入口。",
                ["test_guide_connected"] = "連線正常，手機在線。要驗證一下斷開偵測嗎？",
                ["test_drill_start"] = "開始斷開測試",
                ["test_drill_end"] = "結束測試",
                ["test_drill_waiting"] = "測試中 — 請斷開手機與電腦的互聯連線",
                ["test_drill_online"] = "仍在線 — 等待斷開…",
                ["test_drill_countdown"] = "已斷開 — 離線倒數 {0} / {1} 秒 · 系統匣圖示：紅色",
                ["test_drill_done"] = "測試完成 — 指令已在測試模式模擬執行",
                ["test_drill_conclusion"] = "監控已自動恢復（待手機重連後繼續鎖屏守護）。全程螢幕未鎖、電腦未睡眠。",
                ["test_drill_interrupted"] = "主程式已結束或螢幕狀態變化，測試已結束，監控已恢復。",
                ["test_drill_report_lock"] = "已模擬執行鎖屏指令 LockWorkStation（測試模式：螢幕未鎖定）",
                ["test_drill_report_sleep"] = "已模擬執行睡眠指令 {0} {1}（測試模式：電腦未睡眠）",
                ["test_footer"] = "檢測結果會記入日誌，可在系統匣右鍵 → 檢視日誌 中追溯。"
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
