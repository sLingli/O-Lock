using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
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
        // 有 ESTABLISHED 连接，但远程 IP 均不像手机（不匹配允许前缀）
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

    // 手机连接检测：进程存活 + 通过 GetExtendedTcpTable 查询 IPv4/IPv6 ESTABLISHED 连接
    internal static class TcpConnectionChecker
    {
        private static readonly object localIpLock = new object();
        private static HashSet<string> localIpAddresses = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private static long localIpAddressesLoadedAtTick = -1;

        static TcpConnectionChecker()
        {
            try
            {
                NetworkChange.NetworkAddressChanged += (_, _) => InvalidateLocalIpCache();
            }
            catch
            {
            }
        }

        // ================== TCP 连接表查询 (iphlpapi) ==================
        // 直接调用系统 API 读取 TCP 连接表 (netstat 内部使用的同一 API)，
        // 避免每次检测都启动 netstat 子进程

        [DllImport("iphlpapi.dll")]
        private static extern uint GetExtendedTcpTable(IntPtr pTcpTable, ref int pdwSize, bool bOrder, uint ulAf, uint tableClass, uint reserved);

        private const uint NO_ERROR = 0;
        private const uint ERROR_INSUFFICIENT_BUFFER = 122;
        private const uint AF_INET = 2;                 // IPv4 TCP 连接表
        private const uint AF_INET6 = 23;               // IPv6 / 双栈映射 TCP 连接表
        private const uint TCP_TABLE_OWNER_PID_ALL = 5; // 返回带拥有进程 PID 的全部 TCP 连接
        private const uint MIB_TCP_STATE_ESTAB = 5;     // ESTABLISHED 状态
        // MIB_TCPROW_OWNER_PID: state(0)/localAddr(4)/localPort(8)/remoteAddr(12)/remotePort(16)/owningPid(20)，共 24 字节
        private const int TCP_ROW_SIZE = 24;
        // MIB_TCP6ROW_OWNER_PID: localAddr(0,16)/localScope(16)/localPort(20)/remoteAddr(24,16)/remoteScope(40)/remotePort(44)/state(48)/owningPid(52)，共 56 字节
        private const int TCP6_ROW_SIZE = 56;

        private static readonly List<string> emptySamples = new List<string>(0);

        internal static void InvalidateLocalIpCache()
        {
            lock (localIpLock)
            {
                localIpAddressesLoadedAtTick = -1;
            }
        }

        // Checks whether the configured process is running.
        // 在后台线程执行
        internal static bool IsAppRunning()
        {
            var cfg = config;
            return GetAppPids(cfg.AppProcessName).Count > 0;
        }

        private static HashSet<uint> GetAppPids(string processName)
        {
            var pids = new HashSet<uint>();
            try
            {
                var processes = Process.GetProcessesByName(processName);
                foreach (var proc in processes)
                {
                    try { pids.Add((uint)proc.Id); }
                    finally { proc.Dispose(); }
                }
            }
            catch (Exception ex)
            {
                LogError("Network", $"Process enumeration failed: {Describe(ex)}");
            }
            return pids;
        }

        // 单次后台检测合并进程枚举与 TCP 表查询：避免 UI 线程阻塞，且每轮只扫描一次进程表
        internal static Task<(bool AppRunning, bool? PhoneConnected)> CheckMonitorFactsAsync(bool checkConnection)
        {
            var cfg = config;
            return Task.Run(() =>
            {
                var pids = GetAppPids(cfg.AppProcessName);
                if (pids.Count == 0)
                {
                    if (checkConnection && cfg.LogVerbose)
                        LogDebug("Network", $"{cfg.AppProcessName} process not found (skipping connection check)");
                    return (false, checkConnection ? (bool?)false : null);
                }

                if (!checkConnection)
                    return (true, (bool?)null);

                if (cfg.LogVerbose)
                    LogDebug("Network", $"Matching PIDs: {string.Join(", ", pids.OrderBy(p => p))}");

                bool connected = CheckPhoneConnectionDetail(pids, cfg, collectDiagnostics: cfg.LogVerbose).IsConnected;
                return (true, (bool?)connected);
            });
        }

        // Checks whether the configured process has an established connection
        // to a configured remote IP prefix.
        // 进程枚举与表查询均在后台线程完成，不阻塞 UI
        internal static Task<bool> CheckPhoneConnectionAsync()
        {
            var cfg = config;
            return Task.Run(() =>
            {
                var pids = GetAppPids(cfg.AppProcessName);
                if (pids.Count == 0)
                {
                    if (cfg.LogVerbose)
                        LogDebug("Network", $"{cfg.AppProcessName} process not found (skipping connection check)");
                    return false;
                }

                if (cfg.LogVerbose)
                    LogDebug("Network", $"Matching PIDs: {string.Join(", ", pids.OrderBy(p => p))}");

                return CheckPhoneConnectionDetail(pids, cfg, collectDiagnostics: cfg.LogVerbose).IsConnected;
            });
        }

        // 完整版检测：供测试窗口使用，无条件收集诊断详情（进程状态、连接条数、命中信息、未命中去重样本与归因）
        internal static Task<PhoneConnectionResult> CheckPhoneConnectionDetailAsync()
        {
            var cfg = config;
            return Task.Run(() =>
            {
                var pids = GetAppPids(cfg.AppProcessName);
                if (pids.Count == 0)
                {
                    LogDebug("Network", $"{cfg.AppProcessName} process not found (skipping connection check)");
                    return new PhoneConnectionResult { AppRunning = false, Reason = ConnectionReason.ProcessNotRunning };
                }

                if (cfg.LogVerbose)
                    LogDebug("Network", $"Matching PIDs: {string.Join(", ", pids.OrderBy(p => p))}");

                return CheckPhoneConnectionDetail(pids, cfg, collectDiagnostics: true);
            });
        }

        private static PhoneConnectionResult CheckPhoneConnectionDetail(HashSet<uint> pids, AppConfig cfg, bool collectDiagnostics)
        {
            try
            {
                bool v4Ok = TryQueryTcpTable(AF_INET, TCP_ROW_SIZE, pids, cfg, collectDiagnostics, out TcpScanFacts v4Facts);
                if (v4Ok && v4Facts.HitIp != null && !collectDiagnostics)
                    return EvaluateFacts(v4Facts);

                bool v6Ok = TryQueryTcpTable(AF_INET6, TCP6_ROW_SIZE, pids, cfg, collectDiagnostics, out TcpScanFacts v6Facts);
                if (!v4Ok && !v6Ok)
                    return new PhoneConnectionResult { AppRunning = true, Reason = ConnectionReason.CheckFailed };

                int totalEstab = (v4Ok ? v4Facts.EstablishedCount : 0) + (v6Ok ? v6Facts.EstablishedCount : 0);
                string? hitIp = v4Ok && v4Facts.HitIp != null ? v4Facts.HitIp : (v6Ok ? v6Facts.HitIp : null);
                uint? hitPid = v4Ok && v4Facts.HitIp != null ? v4Facts.HitPid : (v6Ok ? v6Facts.HitPid : null);

                List<string> samples = emptySamples;
                if (collectDiagnostics && hitIp == null)
                {
                    samples = new List<string>(3);
                    if (v4Ok)
                    {
                        foreach (string s in v4Facts.Samples)
                            if (samples.Count < 3 && !samples.Contains(s)) samples.Add(s);
                    }
                    if (v6Ok)
                    {
                        foreach (string s in v6Facts.Samples)
                            if (samples.Count < 3 && !samples.Contains(s)) samples.Add(s);
                    }
                }

                return EvaluateConnection(true, totalEstab, hitIp, hitPid, samples);
            }
            catch (Exception ex)
            {
                LogError("Network", $"CheckPhoneConnection exception: {Describe(ex)}");
                return new PhoneConnectionResult { AppRunning = true, Reason = ConnectionReason.CheckFailed };
            }
        }

        private static bool TryQueryTcpTable(
            uint addressFamily,
            int rowSize,
            HashSet<uint> pids,
            AppConfig cfg,
            bool collectDiagnostics,
            out TcpScanFacts facts)
        {
            facts = new TcpScanFacts(0, null, null, emptySamples);

            int size = 0;
            uint ret = GetExtendedTcpTable(IntPtr.Zero, ref size, false, addressFamily, TCP_TABLE_OWNER_PID_ALL, 0);
            if (ret == NO_ERROR && size <= sizeof(int))
                return true; // 连接表为空
            if (ret != ERROR_INSUFFICIENT_BUFFER || size < sizeof(int) + rowSize)
            {
                // IPv6 在禁用了 IPv6 协议栈的机器上可能返回错误，不污染 IPv4 正常检测
                if (addressFamily == AF_INET)
                    LogError("Network", $"GetExtendedTcpTable(AF_INET) buffer size query failed: ret={ret}, size={size}");
                return false;
            }

            for (int attempt = 0; attempt < 3; attempt++)
            {
                IntPtr buffer = Marshal.AllocHGlobal(size);
                try
                {
                    ret = GetExtendedTcpTable(buffer, ref size, false, addressFamily, TCP_TABLE_OWNER_PID_ALL, 0);
                    if (ret == NO_ERROR)
                    {
                        facts = addressFamily == AF_INET
                            ? ScanTcpV4Table(buffer, size, pids, cfg, collectDiagnostics)
                            : ScanTcpV6Table(buffer, size, pids, cfg, collectDiagnostics);
                        return true;
                    }
                }
                finally
                {
                    Marshal.FreeHGlobal(buffer);
                }

                // 两次调用之间连接表可能增长，按 API 返回的新 size 重试
                if (ret != ERROR_INSUFFICIENT_BUFFER)
                    break;
            }

            if (addressFamily == AF_INET)
                LogError("Network", $"GetExtendedTcpTable(AF_INET) call failed: ret={ret}");
            return false;
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

        // 扫描内核 IPv4 TCP 连接表，收集主程序进程的连接事实
        private static TcpScanFacts ScanTcpV4Table(IntPtr buffer, int size, HashSet<uint> pids, AppConfig cfg, bool collectDiagnostics)
        {
            int numEntries = Marshal.ReadInt32(buffer);
            int maxEntries = (size - sizeof(int)) / TCP_ROW_SIZE;
            if (numEntries < 0 || numEntries > maxEntries)
                numEntries = maxEntries;

            int pidEstab = 0;
            string? hitIp = null;
            uint? hitPid = null;
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
                        var remoteIP = new IPAddress((long)(uint)Marshal.ReadInt32(rowPtr + 12)).ToString();
                        if (hitIp == null && HasAllowedRemoteIpPrefix(remoteIP, cfg))
                        {
                            hitIp = remoteIP;
                            hitPid = pid;
                            if (!collectDiagnostics)
                                break;
                        }
                        if (hitIp == null && samples != null && samples.Count < 3 && !samples.Contains(remoteIP))
                            samples.Add(remoteIP);
                    }
                }
                rowPtr += TCP_ROW_SIZE;
            }

            LogScanSummary("IPv4", numEntries, pidEstab, hitIp, hitPid, samples, cfg);
            return new TcpScanFacts(pidEstab, hitIp, hitPid, samples ?? emptySamples);
        }

        // 扫描内核 IPv6 / 双栈映射 TCP 连接表
        private static TcpScanFacts ScanTcpV6Table(IntPtr buffer, int size, HashSet<uint> pids, AppConfig cfg, bool collectDiagnostics)
        {
            int numEntries = Marshal.ReadInt32(buffer);
            int maxEntries = (size - sizeof(int)) / TCP6_ROW_SIZE;
            if (numEntries < 0 || numEntries > maxEntries)
                numEntries = maxEntries;

            int pidEstab = 0;
            string? hitIp = null;
            uint? hitPid = null;
            var samples = collectDiagnostics ? new List<string>() : null;
            byte[] addrBytes = new byte[16];

            IntPtr rowPtr = buffer + sizeof(int);
            for (int i = 0; i < numEntries; i++)
            {
                if ((uint)Marshal.ReadInt32(rowPtr + 48) == MIB_TCP_STATE_ESTAB)
                {
                    uint pid = (uint)Marshal.ReadInt32(rowPtr + 52);
                    if (pids.Contains(pid))
                    {
                        pidEstab++;
                        Marshal.Copy(rowPtr + 24, addrBytes, 0, 16);
                        string remoteIP = NormalizeIpAddress(new IPAddress(addrBytes));
                        if (hitIp == null && HasAllowedRemoteIpPrefix(remoteIP, cfg))
                        {
                            hitIp = remoteIP;
                            hitPid = pid;
                            if (!collectDiagnostics)
                                break;
                        }
                        if (hitIp == null && samples != null && samples.Count < 3 && !samples.Contains(remoteIP))
                            samples.Add(remoteIP);
                    }
                }
                rowPtr += TCP6_ROW_SIZE;
            }

            LogScanSummary("IPv6", numEntries, pidEstab, hitIp, hitPid, samples, cfg);
            return new TcpScanFacts(pidEstab, hitIp, hitPid, samples ?? emptySamples);
        }

        private static void LogScanSummary(string proto, int numEntries, int pidEstab, string? hitIp, uint? hitPid, List<string>? samples, AppConfig cfg)
        {
            if (!cfg.LogVerbose) return;

            if (hitIp != null)
            {
                LogDebug("Network", $"Check({proto}): TCP table {numEntries} rows, matched {hitIp} (PID {hitPid} = {cfg.AppProcessName})");
            }
            else if (pidEstab > 0 || proto == "IPv4")
            {
                LogDebug("Network", $"Check({proto}): TCP table {numEntries} rows, {cfg.AppProcessName} ESTABLISHED {pidEstab} row(s)" +
                    (samples != null && samples.Count > 0 ? $", not matched (sample remote IPs: {string.Join(", ", samples)})" : $", no ESTABLISHED connections for {cfg.AppProcessName}"));
            }
        }

        internal static string NormalizeIpAddress(IPAddress address)
        {
            if (address.IsIPv4MappedToIPv6)
                return address.MapToIPv4().ToString();
            if (address.AddressFamily == AddressFamily.InterNetworkV6 && address.ScopeId != 0)
                return new IPAddress(address.GetAddressBytes()).ToString();
            return address.ToString();
        }

        // 段边界安全的 IP 前缀匹配：例如 "192.168.1" 仅匹配 "192.168.1" 或 "192.168.1.x"，不会误匹配 "192.168.100.x"
        internal static bool MatchesIpPrefix(string remoteIP, string prefix)
        {
            if (string.IsNullOrWhiteSpace(remoteIP) || string.IsNullOrWhiteSpace(prefix))
                return false;

            string trimmedPrefix = prefix.Trim();
            if (!remoteIP.StartsWith(trimmedPrefix, StringComparison.OrdinalIgnoreCase))
                return false;

            if (remoteIP.Length == trimmedPrefix.Length)
                return true;

            if (trimmedPrefix.EndsWith('.') || trimmedPrefix.EndsWith(':'))
                return true;

            char nextChar = remoteIP[trimmedPrefix.Length];
            return nextChar == '.' || nextChar == ':';
        }

        // 判断远程 IP 是否符合白名单策略：
        // 1. 排除本机/回环地址和 IgnoredRemoteIpPrefixes；
        // 2. 若配置了 AllowedRemoteIpPrefixes，严格按白名单匹配；
        // 3. 仅当 AllowedRemoteIpPrefixes 为空时，回退至私网/链路本地地址兜底。
        internal static bool HasAllowedRemoteIpPrefix(string remoteIP, AppConfig? cfg = null, Func<string, bool>? isLocalIpCheck = null)
        {
            if (string.IsNullOrWhiteSpace(remoteIP))
                return false;

            var activeConfig = cfg ?? config;
            string normalizedIp = remoteIP.Trim();
            if (IPAddress.TryParse(normalizedIp, out IPAddress? parsed) && parsed != null)
                normalizedIp = NormalizeIpAddress(parsed);

            if ((isLocalIpCheck ?? IsLocalIpAddress)(normalizedIp))
                return false;

            foreach (string ignoredPrefix in activeConfig.IgnoredRemoteIpPrefixes)
            {
                if (MatchesIpPrefix(normalizedIp, ignoredPrefix))
                    return false;
            }

            bool hasAllowedWhitelist = false;
            foreach (string allowedPrefix in activeConfig.AllowedRemoteIpPrefixes)
            {
                if (string.IsNullOrWhiteSpace(allowedPrefix))
                    continue;
                hasAllowedWhitelist = true;
                if (MatchesIpPrefix(normalizedIp, allowedPrefix))
                    return true;
            }

            if (hasAllowedWhitelist)
                return false;

            // 仅在未配置任何允许前缀时，回退为 RFC 1918 私网 / IPv6 链路本地与 ULA 地址
            return IsPrivateIpAddress(normalizedIp);
        }

        // RFC 1918 IPv4 私网 + IPv6 链路本地 (fe80::/10) 与唯一本地地址 (fc00::/7) 判断
        internal static bool IsPrivateIpAddress(string ipStr)
        {
            if (!IPAddress.TryParse(ipStr, out IPAddress? addr) || addr is null)
                return false;

            if (addr.IsIPv4MappedToIPv6)
                addr = addr.MapToIPv4();

            if (addr.AddressFamily == AddressFamily.InterNetwork)
            {
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

            if (addr.AddressFamily == AddressFamily.InterNetworkV6)
            {
                if (addr.IsIPv6LinkLocal)
                    return true;
                byte[] bytes = addr.GetAddressBytes();
                if (bytes.Length == 16 && (bytes[0] & 0xFE) == 0xFC) // fc00::/7 ULA
                    return true;
            }

            return false;
        }

        private static bool IsLocalIpAddress(string ipAddress)
        {
            if (!IPAddress.TryParse(ipAddress, out IPAddress? parsedAddress) || parsedAddress is null)
                return false;

            if (parsedAddress.IsIPv4MappedToIPv6)
                parsedAddress = parsedAddress.MapToIPv4();

            if (IPAddress.IsLoopback(parsedAddress))
                return true;

            string normalized = NormalizeIpAddress(parsedAddress);
            return GetLocalIpAddresses().Contains(normalized);
        }

        private static HashSet<string> GetLocalIpAddresses()
        {
            long nowTick = Environment.TickCount64;
            lock (localIpLock)
            {
                if (localIpAddressesLoadedAtTick >= 0 &&
                    (nowTick - localIpAddressesLoadedAtTick) < 60_000 &&
                    localIpAddresses.Count > 0)
                {
                    return localIpAddresses;
                }
            }

            var addresses = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            try
            {
                foreach (NetworkInterface networkInterface in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (networkInterface.OperationalStatus != OperationalStatus.Up)
                        continue;

                    foreach (UnicastIPAddressInformation address in networkInterface.GetIPProperties().UnicastAddresses)
                    {
                        if (address.Address.AddressFamily == AddressFamily.InterNetwork ||
                            address.Address.AddressFamily == AddressFamily.InterNetworkV6)
                        {
                            addresses.Add(NormalizeIpAddress(address.Address));
                        }
                    }
                }
            }
            catch { }

            lock (localIpLock)
            {
                localIpAddresses = addresses;
                localIpAddressesLoadedAtTick = Environment.TickCount64;
                return localIpAddresses;
            }
        }
    }
}
