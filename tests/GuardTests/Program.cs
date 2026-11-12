using System.Net.NetworkInformation;
using SNNUAutoLogin.Services;

int checks = 0;
int failed = 0;

void Check(bool condition, string name, string detail = "")
{
    checks++;
    if (condition)
    {
        Console.WriteLine("PASS " + name);
    }
    else
    {
        failed++;
        Console.WriteLine("FAIL " + name + (detail.Length > 0 ? "  → " + detail : ""));
    }
}

string root = Path.Combine(AppContext.BaseDirectory, "SNNU-GuardTests-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);

try
{
    // ══════════════ 规则校验 ══════════════
    Console.WriteLine("\n规则校验");

    var wired = new ConnectionRule { Id = "w1", MatchType = ConnectionMatchType.Wired, Account = "a", Operator = Operator.Campus };
    var wired2 = new ConnectionRule { Id = "w2", MatchType = ConnectionMatchType.Wired, Account = "b", Operator = Operator.Campus };
    var any = new ConnectionRule { Id = "a1", MatchType = ConnectionMatchType.WirelessAny, Account = "a", Operator = Operator.Campus };
    var any2 = new ConnectionRule { Id = "a2", MatchType = ConnectionMatchType.WirelessAny, Account = "b", Operator = Operator.Campus };
    var ssid1 = new ConnectionRule { Id = "s1", MatchType = ConnectionMatchType.WirelessSsid, Ssid = "校园网", Account = "a", Operator = Operator.Campus };
    var ssid2 = new ConnectionRule { Id = "s2", MatchType = ConnectionMatchType.WirelessSsid, Ssid = "校园网", Account = "b", Operator = Operator.Campus };
    var ssidUpper = new ConnectionRule { Id = "s3", MatchType = ConnectionMatchType.WirelessSsid, Ssid = "校园网", Account = "c", Operator = Operator.Campus };

    Check(ConnectionRuleSet.Validate(wired, new List<ConnectionRule>()) is null, "有线规则合法");
    Check(ConnectionRuleSet.Validate(wired2, new List<ConnectionRule> { wired }) is not null, "有线只能一条");
    Check(ConnectionRuleSet.Validate(any2, new List<ConnectionRule> { any }) is not null, "无线（任意）只能一条");
    Check(ConnectionRuleSet.Validate(ssid2, new List<ConnectionRule> { ssid1 }) is not null, "同一 SSID 不能重复");
    Check(ConnectionRuleSet.Validate(ssidUpper, new List<ConnectionRule> { ssid1 }) is not null, "SSID 重复忽略大小写");
    Check(ConnectionRuleSet.Validate(new ConnectionRule { Id = "x", MatchType = ConnectionMatchType.Wired, Account = "", Operator = Operator.Campus },
        new List<ConnectionRule>()) is not null, "账号为空不合法");
    Check(ConnectionRuleSet.Validate(new ConnectionRule { Id = "x", MatchType = ConnectionMatchType.Wired, Account = "a", Operator = (Operator)99 },
        new List<ConnectionRule>()) is not null, "出口无效不合法");
    Check(ConnectionRuleSet.Validate(new ConnectionRule { Id = "x", MatchType = ConnectionMatchType.WirelessSsid, Account = "a", Operator = Operator.Campus },
        new List<ConnectionRule>()) is not null, "指定 SSID 必须填 SSID");
    Check(ConnectionRuleSet.Validate(new ConnectionRule { Id = "x", MatchType = ConnectionMatchType.WirelessAny, Ssid = "校园网", Account = "a", Operator = Operator.Campus },
        new List<ConnectionRule>()) is not null, "非指定类型不能带 SSID");

    var mixed = ConnectionRuleSet.ValidateSet(new List<ConnectionRule> { wired, wired2, ssid1 }, out var dropped);
    Check(mixed.Count == 2, "非法规则只丢单条，其余保留");
    Check(dropped.Count == 1, "丢弃的规则有说明");

    var sorted = ConnectionRuleSet.Sorted(new List<ConnectionRule> { ssid1, any, wired });
    Check(sorted.Count == 3 && sorted[0].MatchType == ConnectionMatchType.Wired
          && sorted[1].MatchType == ConnectionMatchType.WirelessAny
          && sorted[2].MatchType == ConnectionMatchType.WirelessSsid, "排序：有线 → 无线任意 → 无线指定");

    // ══════════════ 匹配优先级 ══════════════
    Console.WriteLine("\n匹配优先级");

    var store = new CredentialStore(root);
    store.Load();
    store.Settings.Account = "global-account";
    store.Settings.DefaultPort = 8603;
    store.Save();
    store.SavePassword("global-password");

    var access = new FakeNetworkAccess();
    var resolver = new ConnectionProfileResolver(store, access);

    var ruleWired = new ConnectionRule { Id = "rw", MatchType = ConnectionMatchType.Wired, Account = "wired-acc", Operator = Operator.Campus };
    var ruleAny = new ConnectionRule { Id = "ra", MatchType = ConnectionMatchType.WirelessAny, Account = "any-acc", Operator = Operator.Mobile };
    var ruleSsid = new ConnectionRule { Id = "rs", MatchType = ConnectionMatchType.WirelessSsid, Ssid = "校园网", Account = "ssid-acc", Operator = Operator.Telecom };

    var all = new List<ConnectionRule> { ruleWired, ruleAny, ruleSsid };

    Check(resolver.Match(NetworkAccessKind.Wireless, "校园网", true, all)?.Id == "rs", "指定 SSID 优先于无线任意");
    Check(resolver.Match(NetworkAccessKind.Wireless, "别的网", true, all)?.Id == "ra", "无线任意命中");
    Check(resolver.Match(NetworkAccessKind.Wired, "", false, all)?.Id == "rw", "有线命中");
    Check(resolver.Match(NetworkAccessKind.Wireless, "校园网", false, all)?.Id == "ra", "SSID 不可靠时不命中指定 SSID");
    Check(resolver.Match(NetworkAccessKind.Unknown, "", false, all) is null, "判定不出接入方式时不命中任何规则");

    var disabled = new ConnectionRule { Id = "rd", MatchType = ConnectionMatchType.Wired, Account = "x", Operator = Operator.Campus, Enabled = false };
    Check(resolver.Match(NetworkAccessKind.Wired, "", false, new List<ConnectionRule> { disabled }) is null, "停用的规则不参与匹配");

    // ══════════════ 生效档案 ══════════════
    Console.WriteLine("\n生效档案");

    var empty = resolver.Resolve();
    Check(empty.IsDefault && empty.Account == "global-account" && empty.Port == 8603, "未命中规则时用全局默认");
    Check(empty.Password == "global-password", "未命中规则时用全局密码");
    Check(empty.Operator == Operator.Mobile, "未命中规则时用全局出口");

    store.Settings.ConnectionRules = new List<ConnectionRule> { ruleSsid };
    store.Save();
    access.Kind = NetworkAccessKind.Wireless;
    access.Ssid = "校园网";
    access.SsidReliable = true;

    var hit = resolver.Resolve();
    Check(!hit.IsDefault && hit.Account == "ssid-acc", "命中规则时用规则的账号");
    Check(hit.Operator == Operator.Telecom, "命中规则时用规则的出口");
    Check(hit.Port == 8603, "命中规则时端口仍是门户分配的那个，不受规则影响");
    Check(hit.Rule?.Id == "rs", "生效档案带上了命中的规则");

    // 规则命中时若该规则没设密码，回落到全局密码
    store.Settings.ConnectionRules = new List<ConnectionRule> { ruleSsid };
    store.Save();
    var noPwd = resolver.Resolve();
    Check(noPwd.Password == "global-password", "规则没设密码时回落到全局密码");

    store.Settings.ConnectionRules = new List<ConnectionRule>();
    store.Save();

    // ══════════════ 探测目标与协议 ══════════════
    Console.WriteLine("\n探测");

    Check(NetworkProbeService.TransportOf("223.5.5.5") == ProbeTransport.Icmp, "IP 目标走 ICMP");
    Check(NetworkProbeService.TransportOf("example.com") == ProbeTransport.Http, "域名目标走 HTTP");
    Check(NetworkProbeService.TransportOf("http://example.com/x") == ProbeTransport.Http, "网址目标走 HTTP");
    Check(NetworkProbeService.DefaultTarget == "www.snnu.edu.cn", "默认探测目标是学校官网");
    Check(NetworkProbeService.TransportOf(NetworkProbeService.DefaultTarget) == ProbeTransport.Http, "默认目标走 HTTP");
    Check(NetworkProbeService.NormalizeTarget("") == NetworkProbeService.DefaultTarget, "空目标回落默认");
    Check(NetworkProbeService.NormalizeTarget(new string('x', 300)) == NetworkProbeService.DefaultTarget, "超长目标回落默认");
    Check(NetworkProbeService.NormalizeTarget("  1.1.1.1  ") == "1.1.1.1", "目标会去掉首尾空格");
    Check(NetworkProbeService.NormalizeTimeoutSeconds(0) == 3, "超时越界回落默认");
    Check(NetworkProbeService.NormalizeTimeoutSeconds(99) == 3, "超时越界回落默认");
    Check(NetworkProbeService.NormalizeTimeoutSeconds(10) == 10, "合法超时保持不变");

    // ══════════════ 守卫状态机 ══════════════
    Console.WriteLine("\n设备数守卫");

    int effectivePort = 8605;
    int reauthCalls = 0;
    var portal = new FakePortal();
    var probe = new FakeNetworkProbe { Result = true };

    var guardStore = new CredentialStore(Path.Combine(root, "guard"));
    guardStore.Load();
    guardStore.Settings.DeviceGuardEnabled = false;
    guardStore.Settings.DeviceGuardThreshold = 2;
    guardStore.Settings.DefaultPort = 8605;

    using var guard = new DeviceGuardService(
        guardStore, portal, probe,
        () => effectivePort,
        () => reauthCalls++,
        _ => { });

    Check(guard.Status.State == DeviceGuardState.Disabled, "守卫初始为关闭");

    // 守卫关闭 → 不触发
    portal.DeviceCount = 5;
    await guard.EvaluateAsync();
    Check(probe.Calls == 0, "守卫关闭时不探测");
    Check(portal.DisconnectCalls == 0, "守卫关闭时不断开");

    // 打开守卫
    guardStore.Settings.DeviceGuardEnabled = true;

    // 设备数 2 = 阈值 2 → 不触发
    portal.DeviceCount = 2;
    await guard.EvaluateAsync();
    Check(probe.Calls == 0, "设备数等于阈值时不触发");
    Check(guard.Status.DeviceCount == 2, "卡片显示的设备数正确");

    // 设备数 3 > 阈值 2 → 触发探测，但探测通过 → 不处置
    portal.DeviceCount = 3;
    probe.Result = true;
    await guard.EvaluateAsync();
    Check(probe.Calls == 1, "设备数超过阈值时探测");
    Check(portal.DisconnectCalls == 0, "探测通过时不处置");

    // 探测失败 → 断开 + 重登
    probe.Result = false;
    portal.DeviceCount = 3;
    await guard.EvaluateAsync();
    Check(portal.DisconnectCalls == 1, "探测失败时断开本机连接");
    Check(reauthCalls == 1, "探测失败时触发重新认证");
    Check(portal.DisconnectPort == 8605, "断开用的是生效端口");
    Check(guard.Status.State == DeviceGuardState.Cooldown, "处置后进入冷却");

    // 冷却期内不再处置
    await guard.EvaluateAsync();
    Check(portal.DisconnectCalls == 1, "冷却期内不重复处置");

    // 断开失败 → 仍然重登
    portal.DisconnectResult = false;
    await guard.DisposeNowAsync();
    Check(reauthCalls == 2, "断开失败也仍然重新认证");
    Check(guard.Status.State != DeviceGuardState.Stopped, "失败 1 次还不算到上限");

    // 连续失败 3 次 → 停止自动处置
    await guard.DisposeNowAsync();
    Check(guard.Status.State != DeviceGuardState.Stopped, "失败 2 次还不算到上限");
    await guard.DisposeNowAsync();
    Check(guard.Status.State == DeviceGuardState.Stopped, "连续处置失败达上限后停止");

    // 设备列表获取失败 → 跳过本轮，不处置
    var guard2 = new DeviceGuardService(
        guardStore, portal, probe, () => 8603, () => { }, _ => { });
    guardStore.Settings.DeviceGuardEnabled = true;
    guardStore.Settings.DeviceGuardThreshold = 2;
    portal.ListSucceeds = false;
    portal.DisconnectCalls = 0;
    probe.Calls = 0;
    await guard2.EvaluateAsync();
    Check(portal.DisconnectCalls == 0, "设备列表获取失败时跳过本轮");
    Check(probe.Calls == 0, "设备列表获取失败时不探测");
    guard2.Dispose();

    var concurrentPortal = new FakePortal { DeviceCount = 3 };
    using var concurrentGuard = new DeviceGuardService(
        guardStore, concurrentPortal, probe, () => effectivePort, () => { }, _ => { });
    var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    concurrentPortal.BeforeList = () =>
    {
        entered.TrySetResult();
        return release.Task;
    };
    var evaluation = concurrentGuard.EvaluateAsync();
    await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
    concurrentGuard.NotifyNetworkChanged();
    concurrentGuard.NotifyNetworkChanged();
    var pending = concurrentGuard.EvaluateAsync();
    Check(concurrentPortal.ListCalls == 1, "换网期间不并发请求设备列表");
    release.SetResult();
    await Task.WhenAll(evaluation, pending).WaitAsync(TimeSpan.FromSeconds(5));
    Check(concurrentPortal.ListCalls == 2, "多次触发只补做一次评估");
    Check(concurrentPortal.DisconnectCalls == 1, "旧网络评估不会重复断开");

    // ══════════════ 接入方式判定（真实网卡） ══════════════
    Console.WriteLine("\n接入方式判定");

    var candidates = NetworkAccessService.PhysicalCandidates();
    bool allPhysical = candidates.All(c =>
        c.NetworkInterfaceType is NetworkInterfaceType.Ethernet or NetworkInterfaceType.Wireless80211);
    Check(allPhysical, "候选网卡里没有虚拟隧道/蓝牙/回环", string.Join(", ", candidates.Select(c => c.Description)));

    var index = NetworkAccessService.BestInterfaceIndex(System.Net.IPAddress.Parse("223.5.5.5"));
    Check(index is null || index.Value > 0, "GetBestInterfaceEx 用 16 字节 sockaddr 不会报错");

    var (ssid, reliable) = NetworkAccessService.ReadSsid(null);
    Check(ssid.Length == 0 && !reliable, "网卡标识为空时降级为取不到 SSID");

    // 真实布局：isState(4) | mode(4) | profileName[256]WCHAR(512) | DOT11_SSID
    // 所以 uSSIDLength 在偏移 520，SSID 字节从 524 开始。
    var fake = new byte[560];
    fake[520] = 3;                      // uSSIDLength = 3
    fake[524] = (byte)'a';
    fake[525] = (byte)'b';
    fake[526] = (byte)'c';
    var (parsed, parsedOk) = NetworkAccessService.ParseSsid(fake);
    Check(parsedOk && parsed == "abc", "SSID 缓冲按规范偏移（520）解析");

    // 偏移 8 是 profileName 的头（UTF-16 的 'm' = 0x6D），不是 SSID 长度。
    // 以前把它当长度读会得到 109，才导致「永远取不到 SSID」。
    var wrongOffset = new byte[560];
    wrongOffset[8] = (byte)'m';         // 0x6D
    wrongOffset[9] = 0x00;
    var (_, wrongOk) = NetworkAccessService.ParseSsid(wrongOffset);
    Check(!wrongOk, "偏移 8 的 profileName 不会被误当成 SSID 长度");

    // 真实机型取到的形态：profileName 是 UTF-16，SSID 在 520
    var real = new byte[604];
    var profileName = System.Text.Encoding.Unicode.GetBytes("mtt_315_5G");
    Array.Copy(profileName, 0, real, 8, profileName.Length);
    real[520] = 10;
    var realBytes = System.Text.Encoding.ASCII.GetBytes("mtt_315_5G");
    Array.Copy(realBytes, 0, real, 524, realBytes.Length);
    var (realSsid, realOk) = NetworkAccessService.ParseSsid(real);
    Check(realOk && realSsid == "mtt_315_5G", "实机形态的连接属性能解出 SSID");

    var garbage = new byte[560];
    garbage[520] = 200;                 // 长度不合法
    var (_, garbageOk) = NetworkAccessService.ParseSsid(garbage);
    Check(!garbageOk, "非法 SSID 长度被拒绝");

    // 兜底扫描：SSID 落在非规范位置时仍要能解出来
    var offSpec = new byte[560];
    offSpec[300] = 4;
    offSpec[304] = (byte)'t';
    offSpec[305] = (byte)'e';
    offSpec[306] = (byte)'s';
    offSpec[307] = (byte)'t';
    var (fallback, fallbackOk) = NetworkAccessService.ParseSsid(offSpec);
    Check(fallbackOk && fallback == "test", "非规范偏移时兜底扫描能解出 SSID");
}
finally
{
    try { Directory.Delete(root, true); } catch { }
}

Console.WriteLine(failed == 0 ? $"\n全部通过（{checks} 项）" : $"\n{failed} 项失败（共 {checks} 项）");
return failed == 0 ? 0 : 1;
