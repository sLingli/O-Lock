# -*- coding: utf-8 -*-

import sys
import traceback

def crash_log(exc_type, exc_value, exc_tb):
    with open("crash.log", "w", encoding="utf-8") as f:
        f.write("".join(traceback.format_exception(exc_type, exc_value, exc_tb)))

sys.excepthook = crash_log

"""
OLock - Phone offline auto-lock tool
Detects OPPO Connect network activity to determine phone presence.

OLock - 手机离线自动锁屏工具
通过检测 OPPO 互联软件的网络连接状态，判断手机是否在线
"""

import os
import sys
import time
import ctypes
import threading
import ipaddress
import locale
from pathlib import Path

import psutil
import pystray
from PIL import Image, ImageDraw

# ================== 配置项 ==================
APP_NAME = "OLock"
PROCESS_NAME = "pantaChannelService.exe"  # 监听网络连接的进程（负责通信）
APP_PROCESS_NAME = "O+Connect.exe"  # 主程序进程（检测是否运行）
CHECK_INTERVAL = 3  # 检测间隔（秒）
OFFLINE_THRESHOLD = 3  # 连续多少次离线才锁屏
WARMUP_TIME = 60  # 启动/解锁后等待手机连接的最大时间（秒），范围 30-600，超时未连接则锁屏
# ============================================

# 全局变量
offline_count = 0  # 连续离线计数
is_online = False  # 当前在线状态
running = True  # 程序运行标志
icon = None  # 托盘图标对象
is_warmup = False  # 是否处于缓冲期（初始为 False，等待主程序启动后再激活）
is_waiting_for_app = True  # 是否处于等待主程序启动状态（初始为 True）
warmup_remaining = 0  # 缓冲期剩余时间（秒）
was_locked = False  # 上一次检测时屏幕是否锁定


def get_ui_language():
    """Return 'en', 'zh-Hans', or 'zh-Hant' based on system UI language."""
    hans_lang_ids = {0x0804, 0x1004}
    hant_lang_ids = {0x0404, 0x0C04, 0x1404}

    try:
        lang_id = ctypes.windll.kernel32.GetUserDefaultUILanguage()
        if lang_id in hans_lang_ids:
            return "zh-Hans"
        if lang_id in hant_lang_ids:
            return "zh-Hant"
    except Exception:
        pass

    try:
        lang = locale.getdefaultlocale()[0] or ""
        lang_lower = lang.lower()
        if lang_lower.startswith("zh"):
            if any(tag in lang_lower for tag in ("tw", "hk", "mo", "hant")):
                return "zh-Hant"
            return "zh-Hans"
    except Exception:
        pass

    return "en"


TEXTS = {
    "en": {
        "check_error": "Error while checking: {error}",
        "tray_waiting": "{app_name}: ⚪ Waiting for {app_process}",
        "tray_warmup": "{app_name}: 🟡 Connecting... ({seconds}s)",
        "tray_online": "{app_name}: 🟢 Phone online",
        "tray_offline": "{app_name}: 🔴 Not detected ({count}/{threshold})",
        "waiting_app_start": "Waiting for {app_process} to start...",
        "screen_unlock": "Screen unlocked, waiting for app...",
        "app_started_warmup": "Detected {app_process} running, starting warmup...",
        "app_started_warmup_notice": (
            "Detected main app started, entering warmup; "
            "phone must connect within {seconds} seconds."
        ),
        "app_exited_back_waiting": "{app_process} exited, back to waiting.",
        "warmup_connected": "Phone detected during warmup, connected.",
        "warmup_timeout_lock": "Warmup ended, phone not detected; locking screen.",
        "app_exited_unexpected": "{app_process} exited unexpectedly!",
        "phone_reconnected": "Phone reconnected.",
        "phone_disconnected": "Phone disconnected.",
        "phone_not_detected": "Phone not detected ({count}/{threshold}).",
        "confirm_offline_lock": "Confirmed phone offline, locking screen.",
        "autostart_enabled": "Autostart enabled.",
        "autostart_disabled": "Autostart disabled.",
        "autostart_disable_failed": "Failed to disable autostart: {error}",
        "autostart_enable_failed": "Failed to enable autostart: {error}",
        "tray_autostart": "Start with Windows",
        "tray_quit": "Quit",
        "tray_init": "{app_name}: Initializing...",
        "startup": "{app_name} starting...",
        "monitor_process": "Monitor process: {process}",
        "check_interval": "Check interval: {seconds} s",
        "offline_threshold": "Offline threshold: {count} consecutive",
        "warmup_time": "Warmup time: {seconds} s (lock if not connected)",
        "exited": "{app_name} exited",
        "shortcut_desc": "OLock - Phone offline auto-lock",
    },
    "zh-Hans": {
        "check_error": "检测出错: {error}",
        "tray_waiting": "{app_name}: ⚪ 等待 {app_process}",
        "tray_warmup": "{app_name}: 🟡 正在连接... ({seconds}秒)",
        "tray_online": "{app_name}: 🟢 手机在线",
        "tray_offline": "{app_name}: 🔴 未检测到 ({count}/{threshold})",
        "waiting_app_start": "等待 {app_process} 启动...",
        "screen_unlock": "屏幕解锁，重新等待主程序就绪...",
        "app_started_warmup": "检测到 {app_process} 已运行，开始连接缓冲...",
        "app_started_warmup_notice": "检测到主程序已启动，进入缓冲期，{seconds} 秒内需检测到手机",
        "app_exited_back_waiting": "{app_process} 已退出，返回等待状态",
        "warmup_connected": "缓冲期内检测到手机，连接成功！",
        "warmup_timeout_lock": "缓冲期结束，未检测到手机，执行锁屏！",
        "app_exited_unexpected": "{app_process} 意外退出！",
        "phone_reconnected": "手机重连成功",
        "phone_disconnected": "手机连接断开",
        "phone_not_detected": "检测不到手机 ({count}/{threshold})",
        "confirm_offline_lock": "确认手机离线，锁定屏幕！",
        "autostart_enabled": "已启用开机自启",
        "autostart_disabled": "已禁用开机自启",
        "autostart_disable_failed": "禁用开机自启失败: {error}",
        "autostart_enable_failed": "启用开机自启失败: {error}",
        "tray_autostart": "开机自启",
        "tray_quit": "退出",
        "tray_init": "{app_name}: 初始化中...",
        "startup": "{app_name} 启动中...",
        "monitor_process": "监控进程: {process}",
        "check_interval": "检测间隔: {seconds} 秒",
        "offline_threshold": "离线阈值: 连续 {count} 次",
        "warmup_time": "等待时间: {seconds} 秒（超时未连接将锁屏）",
        "exited": "{app_name} 已退出",
        "shortcut_desc": "OLock - 手机离线自动锁屏",
    },
    "zh-Hant": {
        "check_error": "偵測出錯: {error}",
        "tray_waiting": "{app_name}: ⚪ 等待 {app_process}",
        "tray_warmup": "{app_name}: 🟡 正在連線... ({seconds}秒)",
        "tray_online": "{app_name}: 🟢 手機在線",
        "tray_offline": "{app_name}: 🔴 未偵測到 ({count}/{threshold})",
        "waiting_app_start": "等待 {app_process} 啟動...",
        "screen_unlock": "螢幕解鎖，重新等待主程式就緒...",
        "app_started_warmup": "偵測到 {app_process} 已執行，開始連線緩衝...",
        "app_started_warmup_notice": "偵測到主程式已啟動，進入緩衝期，{seconds} 秒內需偵測到手機",
        "app_exited_back_waiting": "{app_process} 已退出，返回等待狀態",
        "warmup_connected": "緩衝期內偵測到手機，連線成功！",
        "warmup_timeout_lock": "緩衝期結束，未偵測到手機，執行鎖屏！",
        "app_exited_unexpected": "{app_process} 意外退出！",
        "phone_reconnected": "手機重新連線成功",
        "phone_disconnected": "手機連線中斷",
        "phone_not_detected": "偵測不到手機 ({count}/{threshold})",
        "confirm_offline_lock": "確認手機離線，鎖定螢幕！",
        "autostart_enabled": "已啟用開機自啟",
        "autostart_disabled": "已停用開機自啟",
        "autostart_disable_failed": "停用開機自啟失敗: {error}",
        "autostart_enable_failed": "啟用開機自啟失敗: {error}",
        "tray_autostart": "開機自啟",
        "tray_quit": "退出",
        "tray_init": "{app_name}: 初始化中...",
        "startup": "{app_name} 啟動中...",
        "monitor_process": "監控行程: {process}",
        "check_interval": "檢測間隔: {seconds} 秒",
        "offline_threshold": "離線閾值: 連續 {count} 次",
        "warmup_time": "等待時間: {seconds} 秒（逾時未連線將鎖屏）",
        "exited": "{app_name} 已退出",
        "shortcut_desc": "OLock - 手機離線自動鎖屏",
    },
}

LANG = get_ui_language()


def tr(key, **kwargs):
    text = TEXTS.get(LANG, TEXTS["en"]).get(key, key)
    return text.format(**kwargs)


def is_private_ip(ip_str):
    """
    判断 IP 是否是局域网地址
    局域网范围：
    - 10.0.0.0/8
    - 172.16.0.0/12 (172.16.x.x ~ 172.31.x.x)
    - 192.168.0.0/16
    """
    try:
        ip = ipaddress.ip_address(ip_str)
        return ip.is_private and not ip.is_loopback
    except ValueError:
        return False


def check_phone_connection():
    """
    检测手机是否连接
    遍历 pantaChannelService.exe 进程的所有网络连接
    如果存在 ESTABLISHED 状态且远程 IP 是局域网，则判定手机在线
    """
    try:
        for proc in psutil.process_iter(['name', 'pid']):
            if proc.info['name'] and proc.info['name'].lower() == PROCESS_NAME.lower():
                try:
                    connections = proc.connections(kind='tcp')
                    for conn in connections:
                        # 检查是否是 ESTABLISHED 状态
                        if conn.status == psutil.CONN_ESTABLISHED:
                            # 检查远程地址是否存在且是局域网 IP
                            if conn.raddr and len(conn.raddr) >= 1:
                                remote_ip = conn.raddr[0]
                                if is_private_ip(remote_ip):
                                    return True
                except (psutil.NoSuchProcess, psutil.AccessDenied):
                    # 进程已退出或无权限访问
                    continue
    except Exception as e:
        print(tr("check_error", error=e))
    return False


def lock_screen():
    """调用 Windows API 锁定屏幕"""
    ctypes.windll.user32.LockWorkStation()


def is_screen_locked():
    """
    检测屏幕是否处于锁定状态
    通过尝试打开输入桌面来判断
    """
    user32 = ctypes.windll.user32
    # 尝试打开输入桌面
    hDesktop = user32.OpenInputDesktop(0, False, 0x0001)  # DESKTOP_READOBJECTS
    if hDesktop:
        user32.CloseDesktop(hDesktop)
        return False  # 能打开说明未锁定
    return True  # 无法打开说明已锁定


def create_icon_image(state):
    """
    创建托盘图标图像
    state: 
      'online' = 绿色
      'offline' = 红色
      'warmup' = 黄色 
      'waiting' = 灰色
    """
    # 创建 64x64 的图像
    size = 64
    image = Image.new('RGBA', (size, size), (0, 0, 0, 0))
    draw = ImageDraw.Draw(image)
    
    # 根据状态选择颜色
    if state == 'online':
        color = (0, 200, 0, 255)    # 绿色
    elif state == 'warmup':
        color = (255, 200, 0, 255)  # 黄色
    elif state == 'waiting':
        color = (128, 128, 128, 255) # 灰色
    else:
        color = (200, 0, 0, 255)    # 红色
    
    # 画一个填充的圆
    margin = 4
    draw.ellipse([margin, margin, size - margin, size - margin], fill=color)
    
    return image


def get_status_text():
    """获取托盘图标的悬停提示文本"""
    if is_waiting_for_app:
        return tr("tray_waiting", app_name=APP_NAME, app_process=APP_PROCESS_NAME)
    elif is_warmup:
        return tr("tray_warmup", app_name=APP_NAME, seconds=warmup_remaining)
    elif is_online:
        return tr("tray_online", app_name=APP_NAME)
    else:
        return tr(
            "tray_offline",
            app_name=APP_NAME,
            count=offline_count,
            threshold=OFFLINE_THRESHOLD,
        )


def get_icon_state():
    """获取当前图标状态"""
    if is_waiting_for_app:
        return 'waiting'
    elif is_warmup:
        return 'warmup'
    elif is_online:
        return 'online'
    else:
        return 'offline'


def update_icon():
    """更新托盘图标和提示文本"""
    global icon
    if icon:
        icon.icon = create_icon_image(get_icon_state())
        icon.title = get_status_text()


def is_app_running():
    """检测 O+Connect.exe 是否在运行"""
    for proc in psutil.process_iter(['name']):
        if proc.info['name'] and proc.info['name'].lower() == APP_PROCESS_NAME.lower():
            return True
    return False


def start_waiting_for_app():
    """进入等待主程序启动状态"""
    global is_waiting_for_app, is_warmup, is_online, offline_count
    is_waiting_for_app = True
    is_warmup = False
    is_online = False
    offline_count = 0
    print(tr("waiting_app_start", app_process=APP_PROCESS_NAME))


def start_warmup():
    """开始缓冲期"""
    global is_warmup, is_waiting_for_app, warmup_remaining, offline_count, is_online
    # 确保配置在有效范围内
    warmup_time = max(30, min(600, WARMUP_TIME))
    is_warmup = True
    is_waiting_for_app = False  # 结束等待程序状态
    warmup_remaining = warmup_time
    offline_count = 0
    is_online = False  # 缓冲期开始时默认未连接
    print(tr("app_started_warmup_notice", seconds=warmup_time))


def monitor_loop():
    """
    主监控循环
    逻辑：
    1. 启动/解锁时 -> 进入【等待程序启动】状态（灰色）
    2. 检测到程序启动 -> 进入【缓冲期】（黄色）
    3. 缓冲期内检测到手机 -> 进入【在线】状态（绿色）
    4. 缓冲期超时未检测到 -> 进入【离线】状态（红色并锁屏）
    5. 在线状态 -> 正常心跳检测
    """
    global offline_count, is_online, running, is_warmup, is_waiting_for_app, warmup_remaining, was_locked
    
    # 程序初始进入等待状态
    start_waiting_for_app()
    
    while running:
        # 检测屏幕锁定状态变化
        currently_locked = is_screen_locked()
        
        # 状态变迁：从锁定 -> 解锁
        if was_locked and not currently_locked:
            print(tr("screen_unlock"))
            start_waiting_for_app()
        
        was_locked = currently_locked
        
        # 屏幕锁定期间暂停工作，只简单休眠
        if currently_locked:
            time.sleep(1)
            continue

        # --- 阶段 1: 等待主程序启动 (灰色) ---
        if is_waiting_for_app:
            if is_app_running():
                print(tr("app_started_warmup", app_process=APP_PROCESS_NAME))
                start_warmup()  # 进入黄色缓冲期
            else:
                # 主程序未启动，保持灰色，不锁屏
                update_icon()
                time.sleep(1)
                continue

        if is_warmup:
            # 实时检测主程序是否还在
            if not is_app_running():
                print(tr("app_exited_back_waiting", app_process=APP_PROCESS_NAME))
                start_waiting_for_app()
                continue
            
            # 检测手机连接
            connected = check_phone_connection()
            
            if connected:
                # 成功连接，且之前是缓冲期：变绿，结束缓冲
                print(tr("warmup_connected"))
                is_warmup = False
                warmup_remaining = 0
                offline_count = 0
                # 这里不需要显式设置 is_online = True，因为缓冲结束会自然流转到下面的正常监控逻辑，
                # 但为了立即更新图标状态，我们还是设置一下
                is_online = True
            else:
                # 未连接：倒计时
                warmup_remaining -= 1 # 每次循环大约1秒
                
                # 更新倒计时显示
                update_icon()
                
                if warmup_remaining <= 0:
                    # 超时未连接：锁屏
                    print(tr("warmup_timeout_lock"))
                    lock_screen()
                    # 锁屏指令发出后，立即进入下一次循环
                    # 此时 was_locked 会变为 True，循环将暂停检测
                    # 当用户再次解锁时，was_locked 变为 False，触发 start_waiting_for_app
                    # 从而完美闭环回到灰色等待状态
                    time.sleep(1) 
                    continue
                else:
                    # 还在缓冲期内，继续等待
                    time.sleep(1)
                    continue
        
        # --- 阶段 3: 正常监控 (绿色/红色) ---
        
        # 如果主程序意外退出，视为断连，变回等待状态？
        # 用户需求里没细说，但"检测O+Connect.exe"似乎是全局前提
        # 这里为了安全：主程序若退出，视为离线，触发锁屏，锁屏后自然会因屏幕解锁而重置为等待状态
        
        app_running = is_app_running()
        if not app_running:
            print(tr("app_exited_unexpected", app_process=APP_PROCESS_NAME))
            connected = False
        else:
            connected = check_phone_connection()
        
        if connected:
            # 连接正常
            if not is_online:
                print(tr("phone_reconnected"))
                is_online = True
            offline_count = 0
        else:
            # 连接断开
            if is_online:
                print(tr("phone_disconnected"))
            is_online = False
            offline_count += 1
            print(
                tr(
                    "phone_not_detected",
                    count=offline_count,
                    threshold=OFFLINE_THRESHOLD,
                )
            )
            
            if offline_count >= OFFLINE_THRESHOLD:
                print(tr("confirm_offline_lock"))
                lock_screen()
                offline_count = 0
                # 锁屏后程序继续运行，下一次循环发现屏幕被锁定，会暂停检测
                # 当用户解锁后，会触发 "was_locked and not currently_locked"，从而进入 start_waiting_for_app
        
        update_icon()
        
        # 等待下次检测
        for _ in range(CHECK_INTERVAL * 10):
            if not running: break
            time.sleep(0.1)


def get_startup_folder():
    """获取 Windows 启动目录路径"""
    return Path(os.environ['APPDATA']) / 'Microsoft' / 'Windows' / 'Start Menu' / 'Programs' / 'Startup'


def get_shortcut_path():
    """获取快捷方式的完整路径"""
    return get_startup_folder() / 'OLock.lnk'


def is_autostart_enabled():
    """检查是否已启用开机自启"""
    return get_shortcut_path().exists()


def toggle_autostart(icon_obj, item):
    """切换开机自启状态"""
    shortcut_path = get_shortcut_path()
    
    if is_autostart_enabled():
        # 已启用，则删除快捷方式
        try:
            shortcut_path.unlink()
            print(tr("autostart_disabled"))
        except Exception as e:
            print(tr("autostart_disable_failed", error=e))
    else:
        # 未启用，则创建快捷方式
        try:
            create_shortcut(shortcut_path)
            print(tr("autostart_enabled"))
        except Exception as e:
            print(tr("autostart_enable_failed", error=e))


def create_shortcut(shortcut_path):
    """
    在指定路径创建本程序的快捷方式
    使用 Windows 的 COM 接口
    """
    import winreg
    from win32com.client import Dispatch
    
    shell = Dispatch('WScript.Shell')
    shortcut = shell.CreateShortCut(str(shortcut_path))
    
    # 获取当前脚本的路径
    if getattr(sys, 'frozen', False):
        # 如果是打包后的 exe
        target = sys.executable
    else:
        # 如果是 Python 脚本，使用 pythonw.exe 运行（无控制台窗口）
        target = sys.executable.replace('python.exe', 'pythonw.exe')
        shortcut.Arguments = f'"{os.path.abspath(__file__)}"'
    
    shortcut.Targetpath = target
    shortcut.WorkingDirectory = os.path.dirname(os.path.abspath(__file__))
    shortcut.Description = tr("shortcut_desc")
    shortcut.save()


def quit_app(icon_obj, item):
    """退出程序"""
    global running, icon
    running = False
    if icon:
        icon.stop()


def setup_tray_icon():
    """设置系统托盘图标"""
    global icon
    
    # 创建菜单
    menu = pystray.Menu(
        pystray.MenuItem(
            tr("tray_autostart"),
            toggle_autostart,
            checked=lambda item: is_autostart_enabled()
        ),
        pystray.Menu.SEPARATOR,
        pystray.MenuItem(tr("tray_quit"), quit_app)
    )
    
    # 创建托盘图标
    icon = pystray.Icon(
        APP_NAME,
        create_icon_image('warmup'),
        tr("tray_init", app_name=APP_NAME),
        menu
    )
    
    return icon


def main():
    """主函数"""
    global icon
    
    print(tr("startup", app_name=APP_NAME))
    print(tr("monitor_process", process=PROCESS_NAME))
    print(tr("check_interval", seconds=CHECK_INTERVAL))
    print(tr("offline_threshold", count=OFFLINE_THRESHOLD))
    print(tr("warmup_time", seconds=max(30, min(600, WARMUP_TIME))))
    
    # 创建托盘图标
    icon = setup_tray_icon()
    
    # 启动监控线程
    monitor_thread = threading.Thread(target=monitor_loop, daemon=True)
    monitor_thread.start()
    
    # 运行托盘图标（这会阻塞主线程）
    icon.run()
    
    print(tr("exited", app_name=APP_NAME))


if __name__ == "__main__":
    main()
