<a name="-english-introduction"></a>

## English Introduction

**O-Lock** is a smart auto-lock tool tailored for **OPPO / OnePlus** users using "PC Connect".

### 🧐 Why this tool?

When using **OPPO Connect (PC Connect)**, the native Windows "Dynamic Lock" often fails because the connection occupies the Bluetooth channel, making the signal unstable.

**O-Lock** solves this by monitoring the **TCP connection** of the OPPO Connect service directly, instead of relying on Bluetooth RSSI. **If file transfer works, O-Lock knows you are there!**

### 🧠 How it Works

1.  **⚪ Gray (Waiting)**: Waits for `O+Connect.exe` to start. No lock action.
2.  **🟡 Yellow (Buffering)**: App started. Waits 60s for the phone to connect.
    - Connected -> **Green**.
    - Timeout -> **Locks Screen**.
3.  **🟢 Green (Guarding)**: Phone connected.
    - Disconnected (3 checks/9s) -> **Locks Screen**.
4.  **🔴 Red (Locked)**: Screen locked. Resets to Gray upon unlock.

### 📥 Installation & Usage

1.  **Download**: Get the latest `O-Lock.exe` from the [Releases](https://github.com/你的GitHub用户名/O-Lock/releases) page on the right side.
2.  **Run**: Double-click the file (Portable, no installation needed).
3.  **Tray Icon**: A small dot will appear in the system tray indicating the status:
    - ⚪ **Gray**: Waiting for OPPO Connect to start.
    - 🟡 **Yellow**: App started. Waiting for phone connection...
    - 🟢 **Green**: Phone Online & Guarding.
4.  **Start on Boot**: Right-click the tray icon -> Check **"Start on Boot"**.

### Contributors
**Github Copilot**
