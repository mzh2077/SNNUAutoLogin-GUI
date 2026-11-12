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

            // WlanQueryInterface 的 ppData 是 out 参数：WLAN 服务总会自己分配并填充，
            // 传 NULL 只是说「不要把指针给我」。实测一次调用就能同时拿到 size 和内容，
            // 所以不需要「先问长度再取数据」跑两遍 —— 两遍会把第一次分配的内存漏掉。
            uint rc = WlanQueryInterface(client, ref guid, CurrentConnectionOpcode,
                IntPtr.Zero, out size, out data, out opcode);
            if (rc != 0 || size == 0 || data == IntPtr.Zero) return ("", false);

            var bytes = new byte[size];
            Marshal.Copy(data, bytes, 0, (int)size);

            return ParseSsid(bytes);
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
    /// WLAN_CONNECTION_ATTRIBUTES 的实际布局（本机 wlanapi 实测确认）：
    ///   isState(4) | mode(4) | profileName[256] WCHAR(512) | WLAN_ASSOCIATION_ATTRIBUTES
    ///   其中 DOT11_SSID{ uSSIDLength(4), ucSSID[1..32] } 落在偏移 520。
    ///
    /// 注意 offset 8 是 profileName 的**头**，不是 SSID 长度 —— 那里是 UTF-16 的
    /// 「m」= 6D-00-…，按长度读会得到 109，直接被 1..32 的校验挡掉。
    /// 这也是先前「拿不到 SSID」的根因：opcode 取错（4 是 radio_state），
    /// 偏移也取错（8 是 profileName 起点）。
    ///
    /// 规范偏移失败时再全缓冲扫描兜底 —— 只接受「长度合理 + 全是可打印 ASCII」的组合，
    /// 宁可取不到也不让垃圾字节变成 SSID 去参与规则匹配。
    /// </summary>
    internal static (string Ssid, bool Reliable) ParseSsid(byte[] buffer)
    {
        if (buffer is null || buffer.Length < 16) return ("", false);

        // 规范位置：DOT11_SSID.uSSIDLength
        if (TryReadSsidAt(buffer, SsidLengthOffset, out var ssid)) return (ssid, true);

        // 兜底：全缓冲扫描
        for (int off = 4; off + 4 <= buffer.Length; off += 4)
        {
            if (off == SsidLengthOffset) continue;
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

    /// <summary>
    /// wlan_intf_opcode_current_connection：当前连接的属性（含 SSID）。
    ///
    /// 注意这个值是 **7**，不是 4 —— 4 是 wlan_intf_opcode_radio_state。
    /// 本机实测：opcode 4 返回 772 字节、outOpcode=2（radio_state 的形状），
    /// 里面根本没有 SSID；opcode 7 返回 604 字节，偏移 520 处才是 DOT11_SSID。
    /// </summary>
    private const uint CurrentConnectionOpcode = 7;

    /// <summary>DOT11_SSID.uSSIDLength 在 WLAN_CONNECTION_ATTRIBUTES 缓冲里的偏移。</summary>
    private const int SsidLengthOffset = 520;

    /// <summary>
    /// WLAN 原始探针：把 wlanapi 返回的连接属性缓冲原样打印出来。
    ///
    /// 只做诊断用 —— 结构布局在不同 Windows 版本上不一致，必须看原始字节才能定
    /// SSID 到底在哪个偏移，而不是靠猜。
    /// </summary>
    internal static string DumpWlanBuffer()
    {
        var sb = new StringBuilder();

        IntPtr client = IntPtr.Zero;
        IntPtr list = IntPtr.Zero;
        IntPtr data = IntPtr.Zero;

        try
        {
            uint negotiated;
            uint rc = WlanOpenHandle(2, IntPtr.Zero, out negotiated, out client);
            sb.AppendLine($"WlanOpenHandle rc={rc} negotiated={negotiated}");
            if (rc != 0) return sb.ToString();

            uint count;
            rc = WlanEnumInterfaces(client, IntPtr.Zero, out list, out count);
            sb.AppendLine($"WlanEnumInterfaces rc={rc} count(out)={count}");

            int items = Marshal.ReadInt32(list, 0);
            sb.AppendLine($"链表头条数={items}");
            if (items <= 0) return sb.ToString();

            const int headerSize = 8;
            const int itemSize = 532;

            for (int i = 0; i < items; i++)
            {
                IntPtr item = list + headerSize + i * itemSize;
                var guidBytes = new byte[16];
                Marshal.Copy(item, guidBytes, 0, 16);
                var guid = new Guid(guidBytes);

                sb.AppendLine($"── 接口[{i}] GUID={guid}");

                // 枚举描述（偏移 16 起，256 个 WCHAR）
                var desc = new byte[512];
                Marshal.Copy(item + 16, desc, 0, 512);
                sb.AppendLine($"   描述={Encoding.Unicode.GetString(desc).TrimEnd('\0')}");

                // 枚举 isState（偏移 16+512）
                sb.AppendLine($"   isState={Marshal.ReadInt32(item, 16 + 512)}");

                DumpOpcode(client, guid, CurrentConnectionOpcode, "current_connection(7)", sb);
                DumpOpcode(client, guid, 4, "radio_state(4)", sb);
                DumpOpcode(client, guid, 1, "interface_state", sb);
            }

            return sb.ToString();
        }
        catch (Exception ex)
        {
            sb.AppendLine($"异常: {ex.GetType().Name} {ex.Message}");
            return sb.ToString();
        }
        finally
        {
            if (data != IntPtr.Zero) { try { WlanFreeMemory(data); } catch { } }
            if (client != IntPtr.Zero) { try { WlanCloseHandle(client, IntPtr.Zero); } catch { } }
        }
    }

    /// <summary>查询一个 opcode，并把返回缓冲的十六进制与可打印字符原样列出。</summary>
    private static void DumpOpcode(IntPtr client, Guid guid, uint opcode, string label, StringBuilder sb)
    {
        IntPtr data = IntPtr.Zero;
        try
        {
            uint size;
            uint outOpcode;

            // 第一遍：只要长度
            uint rc = WlanQueryInterface(client, ref guid, opcode, IntPtr.Zero, out size, out data, out outOpcode);
            sb.AppendLine($"   [{label}] 第一遍 rc={rc} size={size}");
            if (rc != 0 || size == 0)
            {
                if (data != IntPtr.Zero) { try { WlanFreeMemory(data); } catch { } }
                data = IntPtr.Zero;
                return;
            }

            // 第二遍：自己分配缓冲，交给 API 去填
            IntPtr buffer = Marshal.AllocHGlobal((int)size);
            try
            {
                IntPtr ppData = buffer;
                uint size2 = size;
                rc = WlanQueryInterface(client, ref guid, opcode, IntPtr.Zero, out size2, out ppData, out outOpcode);
                sb.AppendLine($"   [{label}] 第二遍(自带缓冲) rc={rc} size={size2} outOpcode={outOpcode} ppData={(ppData == buffer ? "=buffer" : "≠buffer")}");

                if (rc != 0) return;

                var bytes = new byte[(int)size2];
                Marshal.Copy(ppData, bytes, 0, (int)size2);

                sb.AppendLine($"   [{label}] 十六进制（前 64 字节）:");
                sb.AppendLine($"      {BitConverter.ToString(bytes, 0, Math.Min(64, bytes.Length))}");

                sb.AppendLine($"   [{label}] 按长度字段扫描（找 uSSIDLength=1..32 的位置）:");
                for (int off = 0; off + 4 <= bytes.Length; off += 4)
                {
                    int len = BitConverter.ToInt32(bytes, off);
                    if (len is < 1 or > 32) continue;
                    if (off + 4 + len > bytes.Length) continue;
                    bool printable = true;
                    for (int k = 0; k < len; k++)
                    {
                        byte b = bytes[off + 4 + k];
                        if (b < 0x20 || b > 0x7E) { printable = false; break; }
                    }
                    if (!printable) continue;
                    sb.AppendLine($"      offset {off}: len={len} value=\"{Encoding.ASCII.GetString(bytes, off + 4, len)}\"");
                }

                sb.AppendLine($"   [{label}] 全部可打印 ASCII 片段（长度>=3）:");
                {
                    int run = 0;
                    for (int k = 0; k < bytes.Length; k++)
                    {
                        byte b = bytes[k];
                        if (b >= 0x20 && b <= 0x7E) { run++; continue; }
                        if (run >= 3) sb.AppendLine($"      offset {k - run}: \"{Encoding.ASCII.GetString(bytes, k - run, run)}\"");
                        run = 0;
                    }
                    if (run >= 3) sb.AppendLine($"      offset {bytes.Length - run}: \"{Encoding.ASCII.GetString(bytes, bytes.Length - run, run)}\"");
                }

                var (ssid, ok) = ParseSsid(bytes);
                sb.AppendLine($"   [{label}] ParseSsid -> ssid=\"{ssid}\" reliable={ok}");
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
        catch (Exception ex)
        {
            sb.AppendLine($"   [{label}] 异常 {ex.GetType().Name} {ex.Message}");
        }
        finally
        {
            if (data != IntPtr.Zero) { try { WlanFreeMemory(data); } catch { } }
        }
    }

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
