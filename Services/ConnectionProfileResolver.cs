using System;
using System.Collections.Generic;

namespace SNNUAutoLogin.Services;

/// <summary>当前生效的登录档案。</summary>
public sealed record EffectiveProfile
{
    /// <summary>生效账号。</summary>
    public string Account { get; init; } = "";

    /// <summary>生效密码（规则密码或全局密码）。</summary>
    public string Password { get; init; } = "";

    /// <summary>生效接入端口。</summary>
    public int Port { get; init; } = 8603;

    /// <summary>生效运营商出口（始终取全局设置，规则不覆盖）。</summary>
    public Operator Operator { get; init; } = Operator.Mobile;

    /// <summary>命中的规则；null 表示未命中任何规则。</summary>
    public ConnectionRule? Rule { get; init; }

    /// <summary>是否走了全局默认（未命中规则）。</summary>
    public bool IsDefault { get; init; } = true;
}

/// <summary>规则匹配与生效档案解析。</summary>
public interface IConnectionProfileResolver
{
    /// <summary>解析当前生效档案（账号 / 密码 / 端口）。</summary>
    EffectiveProfile Resolve();

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
    private readonly CredentialStore _store;
    private readonly INetworkAccessService _access;

    public ConnectionProfileResolver(CredentialStore store, INetworkAccessService access)
    {
        _store = store;
        _access = access;
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

        int defaultPort = PortalClient.IsKnownPort(settings.DefaultPort) ? settings.DefaultPort : 8603;

        if (rule is null)
        {
            // 未命中任何规则：与升级前的行为完全一致
            return new EffectiveProfile
            {
                Account = settings.Account ?? "",
                Password = _store.LoadPassword(),
                Port = defaultPort,
                Operator = settings.Operator,
                Rule = null,
                IsDefault = true
            };
        }

        string key = rule.CredentialKey;
        string password = _store.LoadRulePassword(key);

        // 规则没配密码时退回全局密码 —— 宁可少一次输入，也不能让登录直接失败
        if (string.IsNullOrEmpty(password)) password = _store.LoadPassword();

        return new EffectiveProfile
        {
            Account = rule.Account,
            Password = password,
            Port = PortalClient.IsKnownPort(rule.Port) ? rule.Port : defaultPort,
            Operator = settings.Operator,
            Rule = rule,
            IsDefault = false
        };
    }
}
