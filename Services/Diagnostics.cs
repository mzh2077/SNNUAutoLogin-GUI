using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace SNNUAutoLogin.Services;

/// <summary>命令行自检：只读地探测 Portal 可达性与当前出口认证状态，用于排障。</summary>
public static class Diagnostics
{
    public static string Run()
    {
        var sb = new StringBuilder();
        sb.AppendLine("=== SNNUAutoLogin 诊断 ===");
        sb.AppendLine($"时间      : {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine($"系统      : {Environment.OSVersion}");
        sb.AppendLine($"运行时    : {Environment.Version}");
        sb.AppendLine($"进程架构  : {Environment.ProcessPath}");
        sb.AppendLine();

        var store = new CredentialStore();
        store.Load();
        sb.AppendLine($"[配置] 账号        : {(string.IsNullOrWhiteSpace(store.Settings.Account) ? "(未设置)" : store.Settings.Account)}");
        sb.AppendLine($"[配置] 出口        : {store.Settings.Operator}");
        sb.AppendLine($"[配置] 密码已保存  : {(string.IsNullOrEmpty(store.LoadPassword()) ? "否" : "是")}");
        sb.AppendLine($"[配置] 开机自启    : {(StartupManager.IsEnabled() ? "已注册" : "未注册")}");
        sb.AppendLine();

        using var client = new PortalClient();

        int port = PortalClient.IsKnownPort(store.Settings.DefaultPort) ? store.Settings.DefaultPort : 8603;
        sb.AppendLine($"[配置] 接入端口    : {port}");

        try
        {
            bool reachable = client.IsPortalReachableAsync(port).GetAwaiter().GetResult();
            sb.AppendLine($"[网络] Portal 可达 : {(reachable ? "是" : "否")}");
            if (!reachable)
            {
                sb.AppendLine("       结论：当前不在校园网环境，或 Portal 服务不可达。");
                return sb.ToString();
            }
        }
        catch (Exception ex)
        {
            sb.AppendLine($"[网络] Portal 探测异常: {ex.GetType().Name} {ex.Message}");
            return sb.ToString();
        }

        try
        {
            var status = client.GetStatusAsync(port).GetAwaiter().GetResult();
            sb.AppendLine($"[状态] 是否在线    : {(status.Success ? "在线" : "离线")}");
            sb.AppendLine($"[状态] 账号        : {status.Account ?? "—"}");
            sb.AppendLine($"[状态] IP          : {status.Ip ?? "—"}");
            sb.AppendLine($"[状态] 登录时间    : {status.LoginTime ?? "—"}");
            sb.AppendLine($"[状态] 原始信息    : {status.Message}");
        }
        catch (Exception ex)
        {
            sb.AppendLine($"[状态] 异常: {ex.GetType().Name} {ex.Message}");
        }

        sb.AppendLine();
        sb.AppendLine("诊断完成（只读，未改动任何认证状态）");
        return sb.ToString();
    }

    public static string WriteReport()
    {
        var text = Run();
        var path = Path.Combine(Path.GetTempPath(), "snnu_diag.txt");
        try { File.WriteAllText(path, text, Encoding.UTF8); } catch { }
        return path;
    }

    /// <summary>
    /// 出网探测自检：按当前设置做一次独立探测，确认「这台机器现在到底能不能出网」。
    ///
    /// 这条路径不碰门户、不改认证状态，只发一次 ICMP 或 HTTP 请求。
    /// 用来验证「设备数守卫」的探测目标填对没有。
    /// </summary>
    public static string RunProbe()
    {
        var sb = new StringBuilder();

        var store = new CredentialStore();
        store.Load();

        string target = NetworkProbeService.NormalizeTarget(store.Settings.DeviceGuardProbeTarget);
        int seconds = NetworkProbeService.NormalizeTimeoutSeconds(store.Settings.DeviceGuardProbeTimeoutSeconds);
        var transport = NetworkProbeService.TransportOf(target);

        sb.AppendLine("=== SNNUAutoLogin 出网探测 ===");
        sb.AppendLine($"目标    : {target}（{(transport == ProbeTransport.Icmp ? "ICMP" : "HTTP")}）");
        sb.AppendLine($"超时    : {seconds} 秒");

        try
        {
            using var probe = new NetworkProbeService();
            var r = probe.ProbeAsync(target, TimeSpan.FromSeconds(seconds), CancellationToken.None)
                        .GetAwaiter().GetResult();

            sb.AppendLine($"结果    : {(r.Success ? "能出网" : "出网失败")}");
            sb.AppendLine($"耗时    : {r.LatencyMs} ms");
            sb.AppendLine($"详情    : {r.Detail}");
        }
        catch (Exception ex)
        {
            sb.AppendLine($"结果    : 探测异常 {ex.GetType().Name} {ex.Message}");
        }

        var text = sb.ToString();
        var path = Path.Combine(Path.GetTempPath(), "snnu_probe.txt");
        try { File.WriteAllText(path, text, Encoding.UTF8); } catch { }

        Console.WriteLine(text);
        return path;
    }

    /// <summary>
    /// 在线设备列表自检：只读地取一次在线设备表，打印设备数与明细。
    ///
    /// 「设备数守卫」判定的就是这张表的行数，所以这个入口是守卫触发条件的
    /// 事实依据 —— 用户可以直接看到「守卫认为有 N 台」的 N 从哪来。
    /// 全程只读，不改任何认证状态。
    /// </summary>
    public static string RunDevices()
    {
        var sb = new StringBuilder();

        var store = new CredentialStore();
        store.Load();

        int port = PortalClient.IsKnownPort(store.Settings.DefaultPort) ? store.Settings.DefaultPort : 8603;
        int threshold = store.Settings.DeviceGuardThreshold is < 1 or > 50 ? 2 : store.Settings.DeviceGuardThreshold;
        bool guardOn = store.Settings.DeviceGuardEnabled;

        sb.AppendLine("=== SNNUAutoLogin 在线设备 ===");
        sb.AppendLine($"时间      : {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine($"统计端口  : {port}");
        sb.AppendLine($"守卫开关  : {(guardOn ? "已开启" : "已关闭")}");
        sb.AppendLine($"设备阈值  : {threshold}");
        sb.AppendLine();

        using var client = new PortalClient();

        DeviceListResult list;
        try
        {
            list = client.GetOnlineDevicesAsync(port).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            sb.AppendLine($"[设备] 查询异常: {ex.GetType().Name} {ex.Message}");
            WriteAndReturn(sb, "snnu_devices.txt");
            return Path.Combine(Path.GetTempPath(), "snnu_devices.txt");
        }

        if (!list.Success)
        {
            sb.AppendLine($"[设备] 查询失败: {list.Message}");
            sb.AppendLine("       说明：取不到列表不等于「0 台」，守卫会跳过本轮而不是误判。");
            WriteAndReturn(sb, "snnu_devices.txt");
            return Path.Combine(Path.GetTempPath(), "snnu_devices.txt");
        }

        sb.AppendLine($"[设备] 在线设备数: {list.Devices.Count}");

        int i = 1;
        foreach (var d in list.Devices)
        {
            sb.AppendLine($"  {i,2}. {d.Ip,-15} {d.DisplayName,-18} {d.LoginTime,-20} {d.Detail}"
                        + (d.IsCurrent ? "   ← 本机" : ""));
            i++;
        }

        sb.AppendLine();
        if (!guardOn)
        {
            sb.AppendLine("守卫结论：守卫已关闭，不做任何检测与处置。");
        }
        else if (list.Devices.Count <= threshold)
        {
            sb.AppendLine($"守卫结论：设备 {list.Devices.Count} 台 ≤ 阈值 {threshold}，不触发检测。");
        }
        else
        {
            sb.AppendLine($"守卫结论：设备 {list.Devices.Count} 台 > 阈值 {threshold}，"
                        + "会触发一次出网探测；探测通过则不处置，失败则断开本机连接并重新登录。");
        }

        WriteAndReturn(sb, "snnu_devices.txt");
        return Path.Combine(Path.GetTempPath(), "snnu_devices.txt");
    }

    /// <summary>
    /// 连接规则自检：判定当前接入方式，并按规则优先级算出实际生效的账号与出口端口。
    ///
    /// 只读，不发起任何认证请求。
    /// </summary>
    public static string RunRules()
    {
        var sb = new StringBuilder();

        var store = new CredentialStore();
        store.Load();

        sb.AppendLine("=== SNNUAutoLogin 连接规则 ===");
        sb.AppendLine($"时间      : {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine();

        var access = new NetworkAccessService();
        var info = access.GetCurrent();

        sb.AppendLine($"[接入方式] 类型      : {info.Kind}"
                    + (info.Kind == NetworkAccessKind.Wireless ? "（无线）" : info.Kind == NetworkAccessKind.Wired ? "（有线）" : "（未判定）"));
        sb.AppendLine($"[接入方式] SSID      : {(string.IsNullOrEmpty(info.Ssid) ? "(未取到)" : info.Ssid)}");
        sb.AppendLine($"[接入方式] SSID 可信 : {(info.SsidReliable ? "是" : "否（将降级为「无线任意」，不匹配指定 SSID 的规则）")}");
        sb.AppendLine($"[接入方式] 网卡      : {info.InterfaceDescription}");
        sb.AppendLine();

        var rules = store.Settings.ConnectionRules;
        sb.AppendLine($"[规则] 已配置 {rules?.Count ?? 0} 条：");
        if (rules is not null)
        {
            foreach (var r in rules)
            {
                if (r is null) continue;
                string scope = r.MatchType switch
                {
                    ConnectionMatchType.Wired => "有线",
                    ConnectionMatchType.WirelessAny => "无线（任意 SSID）",
                    ConnectionMatchType.WirelessSsid => $"无线 SSID={r.Ssid}",
                    _ => "未知"
                };
                sb.AppendLine($"  - {scope,-24} 账号={r.Account,-12} 端口={r.Port} 启用={r.Enabled}");
            }
        }
        sb.AppendLine();

        var resolver = new ConnectionProfileResolver(store, access);
        var matched = resolver.Match(info.Kind, info.Ssid, info.SsidReliable, rules ?? new List<ConnectionRule>());
        var profile = resolver.Resolve();

        sb.AppendLine($"[匹配] 命中规则      : {(matched is null ? "（无，落到全局默认）" : DescribeRule(matched))}");
        sb.AppendLine($"[生效] 账号          : {profile.Account}");
        sb.AppendLine($"[生效] 出口端口      : {profile.Port}");
        sb.AppendLine($"[生效] 密码来源      : {(profile.Rule is null ? "全局密码" : (string.IsNullOrEmpty(store.LoadRulePassword(profile.Rule.CredentialKey)) ? "规则未设密码 → 回退全局密码" : "该规则独立密码"))}");
        sb.AppendLine($"[生效] 是否全局默认  : {(profile.IsDefault ? "是" : "否")}");

        WriteAndReturn(sb, "snnu_rules.txt");
        return Path.Combine(Path.GetTempPath(), "snnu_rules.txt");
    }

    /// <summary>
    /// WLAN 原始探针：打印 wlanapi 返回的连接属性缓冲，用于定位 SSID 的真实偏移。
    /// 只读，不改任何状态。
    /// </summary>
    public static string RunWlan()
    {
        var sb = new StringBuilder();
        sb.AppendLine("=== SNNUAutoLogin WLAN 探针 ===");
        sb.AppendLine($"时间 : {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine();
        sb.Append(NetworkAccessService.DumpWlanBuffer());
        WriteAndReturn(sb, "snnu_wlan.txt");
        return Path.Combine(Path.GetTempPath(), "snnu_wlan.txt");
    }

    private static string DescribeRule(ConnectionRule r)    {
        string scope = r.MatchType switch
        {
            ConnectionMatchType.Wired => "有线",
            ConnectionMatchType.WirelessAny => "无线（任意 SSID）",
            ConnectionMatchType.WirelessSsid => $"无线 SSID={r.Ssid}",
            _ => "未知"
        };
        return $"{scope} → 账号 {r.Account} / 端口 {r.Port}";
    }

    private static void WriteAndReturn(StringBuilder sb, string fileName)
    {
        var text = sb.ToString();
        var path = Path.Combine(Path.GetTempPath(), fileName);
        try { File.WriteAllText(path, text, Encoding.UTF8); } catch { }
        Console.WriteLine(text);
    }
}
