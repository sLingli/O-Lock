import asyncio
import threading
import time
import sys
import os
import ctypes
from collections import deque
from statistics import mean

# 导入第三方库
# pip install bleak pystray Pillow pywin32
from bleak import BleakScanner
from PIL import Image, ImageDraw
import pystray
import win32com.client  # 用于创建 Windows 快捷方式

# ================= 配置参数 (可根据实际情况调整) =================
SCAN_DURATION = 2.0       # 每次扫描持续时间 (秒)。时间越长越精确，但反应越慢。
CHECK_INTERVAL = 3.0      # 监控循环总间隔 (秒)。确保 >= SCAN_DURATION。
RSSI_HISTORY_LEN = 5      # 信号历史记录长度 (防抖动队列)。
LOCK_THRESHOLD = -80      # 锁屏阈值 (dBm)。低于此值触发锁屏。
MISSING_DEVICE_RSSI = -100 # 当扫描不到设备时使用的默认 RSSI 值 (视为极远)。
STARTUP_NAME = "WindowsBleLock" # 开机启动项的名称

class BleLockApp:
    def __init__(self):
        """初始化应用状态"""
        self.target_address = None
        self.target_name = None
        
        # 状态控制标志
        self.running = True    # 控制主监控循环是否运行
        self.paused = False    # 控制是否暂停监控业务
        
        # 信号历史队列 (用于计算移动平均值，防止信号波动导致误触)
        self.rssi_history = deque(maxlen=RSSI_HISTORY_LEN)
        
        # UI 相关
        self.icon = None       # 托盘图标对象
        self.last_rssi_text = "等待数据..."
        
        # 加载 Windows 用户 API
        self.user32 = ctypes.windll.user32

    # ================= 辅助工具函数 =================

    def create_image(self, color):
        """
        生成一个简单的纯色托盘图标。
        使用 Pillow 库绘制，避免依赖外部图片文件。
        """
        width, height = 64, 64
        image = Image.new('RGB', (width, height), color)
        dc = ImageDraw.Draw(image)
        # 绘制一个简单的矩形代表手机/锁
        dc.rectangle((16, 20, 48, 52), fill='white') 
        # 绘制一个小圆点代表状态
        dc.ellipse((28, 32, 36, 40), fill=color)
        return image

    def is_workstation_locked(self):
        """
        判断工作站当前是否已锁定。
        原理: 尝试打开输入桌面 (Input Desktop)。如果失败，通常意味着在锁屏界面。
        注意: 这不是官方 API，但在普通用户会话中很有效。
        """
        try:
            # 0x0100 = DESKTOP_SWITCHDESKTOP 权限
            h_desktop = self.user32.OpenInputDesktop(0, False, 0x0100)
            if h_desktop == 0:
                return True
            self.user32.CloseDesktop(h_desktop)
            return False
        except:
            # 如果发生异常，为了安全起见，假设已锁定
            return True

    def lock_workstation(self):
        """
        执行系统锁屏操作。
        核心: ctypes.windll.user32.LockWorkStation()
        """
        # 只有在未锁定时才尝试锁屏，避免重复调用
        if not self.is_workstation_locked():
            print(f"[操作] 触发锁屏! (平均信号过弱)")
            self.user32.LockWorkStation()
            # 锁屏后，强制将图标设为红色以反馈状态
            self.update_ui_state(rssi_level=MISSING_DEVICE_RSSI)
        else:
            # print("[状态] 系统已处于锁定状态")
            pass

    def create_startup_shortcut(self, icon=None, item=None):
        """
        创建开机启动快捷方式到 Windows 启动文件夹。
        使用 pywin32 的 WScript.Shell 实现。
        """
        try:
            shell = win32com.client.Dispatch("WScript.Shell")
            startup_dir = shell.SpecialFolders("Startup")
            shortcut_path = os.path.join(startup_dir, f"{STARTUP_NAME}.lnk")
            
            # 获取当前脚本路径
            # 如果是打包后的 exe，sys.executable 指向 exe
            # 如果是脚本运行，指向 python.exe，参数需要加上脚本路径
            target = sys.executable
            script_path = os.path.abspath(__file__)
            
            shortcut = shell.CreateShortCut(shortcut_path)
            shortcut.TargetPath = target
            # 如果不是 frozen (打包) 状态，需要把脚本路径作为参数传给 python.exe
            if not getattr(sys, 'frozen', False):
                shortcut.Arguments = f'"{script_path}"'
                
            shortcut.WorkingDirectory = os.path.dirname(script_path)
            shortcut.IconLocation = target
            shortcut.WindowStyle = 7 # 7 = Minimized (最小化运行，虽然托盘程序通常无窗口)
            shortcut.save()
            
            print(f"[设置] 已添加开机自启: {shortcut_path}")
            if self.icon:
                self.icon.notify("已成功添加到开机启动项", "设置成功")
        except Exception as e:
            print(f"[错误] 创建快捷方式失败: {e}")
            if self.icon:
                self.icon.notify(f"添加失败: {e}", "设置错误")

    # ================= 蓝牙核心逻辑 =================

    async def select_device_logic(self):
        """
        第一步: 扫描并让用户选择目标设备。
        这是一个 async 函数，需要在事件循环中运行。
        """
        print("\n--- 正在扫描蓝牙设备 (约5秒)... ---")
        # 更新托盘提示
        self.last_rssi_text = "正在初始化扫描..."
        self.update_ui_state(paused=True) # 暂时显示为灰色

        try:
            # 执行一次性扫描
            devices = await BleakScanner.discover(timeout=5.0)
            
            # 过滤掉没有名字的设备，并按信号强度降序排列
            named_devices = sorted(
                [d for d in devices if d.name], 
                key=lambda x: x.rssi, 
                reverse=True
            )

            if not named_devices:
                print("未找到任何具名设备。请确保手机蓝牙已打开且可被发现。")
                print("提示: 某些手机在锁屏状态下可能不广播蓝牙名称。")
                return False

            print("\n发现以下设备:")
            for idx, dev in enumerate(named_devices):
                print(f"[{idx + 1}] {dev.name} (RSSI: {dev.rssi} dBm) - Address: {dev.address}")
            
            print("\n------------------------------------------------")
            # 这里使用 input 阻塞控制台等待用户输入
            # 注意: 如果是无头模式运行，这里需要改为读取配置文件
            while True:
                s = input("请输入设备序号进行绑定 (输入 0 退出): ")
                if s == '0': 
                    return False
                try:
                    idx = int(s) - 1
                    if 0 <= idx < len(named_devices):
                        target = named_devices[idx]
                        self.target_address = target.address
                        self.target_name = target.name
                        print(f"[选中] 已绑定目标: {self.target_name} [{self.target_address}]")
                        
                        # 选中后，重置历史数据为当前信号值，避免刚启动就锁屏
                        for _ in range(RSSI_HISTORY_LEN):
                            self.rssi_history.append(target.rssi)
                        
                        return True
                    else:
                        print("序号无效，请重新输入。")
                except ValueError:
                    print("输入无效。")
        except Exception as e:
            print(f"[错误] 扫描过程出错: {e}")
            return False

    async def monitor_loop(self):
        """
        第二步: 核心监控循环。
        每隔几秒扫描一次，更新 RSSI，并判断是否需要锁屏。
        """
        print(f"[监控] 开始后台监控: {self.target_name}")
        
        while self.running:
            loop_start_time = time.time()
            
            # 如果暂停，则跳过逻辑
            if self.paused:
                self.update_ui_state(paused=True)
                await asyncio.sleep(1)
                continue

            current_rssi = MISSING_DEVICE_RSSI
            is_found = False

            try:
                # 使用 discover 模式进行短时扫描
                # 注意: 这是比较耗时的操作 (阻塞 async loop SCAN_DURATION 秒)
                start_scan = time.time()
                devices = await BleakScanner.discover(timeout=SCAN_DURATION)
                # print(f"[调试] 扫描耗时: {time.time() - start_scan:.2f}s")
                
                # 在扫描结果中寻找目标设备
                for dev in devices:
                    if dev.address == self.target_address:
                        current_rssi = dev.rssi
                        is_found = True
                        break
                
                # --- 数据处理 ---
                self.rssi_history.append(current_rssi)
                avg_rssi = mean(self.rssi_history)
                
                # --- 日志输出 ---
                status_msg = f"信号: {current_rssi} dBm | 平均: {avg_rssi:.1f} dBm"
                if not is_found:
                    status_msg += " (未检测到设备)"
                
                # 在控制台打印状态 (仅供调试)
                # sys.stdout.write(f"\r{status_msg}   ") 
                # sys.stdout.flush()
                print(status_msg)

                # --- UI 更新 ---
                self.last_rssi_text = f"实时: {current_rssi} | 平均: {avg_rssi:.1f}"
                self.update_ui_state(rssi_level=avg_rssi)

                # --- 锁屏判断 (核心逻辑) ---
                # 只有当队列填满数据后才开始判断，防止误判
                if len(self.rssi_history) >= RSSI_HISTORY_LEN:
                    # 如果平均信号弱于阈值 (-80)
                    if avg_rssi < LOCK_THRESHOLD:
                        # 触发锁屏
                        self.lock_workstation()

            except Exception as e:
                print(f"\n[错误] 监控循环异常: {e}")
                # 发生错误时，不要让程序崩溃，等待一下继续
                await asyncio.sleep(2)

            # 控制循环频率
            # 已经消耗了 SCAN_DURATION，如果还不够 CHECK_INTERVAL，则再睡一会儿
            elapsed = time.time() - loop_start_time
            if elapsed < CHECK_INTERVAL:
                await asyncio.sleep(CHECK_INTERVAL - elapsed)

    def thread_entry_point(self):
        """
        后台线程入口函数。
        负责运行 asyncio 事件循环，因为 UI 必须在主线程运行。
        """
        # 创建新的事件循环
        loop = asyncio.new_event_loop()
        asyncio.set_event_loop(loop)
        
        # 1. 执行设备选择
        if not self.target_address:
            success = loop.run_until_complete(self.select_device_logic())
            if not success:
                print("设备选择失败或用户取消，程序即将退出。")
                self.quit_app()
                return

        # 2. 如果选择成功，执行监控循环
        loop.run_until_complete(self.monitor_loop())
        loop.close()

    # ================= UI 交互回调 =================

    def update_ui_state(self, rssi_level=0, paused=False):
        """
        根据当前状态更新托盘图标的颜色和提示文字。
        """
        if not self.icon: return
        
        # 构造提示信息
        if self.target_name:
            tooltip = f"正在监控: {self.target_name}\n{self.last_rssi_text}"
        else:
            tooltip = "未绑定设备"

        if paused:
            # 暂停状态: 灰色图标
            self.icon.icon = self.create_image("gray")
            tooltip = "【监控已暂停】\n右键菜单可恢复"
        elif rssi_level < LOCK_THRESHOLD:
            # 危险/锁屏状态: 红色图标
            self.icon.icon = self.create_image("red")
        else:
            # 安全状态: 绿色图标
            self.icon.icon = self.create_image("green")
            
        self.icon.title = tooltip

    def on_toggle_pause(self, icon, item):
        """暂停/继续 菜单回调"""
        self.paused = not self.paused
        state = "暂停" if self.paused else "继续"
        print(f"\n[操作] 用户切换状态: {state}")
        # 立即刷新图标状态
        self.update_ui_state(paused=self.paused)

    def on_reset_device(self, icon, item):
        """重置/重新扫描 菜单回调"""
        print("\n[操作] 用户请求重新扫描。请重启程序。")
        self.icon.notify("请重新启动程序以选择新设备", "需重启")
        self.quit_app(icon, item)

    def quit_app(self, icon=None, item=None):
        """退出程序"""
        print("\n[退出] 程序正在关闭...")
        self.running = False
        if self.icon:
            self.icon.stop()
        # 强制退出，因为 asyncio 线程可能还在阻塞中
        os._exit(0)

    def run(self):
        """程序主入口"""
        print("========================================")
        print("   Windows 蓝牙距离感应锁屏工具 v1.0")
        print("========================================")
        
        # 1. 启动后台线程处理蓝牙逻辑
        t = threading.Thread(target=self.thread_entry_point)
        t.daemon = True # 设置为守护线程，主程序退出时自动结束
        t.start()

        # 2. 初始化系统托盘 (必须在主线程运行)
        # 定义右键菜单
        menu = pystray.Menu(
            pystray.MenuItem("暂停/继续监控", self.on_toggle_pause),
            pystray.MenuItem("设置开机自启", self.create_startup_shortcut),
            pystray.MenuItem("重新选择设备", self.on_reset_device),
            pystray.MenuItem("退出", self.quit_app)
        )

        self.icon = pystray.Icon("BleLock", self.create_image("blue"), "正在初始化...", menu)
        print("系统托盘图标已启动，请查看任务栏右下角。")
        print("注意: 首次运行请在控制台关注设备选择提示。")
        
        # 进入 UI 事件循环 (阻塞主线程)
        try:
            self.icon.run()
        except KeyboardInterrupt:
            self.quit_app()

if __name__ == "__main__":
    app = BleLockApp()
    app.run()
