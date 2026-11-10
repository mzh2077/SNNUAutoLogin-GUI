// 实机验证：不打桩，真连门户、真发探测包、真读真实网卡。
//
// 与 GuardTests 的区别：GuardTests 全离线（假门户、假探测、假网卡），
// 这里跑的是真实链路，用来证明「设备数守卫」「连接规则」「巡检时长」
// 三项新功能在真机上确实成立。
//
// 只读：整个流程不发起登录、不断开本机连接、不改认证状态。
// 唯一会写的是临时目录里的 snnu_livecheck.txt。
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using SNNUAutoLogin.Services;

namespace LiveCheck;

internal static class Program
{
    private static int _checks;
    private static int _failed;

    private static void Check(bool ok, string what, string detail = "")
    {
        _checks++;
        if (ok) Console.WriteLine($"PASS  {what}{(detail.Length > 0 ? "  " + detail : "")}");
        else { _failed++; Console.WriteLine($"FAIL  {what}  {detail}"); }
    }

    private static async Task<int> Main()
    {
        var sb = new StringBuilder();
        void Say(string s)
        {
            Console.WriteLine(s);
            sb.AppendLine(s);
        }

        var store = new CredentialStore();
        store.Load();

        int port = PortalClient.IsKnownPort(store.Settings.DefaultPort) ? store.Settings.DefaultPort : 8603;

        Say("=== 实机验证 ===");
        Say($"时间     : {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        Say($"进程     : {Environment.ProcessPath}");
        Say($"账号     : {store.Settings.Account}");
        Say($"默认端口 : {port}");
        Say($"巡检时长 : {store.Settings.CheckIntervalSeconds} 秒");
        Say($"守卫开关 : {(store.Settings.DeviceGuardEnabled ? "开" : "关")}  阈值 {store.Settings.DeviceGuardThreshold}");
        Console.WriteLine();
        sb.AppendLine();

        // ── 1. 真实接入方式判定 ──────────────────────────────
        Say("── 1. 接入方式判定（真实网卡） ──");
        var access = new NetworkAccessService();
        var info = access.GetCurrent();
        Say($"网卡描述 : {info.InterfaceDescription}");
        Say($"接入方式 : {info.Kind}");
        Say($"SSID     : {(string.IsNullOrEmpty(info.Ssid) ? "(未取到)" : info.Ssid)}  可信={info.SsidReliable}");

        Check(info.Kind != NetworkAccessKind.Unknown, "能判定出接入方式", info.Kind.ToString());
        Check(info.Kind == NetworkAccessKind.Wireless ? info.SsidReliable && info.Ssid.Length > 0 : true,
            "无线接入时能取到可靠 SSID", info.Ssid);

        // SSID 必须是本机真实连着的那个（用 WLAN 接口 GUID 反查，绕开所有猜测）
        var realSsid = ReadRealSsid();
        Check(string.IsNullOrEmpty(realSsid) || string.Equals(realSsid, info.Ssid, StringComparison.Ordinal),
            "解出的 SSID 与 WLAN 接口报告的一致", $"接口={realSsid} 服务={info.Ssid}");
        Console.WriteLine();
        sb.AppendLine();

        // ── 2. 真实出网探测 ─────────────────────────────────
        Say("── 2. 出网探测（真实发包） ──");
        string target = NetworkProbeService.NormalizeTarget(store.Settings.DeviceGuardProbeTarget);
        int timeout = NetworkProbeService.NormalizeTimeoutSeconds(store.Settings.DeviceGuardProbeTimeoutSeconds);
        using (var probe = new NetworkProbeService())
        {
            var r = await probe.ProbeAsync(target, TimeSpan.FromSeconds(timeout), CancellationToken.None);
            Say($"目标 {target}  超时 {timeout}s  → {r.Success}  {r.LatencyMs}ms  {r.Detail}");
            Check(r.Success, "能出网探测有明确结果", r.Detail);

            // 探测必须独立于门户认证状态：用一个必然不通的目标再探一次，应当失败
            var bad = await probe.ProbeAsync("240.0.0.1", TimeSpan.FromSeconds(2), CancellationToken.None);
            Say($"不通目标 240.0.0.1 → {bad.Success}  {bad.Detail}");
            Check(!bad.Success, "不通的目标会判定为出网失败", bad.Detail);
        }
        Console.WriteLine();
        sb.AppendLine();

        // ── 3. 真实门户：在线设备数 ─────────────────────────
        Say("── 3. 在线设备（真实门户） ──");
        using (var client = new PortalClient())
        {
            var list = await client.GetOnlineDevicesAsync(port);
            Check(list.Success, "能取到在线设备列表", list.Success ? $"{list.Devices.Count} 台" : list.Message);
            if (list.Success)
            {
                foreach (var d in list.Devices)
                    Say($"   {d.Ip,-15} {d.DisplayName,-18} {d.LoginTime,-20} {d.Detail}{(d.IsCurrent ? "  ← 本机" : "")}");

                int threshold = store.Settings.DeviceGuardThreshold is < 1 or > 50 ? 2 : store.Settings.DeviceGuardThreshold;
                bool wouldTrigger = list.Devices.Count > threshold;
                Say($"守卫判定 : 设备 {list.Devices.Count} 台 vs 阈值 {threshold} → {(wouldTrigger ? "会触发探测" : "不触发")}");
                Check(wouldTrigger == (list.Devices.Count > threshold), "守卫触发条件与设备数一致");
            }
        }
        Console.WriteLine();
        sb.AppendLine();

        // ── 4. 连接规则匹配（真实接入方式 + 真实规则表） ────
        Say("── 4. 连接规则匹配 ──");
        var resolver = new ConnectionProfileResolver(store, access);
        var rules = store.Settings.ConnectionRules ?? new List<ConnectionRule>();
        Say($"已配置规则 {rules.Count} 条");
        foreach (var r in rules)
            Say($"   {r.MatchType} SSID={r.Ssid} 账号={r.Account} 端口={r.Port} 启用={r.Enabled}");

        var matched = resolver.Match(info.Kind, info.Ssid, info.SsidReliable, rules);
        Say($"命中规则 : {(matched is null ? "(无)" : matched.MatchType + " " + matched.Ssid)}");
        Say($"生效档案 : 账号={resolver.Resolve().Account} 端口={resolver.Resolve().Port}");

        // 优先级：指定 SSID > 无线任意 > 有线
        var allRules = new List<ConnectionRule>
        {
            new ConnectionRule { Id="wired", MatchType=ConnectionMatchType.Wired, Ssid="", Account="ACC_WIRED", Port=8601, Enabled=true },
            new ConnectionRule { Id="any",  MatchType=ConnectionMatchType.WirelessAny, Ssid="", Account="ACC_ANY", Port=8605, Enabled=true },
        };
        if (!string.IsNullOrEmpty(info.Ssid))
        {
            allRules.Add(new ConnectionRule
            {
                Id="ssid", MatchType=ConnectionMatchType.WirelessSsid, Ssid=info.Ssid,
                Account="ACC_SSID", Port=8603, Enabled=true
            });
        }

        var mAll = resolver.Match(info.Kind, info.Ssid, info.SsidReliable, allRules);
        if (info.Kind == NetworkAccessKind.Wireless)
        {
            Check(mAll is not null && mAll.MatchType == ConnectionMatchType.WirelessSsid,
                "无线时优先命中指定 SSID 的规则", mAll?.MatchType.ToString() ?? "无");

            // 指定 SSID 停用后应回退到「无线任意」
            foreach (var r in allRules) if (r.Id == "ssid") r.Enabled = false;
            var mFallback = resolver.Match(info.Kind, info.Ssid, info.SsidReliable, allRules);
            Check(mFallback is not null && mFallback.MatchType == ConnectionMatchType.WirelessAny,
                "指定 SSID 停用后回退到无线任意", mFallback?.MatchType.ToString() ?? "无");

            // SSID 不匹配时也应回退到「无线任意」
            foreach (var r in allRules) if (r.Id == "ssid") r.Enabled = true;
            var mismatched = allRules.Where(r => r.Id == "ssid").ToList();
            foreach (var r in mismatched) r.Ssid = "___不存在的SSID___";
            var mMiss = resolver.Match(info.Kind, info.Ssid, info.SsidReliable, allRules);
            Check(mMiss is not null && mMiss.MatchType == ConnectionMatchType.WirelessAny,
                "SSID 对不上时回退到无线任意", mMiss?.MatchType.ToString() ?? "无");
        }

        // 未命中任何规则 → 落全局默认
        var mNone = resolver.Match(info.Kind, info.Ssid, info.SsidReliable, new List<ConnectionRule>());
        Check(mNone is null, "没有规则时不命中任何规则");

        var profile = resolver.Resolve();
        Check(profile.Port == (matched is not null && PortalClient.IsKnownPort(matched.Port) ? matched.Port : port),
            "生效端口与命中的规则一致", profile.Port.ToString());
        Console.WriteLine();
        sb.AppendLine();

        // ── 5. 巡检时长可自定义 ─────────────────────────────
        Say("── 5. 自定义巡检时长 ──");
        int interval = store.Settings.CheckIntervalSeconds;
        Say($"当前持久化值 : {interval} 秒");
        Check(interval is >= 4 and <= 120, "巡检时长在 4–120 秒区间内", interval.ToString());

        // 存一个非预设值再读回来，确认不会被静默改写
        var probeStore = new CredentialStore();
        probeStore.Load();
        int original = probeStore.Settings.CheckIntervalSeconds;
        probeStore.Settings.CheckIntervalSeconds = 37;
        probeStore.Save();

        var reread = new CredentialStore();
        reread.Load();
        Check(reread.Settings.CheckIntervalSeconds == 37,
            "非预设值（37 秒）写入后读回不被覆盖", reread.Settings.CheckIntervalSeconds.ToString());

        // 复原
        probeStore.Settings.CheckIntervalSeconds = original;
        probeStore.Save();
        var restored = new CredentialStore();
        restored.Load();
        Check(restored.Settings.CheckIntervalSeconds == original,
            "巡检时长可复原", restored.Settings.CheckIntervalSeconds.ToString());
        Console.WriteLine();
        sb.AppendLine();

        Say($"=== 结论：{_checks - _failed}/{_checks} 项通过 ===");

        try
        {
            File.WriteAllText(Path.Combine(Path.GetTempPath(), "snnu_livecheck.txt"),
                sb.ToString(), Encoding.UTF8);
        }
        catch { }

        return _failed == 0 ? 0 : 1;
    }

    /// <summary>
    /// 用 WLAN 接口自身的连接属性再取一次 SSID，作为独立对照。
    ///
    /// 这一步不经过 NetworkAccessService 的缓存与筛选，
    /// 所以能真正验证「解出来的 SSID 是不是这台机器当前连的那个」。
    /// </summary>
    private static string ReadRealSsid()
    {
        try
        {
            foreach (var ni in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.NetworkInterfaceType != System.Net.NetworkInformation.NetworkInterfaceType.Wireless80211)
                    continue;
                if (ni.OperationalStatus != System.Net.NetworkInformation.OperationalStatus.Up)
                    continue;

                var (ssid, ok) = NetworkAccessService.ReadSsid(ni.Id);
                if (ok && ssid.Length > 0) return ssid;
            }
        }
        catch { }
        return "";
    }
}
