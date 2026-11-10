using System;
using System.Collections.Generic;
using System.Linq;

namespace SNNUAutoLogin.Services;

/// <summary>连接规则的匹配方式。</summary>
public enum ConnectionMatchType
{
    /// <summary>有线。</summary>
    Wired = 0,

    /// <summary>无线（任意 SSID）。</summary>
    WirelessAny = 1,

    /// <summary>无线（指定 SSID）。</summary>
    WirelessSsid = 2
}

/// <summary>
/// 一条连接规则：把「某种接入方式」和「用哪个账号、走哪个出口」绑在一起。
///
/// 密码不落在规则里 —— 规则只持有一个凭据索引（<see cref="PasswordRef"/>），
/// 真正的密文在 secrets.json 里按这个索引取。
/// </summary>
public sealed class ConnectionRule
{
    /// <summary>规则标识（GUID），同时是凭据索引键。</summary>
    public string Id { get; set; } = "";

    /// <summary>匹配方式。</summary>
    public ConnectionMatchType MatchType { get; set; } = ConnectionMatchType.Wired;

    /// <summary>仅 <see cref="ConnectionMatchType.WirelessSsid"/> 时有效。</summary>
    public string Ssid { get; set; } = "";

    /// <summary>该规则专用的账号。</summary>
    public string Account { get; set; } = "";

    /// <summary>该规则专用的门户接入端口。</summary>
    public int Port { get; set; } = 8603;

    /// <summary>关掉后不参与匹配。</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>凭据索引；为空时按 <see cref="Id"/> 取。</summary>
    public string PasswordRef { get; set; } = "";

    /// <summary>实际使用的凭据键：优先 <see cref="PasswordRef"/>，否则用 <see cref="Id"/>。</summary>
    public string CredentialKey => string.IsNullOrWhiteSpace(PasswordRef) ? Id : PasswordRef;
}

/// <summary>
/// 规则集校验与排序。
///
/// 校验是「逐条」的：一条规则不合法只丢这一条，其余规则照常生效 ——
/// 整份规则集因为一条写错就全部作废，用户体验太差。
/// </summary>
public static class ConnectionRuleSet
{
    /// <summary>规则条数上限（防止配置膨胀）。</summary>
    public const int MaxRules = 20;

    /// <summary>
    /// 校验单条规则。合法返回 null，非法返回可直接展示给用户的中文原因。
    ///
    /// <paramref name="all"/> 是「已接受的全部规则」；同一条规则自身不参与唯一性比对。
    /// </summary>
    public static string? Validate(ConnectionRule rule, IReadOnlyList<ConnectionRule> all)
    {
        if (rule is null) return "规则为空";

        if (string.IsNullOrWhiteSpace(rule.Id)) return "规则缺少标识";

        if (!Enum.IsDefined(rule.MatchType)) return "匹配方式无效";

        if (rule.MatchType == ConnectionMatchType.WirelessSsid)
        {
            string ssid = rule.Ssid?.Trim() ?? "";
            if (ssid.Length == 0) return "指定 SSID 的规则必须填写 SSID";
            if (ssid.Length > 32) return "SSID 不能超过 32 个字符";
        }
        else if (!string.IsNullOrWhiteSpace(rule.Ssid))
        {
            // 非指定 SSID 的类型不允许带 SSID —— 否则界面上的两个字段会互相矛盾
            return "只有「无线（指定 SSID）」才能填写 SSID";
        }

        if (string.IsNullOrWhiteSpace(rule.Account)) return "账号不能为空";
        if (rule.Account.Trim().Length > 64) return "账号不能超过 64 个字符";

        if (!PortalClient.IsKnownPort(rule.Port)) return $"出口端口不在可选范围（{string.Join(" / ", PortalClient.KnownPorts)}）";

        if (all is not null)
        {
            foreach (var other in all)
            {
                if (other is null) continue;
                if (string.Equals(other.Id, rule.Id, StringComparison.Ordinal)) continue;

                if (other.MatchType != rule.MatchType) continue;

                if (rule.MatchType == ConnectionMatchType.Wired)
                    return "有线只能配置一条规则";
                if (rule.MatchType == ConnectionMatchType.WirelessAny)
                    return "无线（任意 SSID）只能配置一条规则";

                // 指定 SSID：同类型内 SSID 不可重复（忽略大小写）
                string a = other.Ssid?.Trim() ?? "";
                string b = rule.Ssid?.Trim() ?? "";
                if (string.Equals(a, b, StringComparison.OrdinalIgnoreCase))
                    return $"SSID「{b}」已经有一条规则了";
            }
        }

        return null;
    }

    /// <summary>
    /// 规则集整体校验：逐条比对，非法条直接丢弃。
    /// 返回通过校验的规则集合（已去重、已截断到上限）。
    /// </summary>
    public static List<ConnectionRule> ValidateSet(IEnumerable<ConnectionRule>? rules, out List<string> dropped)
    {
        dropped = new List<string>();
        var accepted = new List<ConnectionRule>();

        if (rules is null) return accepted;

        foreach (var rule in rules)
        {
            if (accepted.Count >= MaxRules)
            {
                dropped.Add("规则条数已达上限，超出的规则未生效");
                break;
            }

            var error = Validate(rule, accepted);
            if (error is not null)
            {
                dropped.Add(error);
                continue;
            }

            accepted.Add(rule);
        }

        return accepted;
    }

    /// <summary>
    /// 排序：匹配方式（有线 → 无线任意 → 无线指定）→ SSID 字典序。
    /// 界面上的规则列表按这个顺序显示，和匹配优先级一致，看起来才不别扭。
    /// </summary>
    public static IReadOnlyList<ConnectionRule> Sorted(IEnumerable<ConnectionRule>? rules)
    {
        if (rules is null) return Array.Empty<ConnectionRule>();

        return rules
            .Where(r => r is not null)
            .OrderBy(r => (int)r.MatchType)
            .ThenBy(r => r.Ssid ?? "", StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}
