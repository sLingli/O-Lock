<a name="-english-introduction"></a>

## English Introduction

**O-Lock** is a smart auto-lock tool tailored for **OPPO / OnePlus** users using "PC Connect".

### 🧐 Why this tool?

When using **OPPO Connect (PC Connect)**, the native Windows "Dynamic Lock" often fails because the connection occupies the Bluetooth channel, making the signal unstable.

**O-Lock** solves this by monitoring the **TCP connections** of the OPPO Connect main process (`O+Connect.exe`) directly, instead of relying on Bluetooth RSSI. **If file transfer works, O-Lock knows you are there!**

### 🧠 How it Works

1.  **⚪ Gray (Waiting)**: Waits for `O+Connect.exe` to start. No lock action.
2.  **🟡 Yellow (Buffering)**: App started. Waits 60s for the phone to connect.
    - Connected -> **Green**.
    - Timeout -> **Locks Screen**.
3.  **🟢 Green (Guarding)**: Phone connected.
    - Disconnected for 9s (configurable) -> **Locks Screen**.
4.  **🔴 Red (Locked)**: Screen locked. Resets to Gray upon unlock.

### 📥 Installation & Usage

1.  **Download**: Get the latest `O-Lock.exe` from the [Releases](https://github.com/sLingli/O-Lock/releases) page on the right side.
2.  **Run**: Double-click the file (Portable, no installation needed).
3.  **Tray Icon**: A small dot will appear in the system tray indicating the status:
    - ⚪ **Gray**: Waiting for OPPO Connect to start.
    - 🟡 **Yellow**: App started. Waiting for phone connection...
    - 🟢 **Green**: Phone Online & Guarding.
4.  **Start on Boot**: Right-click the tray icon -> Check **"Start on Boot"**.
5.  **Settings**: Right-click the tray icon -> **Settings** to change the process name, offline tolerance, warmup, and more.
    All settings live in the registry under `HKCU\Software\OLock`. Since **v1.3.2** the `olock.config.json` next to the executable is no longer read.

### 🧪 Connection Test (new in v1.4.0)

Right-click the tray icon → **Connection test…** (or double-click `OLock.exe` again while running) to verify the tool without waiting for a real lock:

- **Check now**: Automatically checks when opened, or click to re-check anytime. 🟢 When connected, the matched IP and process PID are shown; 🔴 when not, a diagnosis is given (main process not running / no established connection / remote IP doesn't look like the phone).
- **Disconnect test**: Once the phone is online, start it from the guide inside the window. The test runs the **real monitoring pipeline** (offline countdown, tray icon turns red), but only **simulates** the lock/sleep command when it fires — **the screen never locks and the PC never sleeps**, and monitoring safely waits for the phone to reconnect before resuming lock guard.

### Contributors
**Github Copilot**
