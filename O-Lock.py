# -*- coding: utf-8 -*-
"""
BleLock - 手机离线自动锁屏工具
通过检测 OPPO 互联软件的网络连接状态，判断手机是否在线
"""

import os
import sys
import time
import ctypes
import threading
import ipaddress
from pathlib import Path

import psutil
import pystray
from PIL import Image, ImageDraw

# ================== 配置项 ==================
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
        print(f"检测出错: {e}")
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
        return f"BleLock: ⚪ 等待 {APP_PROCESS_NAME}"
    elif is_warmup:
        return f"BleLock: 🟡 正在连接... ({warmup_remaining}秒)"
    elif is_online:
        return "BleLock: 🟢 手机在线"
    else:
        return f"BleLock: 🔴 未检测到 ({offline_count}/{OFFLINE_THRESHOLD})"


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
    print(f"等待 {APP_PROCESS_NAME} 启动...")


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
    print(f"检测到主程序已启动，进入缓冲期，{warmup_time} 秒内需检测到手机")


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
            print("屏幕解锁，重新等待主程序就绪...")
            start_waiting_for_app()
        
        was_locked = currently_locked
        
        # 屏幕锁定期间暂停工作，只简单休眠
        if currently_locked:
            time.sleep(1)
            continue

        # --- 阶段 1: 等待主程序启动 (灰色) ---
        if is_waiting_for_app:
            if is_app_running():
                print(f"检测到 {APP_PROCESS_NAME} 已运行，开始连接缓冲...")
                start_warmup()  # 进入黄色缓冲期
            else:
                # 主程序未启动，保持灰色，不锁屏
                update_icon()
                time.sleep(1)
                continue

        if is_warmup:
            # 实时检测主程序是否还在
            if not is_app_running():
                print(f"{APP_PROCESS_NAME} 已退出，返回等待状态")
                start_waiting_for_app()
                continue
            
            # 检测手机连接
            connected = check_phone_connection()
            
            if connected:
                # 成功连接，且之前是缓冲期：变绿，结束缓冲
                print("缓冲期内检测到手机，连接成功！")
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
                    print("缓冲期结束，未检测到手机，执行锁屏！")
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
            print(f"{APP_PROCESS_NAME} 意外退出！")
            connected = False
        else:
            connected = check_phone_connection()
        
        if connected:
            # 连接正常
            if not is_online:
                print("手机重连成功")
                is_online = True
            offline_count = 0
        else:
            # 连接断开
            if is_online:
                print("手机连接断开")
            is_online = False
            offline_count += 1
            print(f"检测不到手机 ({offline_count}/{OFFLINE_THRESHOLD})")
            
            if offline_count >= OFFLINE_THRESHOLD:
                print("确认手机离线，锁定屏幕！")
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
    return get_startup_folder() / 'BleLock.lnk'


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
            print("已禁用开机自启")
        except Exception as e:
            print(f"禁用开机自启失败: {e}")
    else:
        # 未启用，则创建快捷方式
        try:
            create_shortcut(shortcut_path)
            print("已启用开机自启")
        except Exception as e:
            print(f"启用开机自启失败: {e}")


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
    shortcut.Description = "BleLock - 手机离线自动锁屏"
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
            "开机自启",
            toggle_autostart,
            checked=lambda item: is_autostart_enabled()
        ),
        pystray.Menu.SEPARATOR,
        pystray.MenuItem("退出", quit_app)
    )
    
    # 创建托盘图标
    icon = pystray.Icon(
        "BleLock",
        create_icon_image('warmup'),
        "BleLock: 初始化中...",
        menu
    )
    
    return icon


def main():
    """主函数"""
    global icon
    
    print("BleLock 启动中...")
    print(f"监控进程: {PROCESS_NAME}")
    print(f"检测间隔: {CHECK_INTERVAL} 秒")
    print(f"离线阈值: 连续 {OFFLINE_THRESHOLD} 次")
    print(f"等待时间: {max(30, min(600, WARMUP_TIME))} 秒（超时未连接将锁屏）")
    
    # 创建托盘图标
    icon = setup_tray_icon()
    
    # 启动监控线程
    monitor_thread = threading.Thread(target=monitor_loop, daemon=True)
    monitor_thread.start()
    
    # 运行托盘图标（这会阻塞主线程）
    icon.run()
    
    print("BleLock 已退出")


if __name__ == "__main__":
    main()
