using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace SNNUAutoLogin.Services;

/// <summary>当前生效的登录档案。</summary>
public sealed record EffectiveProfile
{
    /// <summary>生效账号。</summary>
    public string Account { get; init; } = "";

    /// <summary>生效密码（规则密码或全局密码）。</summary>
    public string Password { get; init; } = "";

    /// <summary>
    /// 生效接入端口。
    ///
    /// 注意：这不是用户选出来的，而是门户按线路分配的 ——
    /// 由 <see cref="IPortalPortDiscovery"/> 在运行时发现。默认值 8603 只是兜底。
    /// </summary>
    public int Port { get; init; } = 8603;

    /// <summary>生效运营商出口。</summary>
    public Operator Operator { get; init; } = Operator.Mobile;

    /// <summary>命中的规则；null 表示未命中任何规则。</summary>
    public ConnectionRule? Rule { get; init; }

    /// <summary>是否走了全局默认（未命中规则）。</summary>
    public bool IsDefault { get; init; } = true;
}

/// <summary>
/// 门户接入端口的发现者。
///
/// 端口是服务端按接入线路临时分配的（访问门户根路径会回一段 JS 指向具体端口），
/// 所以它只能被「读出来」，不能被「选出来」。
/// </summary>
public interface IPortalPortDiscovery
{
    /// <summary>取门户当前分配的接入端口；拿不到时返回 null。</summary>
    Task<int?> DiscoverAsync(CancellationToken ct = default);

    /// <summary>已发现的端口（还没发现过就是 null）。同步读取，供界面显示。</summary>
    int? CurrentPort { get; }

    /// <summary>换线路 / 重连后作废缓存。</summary>
    void Invalidate();
}

/// <summary>规则匹配与生效档案解析。</summary>
public interface IConnectionProfileResolver
{
    /// <summary>解析当前生效档案（账号 / 密码 / 端口）。</summary>
    EffectiveProfile Resolve();

    /// <summary>记录门户刚刚分配的接入端口（由端口发现服务调用）。</summary>
    void SetDiscoveredPort(int port);

    /// <summary>
    /// 纯匹配函数：按优先级选出唯一命中的规则。
    /// 没有 IO、没有全局状态，单元测试可以直接覆盖各种分支。
    /// </summary>
    ConnectionRule? Match(
        NetworkAccessKind kind,
        string ssid,
        bool ssidReliable,
        IReadOnlyList<ConnectionRule> rules);
}

/// <summary>
/// 生效档案解析器。
///
/// 匹配优先级是固定的：无线（指定 SSID） &gt; 无线（任意 SSID） &gt; 有线。
/// 同一时刻只允许命中一条 —— 规则集的唯一性校验已经保证有线的、无线任意的各只有一条，
/// 指定 SSID 的多条之间靠 SSID 区分。
/// </summary>
public sealed class ConnectionProfileResolver : IConnectionProfileResolver
{
    private readonly ICredentialSource _store;
    private readonly INetworkAccessService _access;
    private readonly IPortalPortDiscovery? _ports;
    private readonly bool _allowDefaultRulePassword;

    /// <summary>门户最近一次分配的接入端口（0 表示还没发现过）。</summary>
    private int _discoveredPort;

    public ConnectionProfileResolver(ICredentialSource store, INetworkAccessService access)
        : this(store, access, null)
    {
    }

    /// <param name="ports">
    /// 端口发现者。端口是门户分配的（不是用户选的），所以由它来告诉程序该走哪个端口。
    /// 没有它时退回配置里的兜底端口。
    /// </param>
    public ConnectionProfileResolver(
        ICredentialSource store,
        INetworkAccessService access,
        IPortalPortDiscovery? ports,
        bool allowDefaultRulePassword = true)
    {
        _store = store;
        _access = access;
        _ports = ports;
        _allowDefaultRulePassword = allowDefaultRulePassword;
    }

    /// <inheritdoc />
    public void SetDiscoveredPort(int port)
    {
        System.Threading.Volatile.Write(ref _discoveredPort, port);
    }

    public ConnectionRule? Match(
        NetworkAccessKind kind,
        string ssid,
        bool ssidReliable,
        IReadOnlyList<ConnectionRule> rules)
    {
        if (rules is null || rules.Count == 0) return null;

        // 判定不出接入方式时，不命中任何规则 —— 宁可走全局默认，也不要瞎猜一条
        if (kind == NetworkAccessKind.Unknown) return null;

        bool wireless = kind == NetworkAccessKind.Wireless;
        string want = (ssid ?? "").Trim();

        // 1) 无线（指定 SSID）优先级最高
        if (wireless && ssidReliable && want.Length > 0)
        {
            foreach (var r in rules)
            {
                if (r is null || !r.Enabled) continue;
                if (r.MatchType != ConnectionMatchType.WirelessSsid) continue;

                if (string.Equals(r.Ssid?.Trim(), want, StringComparison.OrdinalIgnoreCase))
                    return r;
            }
        }

        // 2) 无线（任意 SSID）
        if (wireless)
        {
            foreach (var r in rules)
            {
                if (r is null || !r.Enabled) continue;
                if (r.MatchType == ConnectionMatchType.WirelessAny)
                    return r;
            }
        }

        // 3) 有线
        if (!wireless)
        {
            foreach (var r in rules)
            {
                if (r is null || !r.Enabled) continue;
                if (r.MatchType == ConnectionMatchType.Wired)
                    return r;
            }
        }

        return null;
    }

    public EffectiveProfile Resolve()
    {
        var settings = _store.Settings;

        var access = _access.GetCurrent();
        var rule = Match(access.Kind, access.Ssid, access.SsidReliable, settings.ConnectionRules);

        // 端口是门户分配的，跟规则无关；这里只是兜底默认值
        int defaultPort = PortalClient.IsKnownPort(settings.DefaultPort) ? settings.DefaultPort : 8603;

        // 已发现的门户端口优先 —— 那是服务端真正分配的接入点
        int discovered = _ports is null ? System.Threading.Volatile.Read(ref _discoveredPort) : _ports.CurrentPort ?? 0;
        int port = discovered > 0 ? discovered : defaultPort;

        if (rule is null)
        {
            // 未命中任何规则：与升级前的行为完全一致
            return new EffectiveProfile
            {
                Account = settings.Account ?? "",
                Password = _store.LoadPassword(),
                Port = port,
                Operator = settings.Operator,
                Rule = null,
                IsDefault = true
            };
        }

        string key = rule.CredentialKey;
        string password = _store.LoadRulePassword(key);

        // 规则没配密码时退回全局密码 —— 宁可少一次输入，也不能让登录直接失败
        if (_allowDefaultRulePassword && string.IsNullOrEmpty(password)) password = _store.LoadPassword();

        return new EffectiveProfile
        {
            Account = rule.Account,
            Password = password,
            Port = port,
            Operator = rule.Operator,
            Rule = rule,
            IsDefault = false
        };
    }
}
