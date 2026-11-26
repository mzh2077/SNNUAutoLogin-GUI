using System;

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

    /// <summary>系统拒绝了本次 SSID 查询。</summary>
    public bool SsidAccessDenied { get; init; }

    /// <summary>命中的网卡描述，用于日志与界面自述。</summary>
    public string InterfaceDescription { get; init; } = "";

    /// <summary>判定时间（用于缓存）。</summary>
    public DateTime At { get; init; }
}

/// <summary>接入方式识别服务。抽成接口是为了能在单元测试里换成假替身，不去真读网卡。</summary>
public interface INetworkAccessService
{
    /// <summary>取当前接入方式（带短 TTL 缓存）。返回值的 SSID 可能为空，不要依赖它。</summary>
    NetworkAccessInfo GetCurrent();

    /// <summary>网络状态变化时调用，下次读取会重新判定。</summary>
    void Invalidate();

    /// <summary>
    /// 取当前接入方式，并尽量带上 SSID。
    ///
    /// 读 SSID 要经过 WLAN 服务（安全软件会把它记成「读取无线/位置信息」），
    /// 所以只有真正需要 SSID 的调用方才用这个方法：按 SSID 匹配规则、界面展示接入方式、诊断输出。
    /// 默认实现退回 <see cref="GetCurrent"/>，旧实现与测试替身不必改动。
    /// </summary>
    NetworkAccessInfo GetCurrentWithSsid() => GetCurrent();
}
