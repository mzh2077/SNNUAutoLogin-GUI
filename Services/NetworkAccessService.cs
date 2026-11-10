using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;

namespace SNNUAutoLogin.Services;

/// <summary>当前接入方式。</summary>
public enum NetworkAccessKind
{
    /// <summary>无法判定（没有合格的物理网卡，或探测失败）。</summary>
    Unknown = 0,

    /// <summary>有线。</summary>
    Wired = 1,

    /// <summary>无线。</summary>
    Wireless = 2
}

/// <summary>一次接入方式判定的结果。</summary>
public sealed record NetworkAccessInfo
{
    /// <summary>接入方式。</summary>
    public NetworkAccessKind Kind { get; init; } = NetworkAccessKind.Unknown;

    /// <summary>无线时的 SSID；取不到时为空串。</summary>
    public string Ssid { get; init; } = "";

    /// <summary>SSID 是否可靠取到。false 时只按「无线（任意）」参与规则匹配。</summary>
    public bool SsidReliable { get; init; }

    /// <summary>命中的网卡描述，用于日志与界面自述。</summary>
    public string InterfaceDescription { get; init; } = "";

    /// <summary>判定时间（用于缓存）。</summary>
    public DateTime At { get; init; }
}

/// <summary>接入方式识别服务。抽成接口是为了能在单元测试里换成假替身，不去真读网卡。</summary>
public interface INetworkAccessService
{
    /// <summary>取当前接入方式（带短 TTL 缓存）。</summary>
    NetworkAccessInfo GetCurrent();

    /// <summary>网络状态变化时调用，下次读取会重新判定。</summary>
    void Invalidate();
}

/// <summary>
/// 接入方式识别实现。
///
/// 这里踩过的坑（都是本机实测出来的）：
///   1. 「状态是 Up 的网卡」不等于「有效的接入网卡」—— 虚拟隧道网卡同样是 Up；
///   2. 「默认路由走的网卡」也不等于「有效的接入网卡」—— 隧道网卡会占着默认路由；
///   3. WLAN 接口的 GUID 和 .NET 的 NetworkInterface.Id 是同一个值，
///      所以 SSID ↔ 网卡可以直接对齐，不必去啃 IP_ADAPTER_ADDRESSES 那个变长结构。
///
/// 因此判定分两层：先筛出「物理网卡候选集」，多候选时再用路由择优。
/// </summary>
public sealed class NetworkAccessService : INetworkAccessService
{
    /// <summary>判定结果的缓存时长。网卡状态变化有延迟，几秒的缓存能挡掉大部分重复读取。</summary>
    private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(5);

    /// <summary>择优时用来问路由的目��地址：门户所在的内网地址。</summary>
    private const string RouteProbeHost = "202.117.144.205";

    /// <summary>描述里出现这些关键字的网卡视为虚拟适配器，直接排除。</summary>
    private static readonly string[] VirtualHints =
    {
        "wi-fi direct", "wireless direct", "bluetooth", "hyper-v", "vmware",
        "virtualbox", "vethernet", "loopback", "tunnel", "tap-windows", "npcap"
    };

    private readonly object _gate = new();
    private NetworkAccessInfo? _cached;

    public void Invalidate()
    {
        lock (_gate) _cached = null;
    }

    public NetworkAccessInfo GetCurrent()
    {
        lock (_gate)
        {
            if (_cached is not null && DateTime.Now - _cached.At < CacheTtl)
                return _cached;

            var info = Detect();
            _cached = info;
            return info;
        }
    }

    private static NetworkAccessInfo Detect()
    {
        var candidates = PhysicalCandidates();

        if (candidates.Count == 0)
        {
            return new NetworkAccessInfo
            {
                Kind = NetworkAccessKind.Unknown,
                At = DateTime.Now,
                InterfaceDescription = ""
            };
        }

        NetworkInterface? chosen = candidates.Count == 1 ? candidates[0] : ChooseByRoute(candidates);

        var kind = chosen.NetworkInterfaceType == NetworkInterfaceType.Wireless80211
            ? NetworkAccessKind.Wireless
            : NetworkAccessKind.Wired;

        var info = new NetworkAccessInfo
        {
            Kind = kind,
            At = DateTime.Now,
            InterfaceDescription = chosen.Description
        };

        if (kind != NetworkAccessKind.Wireless) return info;

        var (ssid, reliable) = ReadSsid(chosen.Id);
        return info with { Ssid = ssid, SsidReliable = reliable };
    }

    /// <summary>
    /// 筛出「可能承载校园网接入」的物理网卡。
    ///
    /// 判据：类型是 Ethernet / Wireless80211（虚拟隧道、蓝牙、Loopback 的类型都不是这两个）
    /// + 状态 Up + 描述不含虚拟适配器关键字 + 有非链路本地的 IPv4 + 有默认网关。
    /// </summary>
    internal static List<NetworkInterface> PhysicalCandidates()
    {
        var list = new List<NetworkInterface>();

        foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (ni.NetworkInterfaceType is not (NetworkInterfaceType.Ethernet or NetworkInterfaceType.Wireless80211))
                continue;

            if (ni.OperationalStatus != OperationalStatus.Up) continue;

            if (IsVirtual(ni.Description)) continue;

            if (!HasUsableIpv4(ni)) continue;

            if (!HasDefaultGateway(ni)) continue;

            list.Add(ni);
        }

        return list;
    }

    private static bool IsVirtual(string? description)
    {
        if (string.IsNullOrWhiteSpace(description)) return false;
        string d = description.ToLowerInvariant();
        return VirtualHints.Any(h => d.Contains(h));
    }

    /// <summary>是否有非链路本地、非回环的 IPv4 地址。</summary>
    private static bool HasUsableIpv4(NetworkInterface ni)
    {
        try
        {
            foreach (var ua in ni.GetIPProperties().UnicastAddresses)
            {
                if (ua.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                if (IPAddress.IsLoopback(ua.Address)) continue;

                var b = ua.Address.GetAddressBytes();
                if (b[0] == 169 && b[1] == 254) continue;   // 链路本地（没拿到 DHCP 地址时的自动私有地址）

                return true;
            }
        }
        catch { }
        return false;
    }

    /// <summary>是否有默认网关（能上网的网卡才有网关）。</summary>
    private static bool HasDefaultGateway(NetworkInterface ni)
    {
        try
        {
            foreach (var gw in ni.GetIPProperties().GatewayAddresses)
            {
                if (gw.Address is null) continue;
                if (gw.Address.AddressFamily != AddressFamily.InterNetwork) continue;

                var b = gw.Address.GetAddressBytes();
                if (b[0] == 0 && b[1] == 0 && b[2] == 0 && b[3] == 0) continue;

                return true;
            }
        }
        catch { }
        return false;
    }

    /// <summary>
    /// 多个候选时的择优：
    /// 先问系统「去门户地址该走哪块网卡」，命中候选集就用它；
    /// 没命中（多半是路由指向了虚拟隧道）则退回到「IPv4 接口度量最小者」。
    /// </summary>
    private static NetworkInterface ChooseByRoute(List<NetworkInterface> candidates)
    {
        try
        {
            var best = BestInterfaceIndex(IPAddress.Parse(RouteProbeHost));
            if (best is not null)
            {
                foreach (var ni in candidates)
                {
                    var idx = ni.GetIPProperties().GetIPv4Properties()?.Index;
                    if (idx is not null && idx.Value == best.Value)
                        return ni;
                }
            }
        }
        catch { }

        // 退化路径：按 IPv4 接口度量择优（度量越小越优先）
        NetworkInterface? fallback = null;
        int bestMetric = int.MaxValue;

        foreach (var ni in candidates)
        {
            try
            {
                var v4 = ni.GetIPProperties().GetIPv4Properties();
                if (v4 is null) continue;
                if (v4.Index < bestMetric)
                {
                    bestMetric = v4.Index;
                    fallback = ni;
                }
            }
            catch { }
        }

        return fallback ?? candidates[0];
    }

    /// <summary>
    /// GetBestInterfaceEx：传目标地址，问系统路由会用哪块网卡。
    ///
    /// 必须传 16 字节的 sockaddr_in（AF_INET + 端口 + 4 字节地址），
    /// 只塞 4 字节地址会直接返回错误 87（参数无效）。
    /// </summary>
    internal static uint? BestInterfaceIndex(IPAddress destination)
    {
        try
        {
            var raw = destination.GetAddressBytes();
            if (raw.Length != 4) return null;

            var sockaddr = new byte[16];
            sockaddr[0] = 2; sockaddr[1] = 0;    // AF_INET
            sockaddr[2] = 0; sockaddr[3] = 0;    // 端口 0
            Buffer.BlockCopy(raw, 0, sockaddr, 4, 4);

            uint index;
            uint rc = GetBestInterfaceEx(sockaddr, out index);
            return rc == 0 ? index : null;
        }
        catch
        {
            return null;
        }
    }

    // ────────────────────────── SSID 读取 ──────────────────────────

    /// <summary>
    /// 读取当前无线连接的 SSID。
    ///
    /// WLAN API 的连接属性是变长结构（SSID 长度不固定），
    /// 所以全程按字节缓冲区手工解析 —— 用固定 StructLayout 去套会让后面的字段全部错位。
    /// </summary>
    internal static (string Ssid, bool Reliable) ReadSsid(string? interfaceId)
    {
        if (string.IsNullOrWhiteSpace(interfaceId)) return ("", false);

        if (!Guid.TryParse(interfaceId.Trim('{', '}'), out var want)) return ("", false);

        IntPtr client = IntPtr.Zero;
        try
        {
            uint negotiated;
            uint rc = WlanOpenHandle(2, IntPtr.Zero, out negotiated, out client);
            if (rc != 0) return ("", false);

            IntPtr list = IntPtr.Zero;
            uint count;
            rc = WlanEnumInterfaces(client, IntPtr.Zero, out list, out count);
            if (rc != 0 || list == IntPtr.Zero) return ("", false);

            // 接口条数只能从链表头读：出参里的个数在这台机器上会读回 0
            int items = Marshal.ReadInt32(list, 0);
            if (items <= 0) return ("", false);

            const int headerSize = 8;      // dwNumberOfItems + dwIndex
            const int itemSize = 532;      // GUID(16) + WCHAR[256](512) + isState(4)

            for (int i = 0; i < items; i++)
            {
                IntPtr item = list + headerSize + i * itemSize;

                var guidBytes = new byte[16];
                Marshal.Copy(item, guidBytes, 0, 16);
                var guid = new Guid(guidBytes);

                if (guid != want) continue;

                return ReadSsidFromHandle(client, guid);
            }

            return ("", false);
        }
        catch
        {
            return ("", false);
        }
        finally
        {
            if (client != IntPtr.Zero)
            {
                try { WlanCloseHandle(client, IntPtr.Zero); } catch { }
            }
        }
    }

    /// <summary>向 WLAN 服务查询某接口的当前连接属性并解析 SSID。</summary>
    private static (string Ssid, bool Reliable) ReadSsidFromHandle(IntPtr client, Guid guid)
    {
        IntPtr data = IntPtr.Zero;
        try
        {
            uint size;
            uint opcode;

            // 第一遍：只要长度。ppData 传 NULL 时 WLAN 服务会返回所需字节数
            uint rc = WlanQueryInterface(client, ref guid, CurrentConnectionOpcode,
                IntPtr.Zero, out size, out data, out opcode);
            if (rc != 0 || size == 0) return ("", false);

            // 第二遍：自己分配缓冲，按字节解析
            IntPtr buffer = Marshal.AllocHGlobal((int)size);
            try
            {
                rc = WlanQueryInterface(client, ref guid, CurrentConnectionOpcode,
                    IntPtr.Zero, out size, out data, out opcode);
                if (rc != 0 || data == IntPtr.Zero) return ("", false);

                var bytes = new byte[size];
                Marshal.Copy(data, bytes, 0, (int)size);

                return ParseSsid(bytes);
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
        catch
        {
            return ("", false);
        }
        finally
        {
            // WLAN 服务分配的内存只能用 WlanFreeMemory 释放
            if (data != IntPtr.Zero)
            {
                try { WlanFreeMemory(data); } catch { }
            }
        }
    }

    /// <summary>
    /// 从连接属性缓冲里取 SSID。
    ///
    /// WLAN_CONNECTION_ATTRIBUTES 的布局是：
    ///   isState(4) | mode(4) | DOT11_SSID{ uSSIDLength(4), ucSSID[1..32] } | ...
    /// 也就是长度在偏移 8、字节从偏移 12 开始。
    ///
    /// 但不同 Windows 版本的实现细节可能有出入（有的机器上按规范偏移解不出内容），
    /// 所以规范偏移失败时会在前 48 字节里再扫一遍 —— 只接受「长度合理 + 全是可打印字符」的组合，
    /// 宁可取不到也不让垃圾字节变成 SSID 去参与规则匹配。
    /// </summary>
    internal static (string Ssid, bool Reliable) ParseSsid(byte[] buffer)
    {
        if (buffer is null || buffer.Length < 16) return ("", false);

        if (TryReadSsidAt(buffer, 8, out var ssid)) return (ssid, true);

        int limit = Math.Min(48, buffer.Length - 4);
        for (int off = 8; off <= limit; off++)
        {
            if (off == 8) continue;
            if (TryReadSsidAt(buffer, off, out var alt)) return (alt, true);
        }

        return ("", false);
    }

    private static bool TryReadSsidAt(byte[] buffer, int lengthOffset, out string ssid)
    {
        ssid = "";

        if (lengthOffset < 0 || lengthOffset + 4 > buffer.Length) return false;

        int len = BitConverter.ToInt32(buffer, lengthOffset);
        if (len is < 1 or > 32) return false;

        int start = lengthOffset + 4;
        if (start + len > buffer.Length) return false;

        for (int i = 0; i < len; i++)
        {
            byte b = buffer[start + i];
            // SSID 理论上是任意字节，但合法值不会包含控制字符
            if (b < 0x20 || b > 0x7E) return false;
        }

        ssid = Encoding.ASCII.GetString(buffer, start, len);
        return true;
    }

    // ────────────────────────── P/Invoke ──────────────────────────

    /// <summary>wlan_intf_opcode_current_connection：当前连接的属性（含 SSID）。</summary>
    private const uint CurrentConnectionOpcode = 4;

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern uint GetBestInterfaceEx(byte[] pDestinationAddress, out uint pdwBestIfIndex);

    [DllImport("wlanapi.dll")]
    private static extern uint WlanOpenHandle(uint dwClientVersion, IntPtr pReserved,
        out uint pdwNegotiatedVersion, out IntPtr phClientHandle);

    [DllImport("wlanapi.dll")]
    private static extern uint WlanCloseHandle(IntPtr hClientHandle, IntPtr pReserved);

    [DllImport("wlanapi.dll")]
    private static extern uint WlanEnumInterfaces(IntPtr hClientHandle, IntPtr pReserved,
        out IntPtr ppInterfaceList, out uint pdwNumberOfItems);

    [DllImport("wlanapi.dll")]
    private static extern uint WlanQueryInterface(IntPtr hClientHandle, ref Guid pInterfaceGuid,
        uint wlan_intf_opcode, IntPtr pReserved, out uint pdwDataSize, out IntPtr ppData,
        out uint pWlanOpcodeValueOut);

    [DllImport("wlanapi.dll")]
    private static extern void WlanFreeMemory(IntPtr pMemory);
}
