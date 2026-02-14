"""
Windows 蓝牙距离感应锁屏工具 v3.0 (基于 RFCOMM Socket Ping)

原理:
  不依赖 BLE 广播扫描 (bleak)，而是通过尝试建立 RFCOMM 蓝牙 Socket 连接
  来判断设备是否在附近。即使手机处于 "已配对但未连接" 的省电状态，
  只要在蓝牙范围内，Socket 连接尝试会得到响应 (成功或拒绝)。
  如果设备不在范围内，连接会超时。

依赖:
  pip install winsdk pystray Pillow

作者: GitHub Copilot
"""

import asyncio
import ctypes
import os
import socket
import threading
import time

from PIL import Image, ImageDraw
import pystray

# Windows SDK 用于枚举已配对蓝牙设备
from winrt.windows.devices.enumeration import DeviceInformation, DeviceInformationKind

# ==================== 配置参数 ====================
CHECK_INTERVAL = 3.0        # 监控循环间隔 (秒)
SOCKET_TIMEOUT = 2.0        # Socket 连接超时 (秒)，超过则视为"不在附近"
RFCOMM_PORT = 1             # RFCOMM 端口，通常手机默认开放端口 1
FAIL_THRESHOLD = 3          # 连续失败次数阈值，达到后触发锁屏
LOCK_COOLDOWN = 15.0        # 锁屏后冷却时间 (秒)，防止反复锁屏


class BleLockApp:
    """蓝牙距离感应锁屏工具 (RFCOMM Ping 版)"""

    def __init__(self):
        # 目标设备信息
        self.target_address = None  # MAC 地址，格式如 "AA:BB:CC:DD:EE:FF"
        self.target_name = None

        # 状态
        self.running = True
        self.paused = False
        self.fail_count = 0         # 连续失败计数
        self.last_lock_time = 0     # 上次锁屏时间戳
        self.last_status = "unknown"  # 上次状态: "online" / "offline" / "unknown"

        # UI
        self.tray_icon = None
        self.icons = {}

        # Windows API
        self.user32 = ctypes.windll.user32

    # ==================== 图标生成 ====================

    @staticmethod
    def _make_icon(bg_color):
        """生成 64x64 托盘图标"""
        img = Image.new("RGB", (64, 64), bg_color)
        draw = ImageDraw.Draw(img)
        # 画一个简易手机形状
        draw.rounded_rectangle((16, 8, 48, 56), radius=4, fill="white")
        # 屏幕
        draw.rectangle((20, 14, 44, 46), fill=bg_color)
        # Home 键
        draw.ellipse((28, 48, 36, 54), fill=bg_color)
        return img

    def _init_icons(self):
        """预生成各状态图标"""
        self.icons = {
            "init":    self._make_icon((0, 120, 215)),    # 蓝色 - 初始化
            "online":  self._make_icon((34, 177, 76)),    # 绿色 - 设备在线
            "offline": self._make_icon((237, 28, 36)),    # 红色 - 设备离线
            "paused":  self._make_icon((158, 158, 158)),  # 灰色 - 暂停中
        }

    def _get_status_icon(self):
        """根据当前状态返回图标"""
        if self.paused:
            return self.icons["paused"]
        if self.last_status == "online":
            return self.icons["online"]
        if self.last_status == "offline":
            return self.icons["offline"]
        return self.icons["init"]

    # ==================== 系统工具 ====================

    def _is_locked(self):
        """检测工作站是否已锁屏"""
        try:
            h = self.user32.OpenInputDesktop(0, False, 0x0100)
            if h == 0:
                return True
            self.user32.CloseDesktop(h)
            return False
        except Exception:
            return True

    def _lock_screen(self):
        """执行锁屏 (带冷却)"""
        now = time.time()
        if now - self.last_lock_time < LOCK_COOLDOWN:
            return False
        if self._is_locked():
            return False
        print("[锁屏] 触发锁屏！设备连续不可达。")
        self.user32.LockWorkStation()
        self.last_lock_time = now
        return True

    # ==================== 设备枚举 (winsdk) ====================

    async def _get_paired_devices(self):
        """
        使用 winsdk 枚举所有已配对的经典蓝牙设备。
        返回: [(name, mac_address), ...]
        """
        # AQS 查询字符串: 已配对的蓝牙设备
        # 参考: https://learn.microsoft.com/en-us/windows/uwp/devices-sensors/enumerate-devices
        selector = (
            "System.Devices.Aep.ProtocolId:=\"{e0cbf06c-cd8b-4647-bb8a-263b43f0f974}\" "
            "AND System.Devices.Aep.IsPaired:=System.StructuredQueryType.Boolean#True"
        )
        
        # 需要获取的属性
        properties = [
            "System.Devices.Aep.DeviceAddress",  # MAC 地址
        ]

        devices = await DeviceInformation.find_all_async(
            selector,
            properties,
            DeviceInformationKind.ASSOCIATION_ENDPOINT,
        )

        result = []
        for dev in devices:
            name = dev.name or "Unknown"
            # 获取 MAC 地址
            mac = None
            if "System.Devices.Aep.DeviceAddress" in dev.properties:
                mac = dev.properties["System.Devices.Aep.DeviceAddress"]
            
            if mac:
                # 转换为标准格式 AA:BB:CC:DD:EE:FF
                mac = mac.upper().replace("-", ":")
                result.append((name, mac))
        
        return result

    async def select_device(self):
        """列出已配对设备，让用户选择"""
        while True:
            print("\n--- 正在枚举已配对的蓝牙设备... ---")
            devices = await self._get_paired_devices()

            if not devices:
                print("[错误] 未找到任何已配对的蓝牙设备！")
                print("请先在 Windows 设置中配对你的手机。")
                retry = input("输入 r 重新扫描，其他键退出: ")
                if retry.strip().lower() == "r":
                    continue
                return False

            print("\n--- 已配对设备列表 ---")
            for i, (name, mac) in enumerate(devices):
                print(f"  [{i}] {name}  (MAC: {mac})")
            print(f"  [r] 刷新列表")
            print("----------------------")

            while True:
                choice = input("\n请输入设备序号 (或 r 刷新, q 退出): ").strip().lower()

                if choice == "q":
                    return False
                if choice == "r":
                    break

                try:
                    idx = int(choice)
                    if 0 <= idx < len(devices):
                        self.target_name, self.target_address = devices[idx]
                        print(f"[绑定] 已选中: {self.target_name} [{self.target_address}]")
                        return True
                    else:
                        print("序号无效。")
                except ValueError:
                    print("输入无效。")

    # ==================== 核心: RFCOMM Ping ====================

    def _ping_device(self):
        """
        尝试通过 RFCOMM Socket 连接目标设备。
        
        返回:
          True  - 设备在线 (连接成功或被拒绝都算在线)
          False - 设备离线 (超时)
        """
        sock = None
        try:
            # 创建蓝牙 RFCOMM socket
            # AF_BLUETOOTH = 31, BTPROTO_RFCOMM = 3
            sock = socket.socket(
                socket.AF_BLUETOOTH,
                socket.SOCK_STREAM,
                socket.BTPROTO_RFCOMM
            )
            sock.settimeout(SOCKET_TIMEOUT)
            
            # 尝试连接
            # 地址格式: (MAC, port)
            sock.connect((self.target_address, RFCOMM_PORT))
            
            # 如果连接成功，说明设备在线
            return True

        except socket.timeout:
            # 超时 = 设备不在范围内
            return False

        except OSError as e:
            # 常见错误码:
            # 10061 (WSAECONNREFUSED) - 连接被拒绝，说明设备在线但端口未开放
            # 10064 (WSAEHOSTDOWN) - 主机关闭
            # 10065 (WSAEHOSTUNREACH) - 主机不可达
            err_code = e.errno or (e.args[0] if e.args else 0)
            
            if err_code in (10061,):  # Connection refused
                # 被拒绝 = 设备在线，只是端口没开
                return True
            else:
                # 其他错误视为离线
                return False

        except Exception:
            return False

        finally:
            if sock:
                try:
                    sock.close()
                except Exception:
                    pass

    # ==================== 监控循环 ====================

    def _monitor_loop(self):
        """核心监控循环 (同步版本，在后台线程运行)"""
        print(f"\n--- 开始监控 (超时: {SOCKET_TIMEOUT}s, 阈值: {FAIL_THRESHOLD}次) ---")
        print("提示: 程序已最小化到系统托盘，右键图标可操作。\n")

        while self.running:
            loop_start = time.time()

            if self.paused:
                self._update_tray()
                time.sleep(1)
                continue

            # --- Ping ---
            is_online = self._ping_device()

            if is_online:
                self.fail_count = 0
                self.last_status = "online"
                print(f"[在线] {self.target_name} - 连续失败: 0")
            else:
                self.fail_count += 1
                print(f"[离线] {self.target_name} - 连续失败: {self.fail_count}/{FAIL_THRESHOLD}")

                if self.fail_count >= FAIL_THRESHOLD:
                    self.last_status = "offline"
                    self._lock_screen()
                    # 锁屏后重置计数，避免立即再次触发
                    self.fail_count = 0

            # --- 更新 UI ---
            self._update_tray()

            # --- 控制循环间隔 ---
            elapsed = time.time() - loop_start
            if elapsed < CHECK_INTERVAL:
                time.sleep(CHECK_INTERVAL - elapsed)

    # ==================== 托盘 UI ====================

    def _update_tray(self):
        """更新托盘图标与提示"""
        if not self.tray_icon:
            return
        
        self.tray_icon.icon = self._get_status_icon()

        if self.paused:
            self.tray_icon.title = "BleLock [已暂停]"
        else:
            status_text = "在线" if self.last_status == "online" else "离线"
            self.tray_icon.title = (
                f"BleLock: {self.target_name}\n"
                f"状态: {status_text}\n"
                f"连续失败: {self.fail_count}/{FAIL_THRESHOLD}"
            )

    def _on_toggle_pause(self, icon, item):
        """暂停/继续"""
        self.paused = not self.paused
        print(f"[操作] {'暂停' if self.paused else '继续'}监控")
        self._update_tray()

    def _on_rescan(self, icon, item):
        """重新选择设备"""
        print("[操作] 用户请求重新选择设备，程序即将退出...")
        self._quit()

    def _on_exit(self, icon, item):
        """退出"""
        self._quit()

    def _quit(self):
        """安全退出"""
        print("[退出] 程序正在关闭...")
        self.running = False
        if self.tray_icon:
            self.tray_icon.stop()
        os._exit(0)

    # ==================== 主入口 ====================

    def run(self):
        """启动应用"""
        print("=" * 50)
        print("  Windows 蓝牙距离感应锁屏工具 v3.0")
        print("  (基于 RFCOMM Socket Ping)")
        print("=" * 50)

        # 初始化图标
        self._init_icons()

        # 1. 选择设备
        if not asyncio.run(self.select_device()):
            input("按回车键退出...")
            return

        # 2. 构建托盘
        menu = pystray.Menu(
            pystray.MenuItem("暂停/继续", self._on_toggle_pause),
            pystray.MenuItem("重新选择设备", self._on_rescan),
            pystray.MenuItem("退出", self._on_exit),
        )
        self.tray_icon = pystray.Icon(
            "BleLock", self.icons["init"], "BleLock 启动中...", menu
        )

        # 3. 后台线程启动监控
        t = threading.Thread(target=self._monitor_loop, daemon=True)
        t.start()

        # 4. 主线程运行托盘
        try:
            self.tray_icon.run()
        except KeyboardInterrupt:
            self._quit()


if __name__ == "__main__":
    BleLockApp().run()