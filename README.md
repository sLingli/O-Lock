<div align="center">

# O-Lock
### OPPO Connect Auto-Lock / OPPO 互联自动锁

[English](README.ENG.md) | [简体中文](README.md)


<p align="center">
  <img src="https://img.shields.io/badge/Platform-Windows-blue" alt="Platform">
  <img src="https://img.shields.io/badge/Device-OPPO%20%2F%20OnePlus-green" alt="Device">
</p>

</div>

<a name="-中文介绍"></a>

## 中文介绍

**O-Lock** 是一款专为 **OPPO / OnePlus/Realme** 用户打造的 Windows 智能锁屏工具。

它是为了解决 **OPPO 互联 (PC Connect)** 与 Windows 自带“动态锁”冲突的问题而生。

### 🧐 为什么要开发这个工具？(背景)

很多 OPPO 用户在使用“OPPO 互联”跨屏协同功能时会发现：**Windows 自带的“蓝牙动态锁”失效了。**

这是因为 OPPO 互联为了保持高速数据传输，会长期占用蓝牙通道或导致蓝牙连接不稳定，系统无法准确判断手机是否离开。

**O-Lock 的解决方案：**
我们不依赖不稳定的蓝牙 RSSI，而是直接监测 **OPPO 互联 (O+Connect.exe)** 的运行状态及其底层 **TCP 网络连接**。
**只要 OPPO 互联还能传数据，O-Lock 就知道你还在！**

---

### 🧠 核心原理与工作流程

O-Lock 采用智能状态机机制，确保在各种场景下都能准确判断，绝不误锁。

#### 1. ⚪ 灰色状态：等待程序启动
- **触发场景**：刚开机，或电脑刚解锁。
- **逻辑**：监测 `O+Connect.exe` (OPPO互联主程序) 是否运行。
- **行为**：如果没运行，保持静默，**绝不锁屏**。

#### 2. 🟡 黄色状态：缓冲期 (Loading)
- **触发场景**：检测到 `O+Connect.exe` 启动了。
- **逻辑**：给予一段缓冲时间（默认 60秒），等待手机自动连接。
- **行为**：
    - **连接成功** → 立即转为 🟢 绿色。
    - **超时未连** → 视为手机不在身边，立即转为 🔴 红色并 **执行锁屏**。

#### 3. 🟢 绿色状态：守护中 (Monitoring)
- **触发场景**：手机通过 OPPO 互联成功连接（TCP 建立）。
- **逻辑**：每 3 秒检测一次心跳。
- **行为**：
    - **连接断开** → 连续 3 次（约 9秒）检测不到连接，执行 **锁屏**。

#### 4. 🔴 红色状态：已锁屏
- **触发场景**：手机离开或连接超时。
- **行为**：锁屏后程序暂停检测，等待下一次用户解锁屏幕，重置回 **⚪ 灰色状态**。

---

### 📥 安装与使用

1.  **下载**：在右侧 [Releases](https://github.com/你的GitHub用户名/O-Lock/releases) 页面下载最新的 `O-Lock.exe`。
2.  **运行**：双击程序，无需安装。
3.  **托盘图标**：右下角会出现一个小圆点，颜色代表当前状态。
    - ⚪ 灰：等待 OPPO 互联启动
    - 🟡 黄：正在连接手机...
    - 🟢 绿：手机在线，放心使用
4.  **开机自启**：在托盘图标上 **右键** -> 勾选 **“开机自启”**。
5.  **v1.1 C# 重构版**

重构为 C#，大幅降低资源占用。

⚠️ 仅支持 Windows 10/11（x64）

| 文件 | 说明 |
|------|------|
| Lite.zip | 需安装 [.NET 8 运行时](https://dotnet.microsoft.com/download/dotnet/8.0) |
| Full.zip | 开箱即用，无需任何依赖 |

---

### 贡献者
**GitHub copilot**

---

### ⚠️ Disclaimer / 免责声明

This project is an independent open-source software and is **not** affiliated with, endorsed by, or connected to **OPPO**, **OnePlus**, or **Realme**.
"OPPO", "PC Connect", "HeyTap" are trademarks of OPPO Electronics Corp.

本项目是一款独立的第三方开源工具，与 **OPPO**、**OnePlus** (一加) 或 **Realme** (真我) 无任何官方关联。
文中所提及的 "OPPO"、"PC Connect" (跨屏互联) 等品牌名称仅用于说明兼容性，其商标所有权归原公司所有。
