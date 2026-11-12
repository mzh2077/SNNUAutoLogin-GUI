using System.Text;
using SNNUAutoLogin.Services;

// 在线设备列表的解析回归测试。
//
// 两部分：
//   1. 合成样本 —— 覆盖所有分支，断言固定、可靠；
//   2. 真实样本 —— fixture_devices.html 是从校园网门户在线控制页抓下来的页面（UTF-8），
//      用来确认「真实页面能被解析」，不绑定具体设备数量（在线设备本来就时多时少）。
//
// 换学校时：替换 fixture_devices.html，并按新学校的表格结构改合成样本。

Console.OutputEncoding = Encoding.UTF8;

int failed = 0;

void Check(string name, bool ok, string detail = "")
{
    if (ok)
    {
        Console.WriteLine($"  PASS  {name}");
    }
    else
    {
        failed++;
        Console.WriteLine($"  FAIL  {name}{(detail.Length > 0 ? " — " + detail : "")}");
    }
}

// ────────────────────────── 1. 合成样本 ──────────────────────────

// 结构照搬真实门户：表头用 <th>，数据行用 <td>，
// 最后一列是操作位 —— 别的会话有「强制下线」按钮（onclick 里带会话标识），
// 本机当前这条连接没有。
const string Synthetic = """
<html><body>
<table id="ec_table">
  <tbody id="ec_table_body">
  <tr bgcolor="#e8e8e8" class="zhenwen">
    <th>上网IP</th><th>MAC地址</th><th>上网时间</th><th>上网设备</th><th>上网位置</th><th>&nbsp;</th>
  </tr>
  <tr>
    <td class="zhengwen1">10.139.31.240</td>
    <td class="zhengwen1">06:3b:96:c0:c4:4a</td>
    <td class="zhengwen1">2026-10-30 17:36:39</td>
    <td class="zhengwen1">Android手机</td>
    <td class="zhengwen1">雁塔校区无线用户</td>
    <td class="zhengwen1">
        <input type="button" value="强制下线" onclick="dm('YT-ME6001201050000000ba052aAAANNG')"/>
    </td>
  </tr>
  <tr>
    <td class="zhengwen1">10.148.3.65</td>
    <td class="zhengwen1">70:a6:cc:cc:73:65</td>
    <td class="zhengwen1">2026-10-30 09:58:58</td>
    <td class="zhengwen1">&nbsp;</td>
    <td class="zhengwen1">办公区某楼</td>
    <td class="zhengwen1">&nbsp;&nbsp;</td>
  </tr>
  </tbody>
</table>
</body></html>
""";

Console.WriteLine("在线设备列表解析测试");

var devices = DeviceListParser.Parse(Synthetic, "10.148.3.65");

Check("解析出 2 台设备", devices.Count == 2, $"实际 {devices.Count}");

if (devices.Count == 2)
{
    var phone = devices[0];
    var self = devices[1];

    Check("设备名正确", phone.DisplayName == "Android手机", phone.DisplayName);
    Check("IP 正确", phone.Ip == "10.139.31.240", phone.Ip);
    Check("MAC 正确", phone.Mac == "06:3b:96:c0:c4:4a", phone.Mac);
    Check("上线时间正确", phone.LoginTime == "2026-10-30 17:36:39", phone.LoginTime);
    Check("位置正确", phone.Location == "雁塔校区无线用户", phone.Location);

    Check("别的会话可以强制下线", phone.CanForceOffline);
    Check("别的会话不是本机", !phone.IsCurrent);
    Check("会话标识提取正确",
        phone.SessionId == "YT-ME6001201050000000ba052aAAANNG", phone.SessionId ?? "null");

    Check("本机 IP 正确", self.Ip == "10.148.3.65", self.Ip);
    Check("本机被标记为当前设备", self.IsCurrent);
    Check("本机不能强制下线（只能断开）", !self.CanForceOffline);
    Check("本机设备名为空时回退到 MAC", self.DisplayName == "70:a6:cc:cc:73:65", self.DisplayName);
    Check("本机位置仍然解析出来", self.Location == "办公区某楼", self.Location);
}

// ── 强制下线地址：保留 vloginid / ts / id，并带上 type=dm ─────────────
var frameUrl = "http://202.117.144.205:80/zili/auth_my_state.php?vloginid=20263436&ts=1793354084096&id=5212a86f8b29ba90b2c994f2016acbab";
var dmUrl = DeviceListParser.BuildForceOfflineUrl(frameUrl, "YT-ME6001201050000000ba052aAAANNG");

Check("下线地址带 type=dm", dmUrl.Contains("type=dm"), dmUrl);
Check("下线地址带 sessionid", dmUrl.Contains("sessionid=YT-ME6001201050000000ba052aAAANNG"), dmUrl);
Check("下线地址保留 vloginid", dmUrl.Contains("vloginid=20263436"), dmUrl);
Check("下线地址保留 ts", dmUrl.Contains("ts=1793354084096"), dmUrl);
Check("下线地址保留 id", dmUrl.Contains("id=5212a86f8b29ba90b2c994f2016acbab"), dmUrl);
Check("下线地址只有一个问号", dmUrl.Count(c => c == '?') == 1, dmUrl);
Check("下线地址路径不变",
    dmUrl.StartsWith("http://202.117.144.205:80/zili/auth_my_state.php"), dmUrl);

// ── 边界：空页面 / 没有表格 / 没有设备行 ────────────────────────────────
Check("空字符串不报错", DeviceListParser.Parse("", null).Count == 0);
Check("null 不报错", DeviceListParser.Parse(null!, null).Count == 0);
Check("无关页面解析出 0 台", DeviceListParser.Parse("<html><body>hi</body></html>", null).Count == 0);
Check("表头行不算设备",
    DeviceListParser.Parse("<tbody id=\"ec_table_body\"><tr><th>上网IP</th></tr></tbody>", null).Count == 0);
Check("单元格不足 5 列的行被跳过",
    DeviceListParser.Parse("<tbody id=\"ec_table_body\"><tr><td>1.2.3.4</td></tr></tbody>", null).Count == 0);
Check("没有 tbody id 时解析出 0 台（不误抓）",
    DeviceListParser.Parse("<table><tr><td>1.2.3.4</td></tr></table>", null).Count == 0);

// ── 边界：当前 IP 不在列表里时，没有设备被标为本机 ──────────────────────
var none = DeviceListParser.Parse(Synthetic, "10.255.255.255");
Check("当前 IP 不匹配时无本机设备", none.TrueForAll(d => !d.IsCurrent));
Check("当前 IP 为 null 时无本机设备",
    DeviceListParser.Parse(Synthetic, null).TrueForAll(d => !d.IsCurrent));

// ────────────────────────── 2. 真实样本 ──────────────────────────

var fixturePath = Path.Combine(AppContext.BaseDirectory, "fixture_devices.html");
if (!File.Exists(fixturePath))
{
    // 兜底：从项目目录往上找
    var dir = AppContext.BaseDirectory;
    while (dir is not null && !File.Exists(Path.Combine(dir, "fixture_devices.html")))
        dir = Path.GetDirectoryName(dir.TrimEnd(Path.DirectorySeparatorChar));

    if (dir is null)
    {
        Console.WriteLine("找不到 fixture_devices.html");
        return 1;
    }

    fixturePath = Path.Combine(dir, "fixture_devices.html");
}

var html = File.ReadAllText(fixturePath, Encoding.UTF8);
var real = DeviceListParser.Parse(html, "10.148.3.65");

Check("真实页面能解析出至少 1 台设备", real.Count >= 1, $"实际 {real.Count}");
Check("真实页面每台设备都有合法 IP",
    real.TrueForAll(d => System.Text.RegularExpressions.Regex.IsMatch(d.Ip, @"^[0-9]{1,3}(\.[0-9]{1,3}){3}$")),
    string.Join(", ", real.Select(d => d.Ip)));
Check("真实页面每台设备都有 MAC",
    real.TrueForAll(d => !string.IsNullOrWhiteSpace(d.Mac)),
    string.Join(", ", real.Select(d => d.Mac)));
Check("真实页面每台设备都有上线时间",
    real.TrueForAll(d => !string.IsNullOrWhiteSpace(d.LoginTime)));
Check("真实页面至多一台是本机",
    real.Count(d => d.IsCurrent) <= 1);
Check("真实页面有会话标识的设备都能强制下线",
    real.Where(d => d.SessionId is not null).All(d => d.CanForceOffline));

// ─────────────────── 3. 「断开连接」：地址构造 ───────────────────
//
// 门户 userstatus.jsp 底部那个大按钮就是：
//   <a href="logoff"><img src="image/duankai.gif" …></a>
// 即 /snnuportal/logoff，断的是本机当前这条会话。

Console.WriteLine("\n「断开连接」地址构造");

Check("断线地址是根地址 + logoff",
    DeviceListParser.BuildDisconnectUrl("http://202.117.144.205:8603/snnuportal/")
        == "http://202.117.144.205:8603/snnuportal/logoff",
    DeviceListParser.BuildDisconnectUrl("http://202.117.144.205:8603/snnuportal/"));

Check("根地址末尾没斜杠时也不多出斜杠",
    DeviceListParser.BuildDisconnectUrl("http://202.117.144.205:8603/snnuportal")
        == "http://202.117.144.205:8603/snnuportal/logoff",
    DeviceListParser.BuildDisconnectUrl("http://202.117.144.205:8603/snnuportal"));

Check("不同接入端口拼出各自的 logoff",
    DeviceListParser.BuildDisconnectUrl("http://202.117.144.205:8605/snnuportal/")
        == "http://202.117.144.205:8605/snnuportal/logoff");

// ─────────────────── 4. 「断开连接」与「点击下线」的分工 ───────────────────
//
// 页面级「断开连接」= 本机会话（logoff）
// 行级「点击下线」  = 别人的会话（type=dm）
// 两件事不能混。

Console.WriteLine("\n按钮分工");

var selfDevice = new OnlineDevice { Ip = "10.148.3.65", Mac = "70:a6:cc:cc:73:65", IsCurrent = true };
var otherDevice = new OnlineDevice { Ip = "10.139.31.240", Mac = "06:3b:96:c0:c4:4a", SessionId = "YT-SID" };

Check("本机不能强制下线（走页面级断开）", !selfDevice.CanForceOffline);
Check("别人的会话可以强制下线", otherDevice.CanForceOffline);
Check("别人的会话不是本机", !otherDevice.IsCurrent);

// ─────────────────── 5. DeviceManager：断开本机 ───────────────────

Console.WriteLine("\nDeviceManager 断开本机");

var fake = new FakePortal();
using var dm = new DeviceManager(new Microsoft.UI.Dispatching.DispatcherQueue(), fake);

// 5.1 成功断开
fake.DisconnectResult = true;
var ok = await dm.DisconnectSelfAsync();

Check("断开成功返回 true", ok);
Check("确实调了门户的断开接口", fake.DisconnectCalls == 1, $"调用 {fake.DisconnectCalls} 次");
Check("断开用的是当前端口", fake.LastPort == 8603, $"端口 {fake.LastPort}");
Check("断开后没有误触发强制下线", fake.ForceOfflineCalls == 0);
Check("断开后重新拉了一次列表", fake.GetCalls >= 1, $"拉列表 {fake.GetCalls} 次");

// 5.2 断开失败 → 状态为 Failed 且带错误文案
var fake2 = new FakePortal { DisconnectResult = false };
using var dm2 = new DeviceManager(new Microsoft.UI.Dispatching.DispatcherQueue(), fake2);

var disconnectFailed = await dm2.DisconnectSelfAsync();

Check("断开失败返回 false", !disconnectFailed);
Check("断开失败时状态是 Failed", dm2.State == DeviceListState.Failed, dm2.State.ToString());
Check("断开失败时有错误文案", !string.IsNullOrWhiteSpace(dm2.Error), dm2.Error);

// 5.3 断开失败不影响别的操作（不应调强制下线）
Check("断开失败也没误触发强制下线", fake2.ForceOfflineCalls == 0);

// 5.4 列表状态流转
var fake3 = new FakePortal();
using var dm3 = new DeviceManager(new Microsoft.UI.Dispatching.DispatcherQueue(), fake3);

Check("初始状态是 Idle", dm3.State == DeviceListState.Idle, dm3.State.ToString());

fake3.DevicesResult = DeviceListResult.Fail("未认证，无法获取在线设备");
await dm3.RefreshAsync();

Check("拉列表失败时状态是 Failed", dm3.State == DeviceListState.Failed, dm3.State.ToString());
Check("拉列表失败时带上门户给的文案", dm3.Error.Contains("未认证"), dm3.Error);

fake3.DevicesResult = DeviceListResult.Ok(
    new[] { selfDevice, otherDevice },
    "http://202.117.144.205:80/zili/auth_my_state.php?vloginid=20263436&ts=1&id=1");
await dm3.RefreshAsync();

Check("拉取成功后状态是 Ready", dm3.State == DeviceListState.Ready, dm3.State.ToString());
Check("拉取成功后清掉错误文案", dm3.Error.Length == 0, dm3.Error);
Check("列表里有 2 台设备", dm3.Devices.Count == 2, $"实际 {dm3.Devices.Count}");
Check("设备记录了接入端口", dm3.Devices.All(d => d.Ports.Contains(8603)));

// 5.5 行级「点击下线」走强制下线，不走 logoff
var fake4 = new FakePortal
{
    DevicesResult = DeviceListResult.Ok(
        new[] { otherDevice },
        "http://202.117.144.205:80/zili/auth_my_state.php?vloginid=20263436&ts=1&id=1")
};
using var dm4 = new DeviceManager(new Microsoft.UI.Dispatching.DispatcherQueue(), fake4);
await dm4.RefreshAsync();

var offlineOk = await dm4.TakeOfflineAsync(otherDevice);

Check("行级下线成功返回 true", offlineOk);
Check("行级下线调的是强制下线", fake4.ForceOfflineCalls == 1, $"调用 {fake4.ForceOfflineCalls} 次");
Check("行级下线带对了会话标识", fake4.LastSessionId == "YT-SID", fake4.LastSessionId ?? "null");
Check("行级下线没有误调 logoff", fake4.DisconnectCalls == 0);

// 5.6 强制下线失败 → 状态 Failed
var fake5 = new FakePortal
{
    ForceOfflineResult = false,
    DevicesResult = DeviceListResult.Ok(
        new[] { otherDevice },
        "http://202.117.144.205:80/zili/auth_my_state.php?vloginid=20263436&ts=1&id=1")
};
using var dm5 = new DeviceManager(new Microsoft.UI.Dispatching.DispatcherQueue(), fake5);
await dm5.RefreshAsync();

var offlineFailed = await dm5.TakeOfflineAsync(otherDevice);

Check("强制下线失败返回 false", !offlineFailed);
Check("强制下线失败时状态是 Failed", dm5.State == DeviceListState.Failed, dm5.State.ToString());
Check("强制下线失败后设备状态回退为在线", otherDevice.Status == "在线", otherDevice.Status);

// ─────────────────── 6. 切换接入端口 ───────────────────

Console.WriteLine("\n接入端口切换");

var fake6 = new FakePortal();
using var dm6 = new DeviceManager(new Microsoft.UI.Dispatching.DispatcherQueue(), fake6);

await dm6.RefreshAsync(8605);

Check("切换端口后用的是新端口", fake6.LastPort == 8605, $"端口 {fake6.LastPort}");
Check("切换端口后记住了该端口", dm6.Port == 8605, dm6.Port.ToString());

Console.WriteLine(failed == 0 ? "\n全部通过" : $"\n{failed} 项失败");
return failed == 0 ? 0 : 1;
