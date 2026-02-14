import asyncio
import ctypes
import os
import sys
import threading
import time
from collections import deque

from bleak import BleakScanner
from PIL import Image, ImageDraw
import pystray

# ==================== 配置参数 ====================
SCAN_DURATION = 5.0       # 初次扫描时间 (秒)
MONITOR_SCAN_TIME = 2.0   # 监控时每次扫描时间 (秒)，越短反应越快
CHECK_INTERVAL = 3.0      # 监控循环最小间隔 (秒)，包括扫描时间在内
RSSI_THRESHOLD = -75      # 锁屏阈值 (dBm)，5 次平均值低于此值触发锁屏
WINDOW_SIZE = 5           # 信号平滑窗口大小 (防抖动)
LOCK_COOLDOWN = 15.0      # 锁屏后冷却时间 (秒)，防止反复锁屏
MISSING_RSSI = -100       # 扫描不到设备时填充的 RSSI 值


class BleLockApp:
    """蓝牙距离感应锁屏工具 —— 主应用类"""

    def __init__(self):
        # 目标设备信息
        self.target_address = None
        self.target_name = None

        # 状态标志
        self.running = True       # 程序总开关
        self.paused = False       # 暂停监控
        self.last_lock_time = 0   # 上次锁屏时间戳 (用于冷却)

        # 信号历史 (deque 自动丢弃旧数据，比 list + pop 更高效)
        self.rssi_history = deque(maxlen=WINDOW_SIZE)

        # UI
        self.tray_icon = None
        self.current_rssi = MISSING_RSSI
        self.avg_rssi = MISSING_RSSI

        # Windows API
        self.user32 = ctypes.windll.user32

    # ==================== 图标生成 ====================

    @staticmethod
    def _make_icon(bg_color, dot_color=None):
        """
        生成 64x64 托盘图标。
        bg_color: 背景色 (代表状态)
        dot_color: 中心圆点颜色，默认跟随背景色
        """
        img = Image.new("RGB", (64, 64), bg_color)
        draw = ImageDraw.Draw(img)
        draw.rounded_rectangle((12, 18, 52, 54), radius=6, fill="white")
        draw.ellipse((26, 30, 38, 42), fill=dot_color or bg_color)
        return img

    # 预生成各状态图标 (避免每次循环重复创建)
    @staticmethod
    def _icons():
        return {
            "init":   BleLockApp._make_icon((0, 120, 215)),   # 蓝色 - 初始化
            "safe":   BleLockApp._make_icon((34, 177, 76)),    # 绿色 - 安全范围
            "warn":   BleLockApp._make_icon((255, 185, 0)),    # 黄色 - 接近阈值
            "danger": BleLockApp._make_icon((237, 28, 36)),    # 红色 - 已触发锁屏
            "paused": BleLockApp._make_icon((158, 158, 158)),  # 灰色 - 暂停中
        }

    def _get_status_icon(self):
        """根据当前状态返回对应的图标"""
        if self.paused:
            return self.icons["paused"]
        if self.avg_rssi < RSSI_THRESHOLD:
            return self.icons["danger"]
        if self.avg_rssi < RSSI_THRESHOLD + 10:
            return self.icons["warn"]
        return self.icons["safe"]

    # ==================== 系统工具 ====================

    def _is_locked(self):
        """检测工作站是否已处于锁屏状态"""
        try:
            h = self.user32.OpenInputDesktop(0, False, 0x0100)
            if h == 0:
                return True
            self.user32.CloseDesktop(h)
            return False
        except Exception:
            return True

    def _lock_screen(self):
        """执行锁屏 (带冷却与状态检测)"""
        now = time.time()
        # 冷却期内不重复锁屏
        if now - self.last_lock_time < LOCK_COOLDOWN:
            return
        # 已锁屏则跳过
        if self._is_locked():
            return
        print("[锁屏] 触发锁屏！信号过弱或设备丢失。")
        self.user32.LockWorkStation()
        self.last_lock_time = now

    # ==================== 蓝牙扫描 ====================

    async def _scan_devices(self):
        """扫描蓝牙，返回 [(device, rssi, name), ...] 列表，按信号强度降序"""
        print(f"\n--- 正在扫描蓝牙设备 (约 {SCAN_DURATION} 秒)... ---")
        devices_dict = await BleakScanner.discover(
            timeout=SCAN_DURATION, return_adv=True
        )
        result = []
        for dev, adv in devices_dict.values():
            name = dev.name or "Unknown"
            result.append((dev, adv.rssi, name))
        result.sort(key=lambda x: x[1], reverse=True)
        return result

    async def select_device(self):
        """扫描并让用户选择目标设备 (支持刷新重扫)"""
        while True:
            device_list = await self._scan_devices()

            if not device_list:
                print("[错误] 未扫描到任何设备，请确保手机蓝牙已开启！")
                retry = input("输入 r 重新扫描，其他键退出: ")
                if retry.strip().lower() == "r":
                    continue
                return False

            print("\n--- 扫描结果 ---")
            for i, (dev, rssi, name) in enumerate(device_list):
                print(f"  [{i}] {name}  (MAC: {dev.address})  信号: {rssi} dBm")
            print(f"  [r] 刷新扫描")
            print("----------------")

            while True:
                choice = input("\n请输入设备序号 (或 r 刷新, q 退出): ").strip().lower()

                if choice == "q":
                    return False
                if choice == "r":
                    break  # 跳出内层循环，重新扫描

                try:
                    idx = int(choice)
                    if 0 <= idx < len(device_list):
                        sel = device_list[idx]
                        self.target_address = sel[0].address
                        self.target_name = sel[2]
                        # 用当前信号值填满历史队列，防止刚启动就误触锁屏
                        for _ in range(WINDOW_SIZE):
                            self.rssi_history.append(sel[1])
                        self.avg_rssi = sel[1]
                        print(f"[绑定] 已选中: {self.target_name} [{self.target_address}]")
                        return True
                    else:
                        print("序号无效，请重新输入。")
                except ValueError:
                    print("输入无效，请输入数字、r 或 q。")

    # ==================== 监控循环 ====================

    async def monitor_loop(self):
        """核心监控循环"""
        print(f"\n--- 开始监控 (阈值: {RSSI_THRESHOLD} dBm, 窗口: {WINDOW_SIZE}) ---")
        print("提示: 程序已最小化到系统托盘，右键图标可操作。\n")

        while self.running:
            loop_start = time.time()

            # 暂停状态
            if self.paused:
                self._update_tray()
                await asyncio.sleep(1)
                continue

            try:
                # --- 扫描 ---
                devices_dict = await BleakScanner.discover(
                    timeout=MONITOR_SCAN_TIME, return_adv=True
                )

                self.current_rssi = MISSING_RSSI
                found = False
                for dev, adv in devices_dict.values():
                    if dev.address == self.target_address:
                        self.current_rssi = adv.rssi
                        found = True
                        break

                # --- 更新历史 ---
                self.rssi_history.append(self.current_rssi)
                self.avg_rssi = sum(self.rssi_history) / len(self.rssi_history)

                # --- 控制台日志 ---
                tag = "" if found else " [未检测到]"
                print(
                    f"信号: {self.current_rssi:>4d} dBm | "
                    f"平均: {self.avg_rssi:>6.1f} dBm{tag}"
                )

                # --- 更新图标 ---
                self._update_tray()

                # --- 锁屏判断 ---
                if len(self.rssi_history) >= WINDOW_SIZE and self.avg_rssi < RSSI_THRESHOLD:
                    self._lock_screen()

            except Exception as e:
                print(f"[错误] 监控异常: {e}")

            # 控制循环最小间隔
            elapsed = time.time() - loop_start
            if elapsed < CHECK_INTERVAL:
                await asyncio.sleep(CHECK_INTERVAL - elapsed)

    # ==================== 托盘 UI ====================

    def _update_tray(self):
        """更新托盘图标与悬浮提示"""
        if not self.tray_icon:
            return
        self.tray_icon.icon = self._get_status_icon()

        if self.paused:
            self.tray_icon.title = "BleLock [已暂停]"
        elif self.target_name:
            self.tray_icon.title = (
                f"BleLock: {self.target_name}\n"
                f"实时: {self.current_rssi} dBm | "
                f"均值: {int(self.avg_rssi)} dBm"
            )
        else:
            self.tray_icon.title = "BleLock [未绑定设备]"

    def _on_toggle_pause(self, icon, item):
        """暂停/继续 回调"""
        self.paused = not self.paused
        print(f"[操作] {'暂停' if self.paused else '继续'}监控")
        self._update_tray()

    def _on_rescan(self, icon, item):
        """重新扫描设备 (需重启程序)"""
        print("[操作] 用户请求重新选择设备，程序即将重启...")
        if self.tray_icon:
            self.tray_icon.notify("请在控制台重新选择设备", "重新扫描")
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

    # ==================== 线程入口 ====================

    def _run_async(self):
        """后台线程: 运行 asyncio 事件循环"""
        loop = asyncio.new_event_loop()
        asyncio.set_event_loop(loop)
        loop.run_until_complete(self.monitor_loop())
        loop.close()

    # ==================== 主入口 ====================

    def run(self):
        """启动应用"""
        print("========================================")
        print("   Windows 蓝牙距离感应锁屏工具 v2.0")
        print("========================================")

        # 预生成图标
        self.icons = self._icons()

        # 1. 扫描 & 选择设备 (阻塞，在主线程完成)
        if not asyncio.run(self.select_device()):
            input("按回车键退出...")
            return

        # 2. 构建托盘菜单
        menu = pystray.Menu(
            pystray.MenuItem("暂停/继续", self._on_toggle_pause),
            pystray.MenuItem("重新选择设备", self._on_rescan),
            pystray.MenuItem("退出", self._on_exit),
        )
        self.tray_icon = pystray.Icon(
            "BleLock", self.icons["init"], "BleLock 启动中...", menu
        )

        # 3. 后台线程启动监控循环
        t = threading.Thread(target=self._run_async, daemon=True)
        t.start()

        # 4. 主线程运行托盘 (阻塞)
        try:
            self.tray_icon.run()
        except KeyboardInterrupt:
            self._quit()


if __name__ == "__main__":
    BleLockApp().run()