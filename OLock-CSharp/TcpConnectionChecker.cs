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
    // 连接检测结果的原因归因
    internal enum ConnectionReason
    {
        // 已命中手机会话连接
        Connected,
        // 主程序进程未运行
        ProcessNotRunning,
        // 进程在跑，但没有任何 ESTABLISHED 连接
        NoEstablishedConnection,
        // 有 ESTABLISHED 连接，但远程 IP 均不像手机（不匹配私网前缀）
        IpNotMatched,
        // 连接表查询本身失败
        CheckFailed
    }

    // 连接检测的完整结果：除布尔判定外，附带测试窗口展示所需的诊断信息
    internal class PhoneConnectionResult
    {
        public bool IsConnected { get; set; }
        // 命中时的远程地址与进程 PID
        public string? RemoteIp { get; set; }
        public uint? Pid { get; set; }
        public bool AppRunning { get; set; }
        // 主程序持有的 ESTABLISHED 连接条数（无论是否命中）
        public int EstablishedCount { get; set; }
        // 未命中时的远程地址样本（最多 3 个），用于诊断"为什么没命中"
        public List<string> SampleRemoteIps { get; set; } = new List<string>();
        public ConnectionReason Reason { get; set; }
    }

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

        private static readonly List<string> emptySamples = new List<string>(0);

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
            catch (Exception ex)
            {
                LogError("网络", $"进程枚举失败: {Describe(ex)}");
                return false;
            }
        }

        private static HashSet<uint> GetAppPids()
        {
            var pids = new HashSet<uint>();
            foreach (var proc in Process.GetProcessesByName(config.AppProcessName))
            {
                try { pids.Add((uint)proc.Id); }
                finally { proc.Dispose(); }
            }
            return pids;
        }

        // Checks whether the configured process has an established connection
        // to a configured remote IP prefix.
        // 常态监控每秒调用：仅在开启详细日志时收集样本，命中即提前中断扫描，保证主循环零额外堆分配
        internal static async Task<bool> CheckPhoneConnectionAsync()
        {
            var pids = GetAppPids();
            if (pids.Count == 0)
            {
                if (config.LogVerbose)
                    LogDebug("网络", $"{config.AppProcessName} 进程未找到 (跳过连接检查)");
                return false;
            }

            if (config.LogVerbose)
                LogDebug("网络", $"待匹配 PID: {string.Join(", ", pids.OrderBy(p => p))}");

            return await Task.Run(() => CheckPhoneConnectionDetail(pids, collectDiagnostics: config.LogVerbose).IsConnected);
        }

        // 完整版检测：供测试窗口使用，无条件收集诊断详情（进程状态、连接条数、命中信息、未命中去重样本与归因）
        internal static async Task<PhoneConnectionResult> CheckPhoneConnectionDetailAsync()
        {
            var pids = GetAppPids();
            if (pids.Count == 0)
            {
                LogDebug("网络", $"{config.AppProcessName} 进程未找到 (跳过连接检查)");
                return new PhoneConnectionResult { AppRunning = false, Reason = ConnectionReason.ProcessNotRunning };
            }

            if (config.LogVerbose)
                LogDebug("网络", $"待匹配 PID: {string.Join(", ", pids.OrderBy(p => p))}");

            // 表查询放到后台线程执行，避免阻塞 UI
            return await Task.Run(() => CheckPhoneConnectionDetail(pids, collectDiagnostics: true));
        }

        private static PhoneConnectionResult CheckPhoneConnectionDetail(HashSet<uint> pids, bool collectDiagnostics)
        {
            try
            {
                // 第一次调用传空缓冲区，获取所需缓冲区大小
                int size = 0;
                uint ret = GetExtendedTcpTable(IntPtr.Zero, ref size, false, AF_INET, TCP_TABLE_OWNER_PID_ALL, 0);
                if (ret == NO_ERROR && size <= sizeof(int))
                    return EvaluateConnection(true, 0, null, null, emptySamples); // 连接表为空
                if (ret != ERROR_INSUFFICIENT_BUFFER || size < sizeof(int) + TCP_ROW_SIZE)
                {
                    LogError("网络", $"GetExtendedTcpTable 获取缓冲区大小失败: ret={ret}, size={size}");
                    return new PhoneConnectionResult { AppRunning = true, Reason = ConnectionReason.CheckFailed };
                }

                for (int attempt = 0; attempt < 3; attempt++)
                {
                    IntPtr buffer = Marshal.AllocHGlobal(size);
                    try
                    {
                        ret = GetExtendedTcpTable(buffer, ref size, false, AF_INET, TCP_TABLE_OWNER_PID_ALL, 0);
                        if (ret == NO_ERROR)
                            return EvaluateFacts(ScanTcpTableForConnection(buffer, size, pids, collectDiagnostics));
                    }
                    finally
                    {
                        Marshal.FreeHGlobal(buffer);
                    }

                    // 两次调用之间连接表可能增长，按 API 返回的新 size 重试
                    if (ret != ERROR_INSUFFICIENT_BUFFER)
                        break;
                }

                LogError("网络", $"GetExtendedTcpTable 调用失败: ret={ret}");
            }
            catch (Exception ex)
            {
                LogError("网络", $"CheckPhoneConnection 异常: {Describe(ex)}");
            }
            return new PhoneConnectionResult { AppRunning = true, Reason = ConnectionReason.CheckFailed };
        }

        // 归因纯函数：根据扫描产物推断连接状态与原因。无任何系统调用，可单元测试。
        internal static PhoneConnectionResult EvaluateConnection(bool appRunning, int establishedCount,
            string? hitIp, uint? hitPid, List<string>? samples)
        {
            var sampleList = samples ?? emptySamples;
            if (!appRunning)
                return new PhoneConnectionResult { AppRunning = false, Reason = ConnectionReason.ProcessNotRunning };

            if (hitIp != null)
            {
                return new PhoneConnectionResult
                {
                    AppRunning = true,
                    IsConnected = true,
                    RemoteIp = hitIp,
                    Pid = hitPid,
                    EstablishedCount = establishedCount,
                    Reason = ConnectionReason.Connected
                };
            }

            if (establishedCount > 0)
            {
                return new PhoneConnectionResult
                {
                    AppRunning = true,
                    EstablishedCount = establishedCount,
                    SampleRemoteIps = sampleList,
                    Reason = ConnectionReason.IpNotMatched
                };
            }

            return new PhoneConnectionResult
            {
                AppRunning = true,
                SampleRemoteIps = sampleList,
                Reason = ConnectionReason.NoEstablishedConnection
            };
        }

        private static PhoneConnectionResult EvaluateFacts(TcpScanFacts facts)
            => EvaluateConnection(true, facts.EstablishedCount, facts.HitIp, facts.HitPid, facts.Samples);

        // 扫描原始产物：建立连接条数、命中信息、未命中样本
        private readonly struct TcpScanFacts
        {
            public readonly int EstablishedCount;
            public readonly string? HitIp;
            public readonly uint? HitPid;
            public readonly List<string> Samples;

            public TcpScanFacts(int establishedCount, string? hitIp, uint? hitPid, List<string> samples)
            {
                EstablishedCount = establishedCount;
                HitIp = hitIp;
                HitPid = hitPid;
                Samples = samples;
            }
        }

        // 扫描内核 TCP 连接表，收集主程序进程的连接事实（不做归因，归因在 EvaluateConnection）
        private static TcpScanFacts ScanTcpTableForConnection(IntPtr buffer, int size, HashSet<uint> pids, bool collectDiagnostics)
        {
            int numEntries = Marshal.ReadInt32(buffer);
            int maxEntries = (size - sizeof(int)) / TCP_ROW_SIZE;
            if (numEntries < 0 || numEntries > maxEntries)
                numEntries = maxEntries; // 防御：避免越界读取

            int pidEstab = 0;
            string? hitIp = null;
            uint? hitPid = null;
            // 样本仅在需要诊断时收集 (≤3 个且去重)
            var samples = collectDiagnostics ? new List<string>() : null;

            IntPtr rowPtr = buffer + sizeof(int);
            for (int i = 0; i < numEntries; i++)
            {
                if ((uint)Marshal.ReadInt32(rowPtr) == MIB_TCP_STATE_ESTAB)
                {
                    uint pid = (uint)Marshal.ReadInt32(rowPtr + 20);
                    if (pids.Contains(pid))
                    {
                        pidEstab++;
                        // 远程地址是网络字节序的 DWORD，低字节即第一个八位组，IPAddress 可直接解析
                        var remoteIP = new IPAddress((long)(uint)Marshal.ReadInt32(rowPtr + 12)).ToString();
                        if (hitIp == null && HasAllowedRemoteIpPrefix(remoteIP))
                        {
                            hitIp = remoteIP;
                            hitPid = pid;
                            if (!collectDiagnostics)
                                break; // 常态无诊断需求：命中即提前退出，避免无谓扫描
                        }
                        if (hitIp == null && samples != null && samples.Count < 3 && !samples.Contains(remoteIP))
                            samples.Add(remoteIP);
                    }
                }
                rowPtr += TCP_ROW_SIZE;
            }

            if (hitIp != null)
            {
                if (config.LogVerbose)
                    LogDebug("网络", $"检查: 连接表 {numEntries} 行, 命中 {hitIp} (PID {hitPid} = {config.AppProcessName})");
            }
            else if (config.LogVerbose)
            {
                LogDebug("网络", $"检查: 连接表 {numEntries} 行, {config.AppProcessName} ESTABLISHED {pidEstab} 条" +
                    (samples != null && samples.Count > 0 ? $", 未命中 (远程地址样本: {string.Join(", ", samples)})" : $", 无 {config.AppProcessName} 的 ESTABLISHED 连接"));
            }

            return new TcpScanFacts(pidEstab, hitIp, hitPid, samples ?? emptySamples);
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
            if (!IPAddress.TryParse(ipStr, out IPAddress? addr) || addr is null)
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
            if (!IPAddress.TryParse(ipAddress, out IPAddress? parsedAddress) || parsedAddress is null)
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
