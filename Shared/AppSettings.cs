using System.Collections.Generic;

namespace SNNUAutoLogin.Services;

public sealed class AppSettings
{
    public string Account { get; set; } = "";
    public Operator Operator { get; set; } = Operator.Mobile;
    public bool AutoStart { get; set; } = true;
    public bool AutoReconnect { get; set; } = true;
    public bool MinimizeToTray { get; set; } = true;

    /// <summary>锁屏时保持在线：阻止系统因空闲进入睡眠，并在解锁 / 唤醒后立刻补认证。</summary>
    public bool KeepAliveOnLock { get; set; } = true;

    public int CheckIntervalSeconds { get; set; } = 8;

    /// <summary>设备数守卫开关。默认关闭，老用户升级后行为与从前完全一致。</summary>
    public bool DeviceGuardEnabled { get; set; } = false;

    /// <summary>设备数阈值。语义是「设备数 &gt; 阈值」才触发。</summary>
    public int DeviceGuardThreshold { get; set; } = 2;

    /// <summary>探测目标地址。IP 字面量走 ICMP，域名或 URL 走 HTTP。</summary>
    public string DeviceGuardProbeTarget { get; set; } = NetworkProbeService.DefaultTarget;

    /// <summary>探测超时（秒）。</summary>
    public int DeviceGuardProbeTimeoutSeconds { get; set; } = NetworkProbeService.DefaultTimeoutSeconds;

    /// <summary>全局默认接入端口。未命中任何连接规则时用它。</summary>
    public int DefaultPort { get; set; } = 8603;

    /// <summary>连接规则集合。默认空。</summary>
    public List<ConnectionRule> ConnectionRules { get; set; } = new();
}
