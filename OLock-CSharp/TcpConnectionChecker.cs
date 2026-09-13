using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

using static OLock.Logging;
using static OLock.Program;

namespace OLock
{
    // 手机连接检测：进程存活 + 通过 GetExtendedTcpTable 查询 ESTABLISHED 连接
    internal static class TcpConnectionChecker
    {
        private static HashSet<string> localIpAddresses = new HashSet<string>();
        private static DateTime localIpAddressesLoadedAt = DateTime.MinValue;

        // ================== TCP 连接表查询 (iphlpapi) ==================
        // 直接调用系统 API 读取 TCP 连接表 (netstat 内部使用的同一 API)，
        // 避免每次检测都启动 netstat 子进程

        [DllImport("iphlpapi.dll")]
        private static extern uint GetExtendedTcpTable(IntPtr pTcpTable, ref int pdwSize, bool bOrder, uint ulAf, uint tableClass, uint reserved);

        private const uint NO_ERROR = 0;
        private const uint ERROR_INSUFFICIENT_BUFFER = 122;
        private const uint AF_INET = 2;                 // 仅查询 IPv4 连接
        private const uint TCP_TABLE_OWNER_PID_ALL = 5; // 返回带拥有进程 PID 的全部 TCP 连接
        private const uint MIB_TCP_STATE_ESTAB = 5;     // ESTABLISHED 状态
        // MIB_TCPROW_OWNER_PID 布局: state/localAddr/localPort/remoteAddr/remotePort/owningPid，共 6 个 DWORD (24 字节)
        private const int TCP_ROW_SIZE = 24;

        // Checks whether the configured process is running.
        // 在后台线程执行
        internal static bool IsAppRunning()
        {
            try
            {
                var processes = Process.GetProcessesByName(config.AppProcessName);
                try
                {
                    return processes.Length > 0;
                }
                finally
                {
                    foreach (var p in processes)
                        p.Dispose();
                }
            }
            catch { return false; }
        }

        // Checks whether the configured process has an established connection
        // to a configured remote IP prefix.
        // 通过 GetExtendedTcpTable 直接查询内核 TCP 连接表，无外部进程、无文本解析
        internal static async Task<bool> CheckPhoneConnectionAsync()
        {
            var pids = new HashSet<uint>();
            foreach (var proc in Process.GetProcessesByName(config.AppProcessName))
            {
                try { pids.Add((uint)proc.Id); }
                finally { proc.Dispose(); }
            }

            if (pids.Count == 0) return false;

            // 表查询放到后台线程执行，避免阻塞 UI
            return await Task.Run(() => HasEstablishedPhoneConnection(pids));
        }

        private static bool HasEstablishedPhoneConnection(HashSet<uint> pids)
        {
            try
            {
                // 第一次调用传空缓冲区，获取所需缓冲区大小
                int size = 0;
                uint ret = GetExtendedTcpTable(IntPtr.Zero, ref size, false, AF_INET, TCP_TABLE_OWNER_PID_ALL, 0);
                if (ret == NO_ERROR && size <= sizeof(int))
                    return false; // 连接表为空
                if (ret != ERROR_INSUFFICIENT_BUFFER || size < sizeof(int) + TCP_ROW_SIZE)
                {
                    LogError($"GetExtendedTcpTable 获取缓冲区大小失败: ret={ret}, size={size}");
                    return false;
                }

                for (int attempt = 0; attempt < 3; attempt++)
                {
                    IntPtr buffer = Marshal.AllocHGlobal(size);
                    try
                    {
                        ret = GetExtendedTcpTable(buffer, ref size, false, AF_INET, TCP_TABLE_OWNER_PID_ALL, 0);
                        if (ret == NO_ERROR)
                            return ScanTcpTableForConnection(buffer, size, pids);
                    }
                    finally
                    {
                        Marshal.FreeHGlobal(buffer);
                    }

                    // 两次调用之间连接表可能增长，按 API 返回的新 size 重试
                    if (ret != ERROR_INSUFFICIENT_BUFFER)
                        break;
                }

                LogError($"GetExtendedTcpTable 调用失败: ret={ret}");
            }
            catch (Exception ex)
            {
                LogError($"CheckPhoneConnection 异常: {ex.Message}");
            }
            return false;
        }

        private static bool ScanTcpTableForConnection(IntPtr buffer, int size, HashSet<uint> pids)
        {
            int numEntries = Marshal.ReadInt32(buffer);
            int maxEntries = (size - sizeof(int)) / TCP_ROW_SIZE;
            if (numEntries < 0 || numEntries > maxEntries)
                numEntries = maxEntries; // 防御：避免越界读取

            IntPtr rowPtr = buffer + sizeof(int);
            for (int i = 0; i < numEntries; i++)
            {
                if ((uint)Marshal.ReadInt32(rowPtr) == MIB_TCP_STATE_ESTAB)
                {
                    uint pid = (uint)Marshal.ReadInt32(rowPtr + 20);
                    if (pids.Contains(pid))
                    {
                        // 远程地址是网络字节序的 DWORD，低字节即第一个八位组，IPAddress 可直接解析
                        var remoteIP = new IPAddress((long)(uint)Marshal.ReadInt32(rowPtr + 12)).ToString();
                        if (HasAllowedRemoteIpPrefix(remoteIP))
                            return true;
                    }
                }
                rowPtr += TCP_ROW_SIZE;
            }
            return false;
        }

        private static bool HasAllowedRemoteIpPrefix(string remoteIP)
        {
            if (string.IsNullOrWhiteSpace(remoteIP))
                return false;

            if (IsLocalIpAddress(remoteIP))
                return false;

            foreach (string ignoredPrefix in config.IgnoredRemoteIpPrefixes)
            {
                if (!string.IsNullOrWhiteSpace(ignoredPrefix) &&
                    remoteIP.StartsWith(ignoredPrefix, StringComparison.OrdinalIgnoreCase))
                    return false;
            }

            foreach (string allowedPrefix in config.AllowedRemoteIpPrefixes)
            {
                if (!string.IsNullOrWhiteSpace(allowedPrefix) &&
                    remoteIP.StartsWith(allowedPrefix, StringComparison.OrdinalIgnoreCase))
                    return true;
            }

            // Fallback: any RFC 1918 private IP (not local) is treated as phone connection
            if (IsPrivateIpAddress(remoteIP))
                return true;

            return false;
        }

        // RFC 1918 private IPv4 address check
        private static bool IsPrivateIpAddress(string ipStr)
        {
            if (!IPAddress.TryParse(ipStr, out IPAddress addr))
                return false;

            if (addr.AddressFamily != AddressFamily.InterNetwork)
                return false;

            byte[] bytes = addr.GetAddressBytes();
            if (bytes.Length != 4)
                return false;

            // 10.0.0.0/8
            if (bytes[0] == 10) return true;
            // 172.16.0.0/12
            if (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31) return true;
            // 192.168.0.0/16
            if (bytes[0] == 192 && bytes[1] == 168) return true;

            return false;
        }

        private static bool IsLocalIpAddress(string ipAddress)
        {
            if (!IPAddress.TryParse(ipAddress, out IPAddress parsedAddress))
                return false;

            if (IPAddress.IsLoopback(parsedAddress))
                return true;

            return GetLocalIpAddresses().Contains(parsedAddress.ToString());
        }

        private static HashSet<string> GetLocalIpAddresses()
        {
            if ((DateTime.UtcNow - localIpAddressesLoadedAt).TotalSeconds < 60 && localIpAddresses.Count > 0)
                return localIpAddresses;

            var addresses = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            try
            {
                foreach (NetworkInterface networkInterface in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (networkInterface.OperationalStatus != OperationalStatus.Up)
                        continue;

                    foreach (UnicastIPAddressInformation address in networkInterface.GetIPProperties().UnicastAddresses)
                    {
                        if (address.Address.AddressFamily == AddressFamily.InterNetwork)
                            addresses.Add(address.Address.ToString());
                    }
                }
            }
            catch { }

            localIpAddresses = addresses;
            localIpAddressesLoadedAt = DateTime.UtcNow;
            return localIpAddresses;
        }
    }
}
