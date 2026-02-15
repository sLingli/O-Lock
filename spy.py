import psutil
import time

# 咱们要侦察的关键词
KEYWORDS = ["oppo", "oplus", "nearme", "heytap"]

print("=" * 60)
print("🕵️‍♂️  OPPO 互联 网络侦察脚本 (老K特制版)")
print("=" * 60)

found_any = False

# 遍历所有进程
for proc in psutil.process_iter(['pid', 'name', 'exe']):
    try:
        # 获取进程名和路径，转成小写方便匹配
        name = (proc.info['name'] or "").lower()
        exe = (proc.info['exe'] or "").lower()
        
        # 只要名字里带 OPPO/OnePlus 相关字眼
        if any(kw in name or kw in exe for kw in KEYWORDS):
            found_any = True
            print(f"\n[进程] {proc.info['name']} (PID: {proc.info['pid']})")
            
            try:
                # 获取该进程的所有网络连接
                conns = proc.net_connections()
                if conns:
                    for c in conns:
                        # 只显示建立连接的 (ESTABLISHED) 或者 监听的 (LISTEN)
                        if c.status == 'ESTABLISHED':
                            status_icon = "🟢 已连接"
                        elif c.status == 'LISTEN':
                            status_icon = "👂 监听中"
                        elif c.status == 'CLOSE_WAIT':
                            status_icon = "🟡 等待关闭"
                        else:
                            status_icon = f"⚪ {c.status}"

                        local = f"{c.laddr.ip}:{c.laddr.port}" if c.laddr else "?"
                        remote = f"{c.raddr.ip}:{c.raddr.port}" if c.raddr else "无"
                        
                        # 打印连接详情
                        print(f"    --> {status_icon} | 本地: {local} <==> 远程: {remote}")
                else:
                    print("    --> (没有网络活动)")
            except (psutil.AccessDenied, psutil.NoSuchProcess):
                print("    --> ❌ 权限不足 (请以管理员身份运行)")
    except (psutil.NoSuchProcess, psutil.AccessDenied):
        pass

if not found_any:
    print("\n 没找到任何 OPPO 相关的进程！请确认软件已打开。")

print("\n" + "=" * 60)
input("侦察结束！请截图发给老K。按回车键退出...")