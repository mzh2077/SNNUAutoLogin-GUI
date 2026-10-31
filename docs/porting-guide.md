# 把校园网自动登录改成自己学校的版本

这份文档写给想复用本项目、但自己学校门户不一样的人。

按顺序读，每一步都有「要改哪里 / 怎么验证」。文档不要求你读完所有代码，
**从第 2 步开始动手也行** —— 前两步是理解，后两步是照着改。

---

## 0. 先搞清楚一个前提

校园网认证门户（Portal）就是你在浏览器里点「上网」时弹出来的那个网页。
各家学校的门户长得不一样，但**干的事是一样的**：

```
1. 你访问某个网址（门户地址）
2. 页面问你账号密码 → 你填 → 提交
3. 门户把你的出口 IP 和账号绑定 → 之后这台机器就能上网
4. 之后你可以查「我现在在线吗」「我有几台设备在线」
```

本项目做的是：**把第 2、3 步自动化**，让它开机就自动登录，掉线自动补认证。

「在线设备管理」是第 4 步的延伸：让你在软件里看到账号下有几台设备在线，
并把不用的那台踢下线。

> ⚠️ **重要**：换学校时，你要改的是「门户地址」和「表单字段名」，
> 界面、托盘、巡检、断线重连这些逻辑**基本都能原样复用**。

---

## 1. 项目结构速览

```
SNNUAutoLogin/
├── Services/
│   ├── PortalClient.cs        ★ 门户 HTTP 客户端：登录、状态查询（改学校改这里）
│   ├── LoginCoordinator.cs      登录状态机：定时巡检、掉线重连、验证码
│   ├── DeviceListParser.cs    ★ 在线设备表格的解析（纯函数，好测试）
│   ├── DeviceManager.cs       ★ 在线设备：拉列表、下线、刷新
│   ├── CredentialStore.cs       账号密码落盘（DPAPI 加密）
│   ├── NetworkWatch.cs          网络变化监听（重连 Wi-Fi / 唤醒后立刻补认证）
│   └── ...
├── MainWindow.xaml              界面（WinUI 3 / WPF 风格）
├── MainWindow.xaml.cs           界面逻辑
├── AppRuntime.cs                进程级运行时：把后台状态和界面解耦
├── Fx.cs                        动效层
└── tests/
    ├── StartupTests/            开机自启与登录回归测试
    ├── CredentialTests/         凭据持久化回归测试
    └── DeviceListTests/      ★ 在线设备解析回归测试
```

带 ★ 的是**换学校时主要要碰的文件**。

### 分层约定（照着这个分，新文件才不会乱）

| 层 | 放哪 | 能不能碰网络 | 能不能碰界面 |
|---|---|---|---|
| 协议解析 | `Services/` 里的纯函数 | 不能 | 不能 |
| 网络 + 状态机 | `Services/PortalClient.cs`、`LoginCoordinator.cs` | 能 | 不能 |
| 界面 | `MainWindow.xaml(.cs)` | 不直接发请求 | 能 |

**规矩**：`PortalClient` 只负责「发请求、解析、返回结果」，不碰界面；
`MainWindow` 只负责显示，不自己发请求；两边通过 `AppRuntime` 传话。
所以后台线程拿到数据后，要用 `DispatcherQueue.TryEnqueue` 切回 UI 线程再改控件。

---

## 2. 第一步：摸清你自己学校的门户协议

**这是唯一必须自己做的调查**，做完后第 3 步就是照抄。

### 2.1 手工走一遍

在**已连上校园网**的机器上：

1. 浏览器打开门户地址（找不到就搜「学校名 + 校园网 认证 登录 802.1x」）。
2. 记下地址栏里的**完整 URL**，尤其是**端口号**。
   - 例：`http://202.117.144.205:8603/snnuportal/`
   - 没有端口号就是 80。
3. 抓登录表单。浏览器 F12 → Network → 提交登录那一条请求 →
   - 请求 URL 是什么？
   - 请求方法是 GET 还是 POST？
   - Form Data 里的**字段名**是什么？（本项目是 `account` / `password` / `yys`）
4. 登录成功后，门户跳到哪个页面？那个页面就是「状态页」。
5. 状态页上怎么判断「我在线了」？
   本项目找的是 `当前登录账号` 这个字样 —— 你学校可能叫「用户名」「学号」「帐号」。
6. 状态页上怎么看到「在线设备列表」？
   - 如果设备表格直接写在状态页 HTML 里 → 直接解析；
   - 如果包在 `<iframe>` 里（**本项目就是这种**）→ 先取 iframe 的 `src` 再请求它。

### 2.2 用 curl 抓一份真实响应

```bash
# 状态页（还没登录时也会返回 HTML，看它长什么样）
curl -s -D - "http://<你的门户地址>/<路径>/userstatus.jsp" -o page.html

# 如果设备表格在 iframe 里，从 HTML 里找 src
grep -o 'iframe[^>]*src="[^"]*"' page.html
```

把 `<html>` 存成文件，后面要用来写测试样本。

### 2.3 常见的门户形态

| 形态 | 特征 | 怎么改 |
|---|---|---|
| **表单 POST** | 请求体是 `account=xxx&password=yyy` | 改 `PortalClient.LoginInternalAsync` 里的表单字段 |
| **表单 GET** | 密码在 URL query 里 | 同上，把表单改成拼 URL |
| **表单在 iframe 里** | 状态页只有几行 JS | 见 2.1 第 6 条 |
| **JSON 接口** | 返回 `{"code":0,...}` | 把 `ReadHtmlAsync` 换成读 JSON |
| **需要验证码** | 登录页有图片验证码 | 本项目已支持，改 `LooksLikeCaptcha` 的关键词 |

### 2.4 编码坑（最容易踩）

门户页面**经常是 GB2312/GBK**，但有些子页面是 **UTF-8**。
本项目就踩过：外层 `userstatus.jsp` 是 GB2312，里面的 iframe 却是 UTF-8。

```csharp
// 本项目的做法：外层按 gb18030 解，iframe 按 UTF-8 解
var html = await ReadHtmlAsync(resp, ct);            // GB 家族
var frameHtml = Encoding.UTF8.GetString(frameBytes); // UTF-8
```

**如果你学校页面全是 UTF-8**，把 `PortalClient.CreateGbEncoding()` 换成 `Encoding.UTF8` 即可。

---

## 3. 第二步：改代码

### 3.1 改门户地址与端口

`Services/PortalClient.cs` 顶部：

```csharp
private const string Root = "http://202.117.144.205:8603/snnuportal/";
private const string PortalHost = "202.117.144.205";

/// <summary>可选接入端口：不同端口对应不同接入方式（有线 / 无线 / 各运营商出口）。</summary>
public static readonly int[] KnownPorts = { 8603, 8601, 8602, 8605, 8609, ... };
```

改成你学校的：

```csharp
private const string Root = "http://<门户IP>:<端口>/<路径>/";
private const string PortalHost = "<门户IP>";
public static readonly int[] KnownPorts = { <你学校的端口列表> };
```

> 端口列表怎么找？同一个 IP 上不同端口往往对应**不同接入方式**
> （有线 / 无线 / 移动 / 联通 / 电信）。逐个端口访问登录页，
> 看页面标题或徽标文字不一样，那就是不同的接入点。
>
> 本项目的 `KnownPorts` 就是这么来的：把 8601~8610 挨个试一遍，取能返回登录页的。

### 3.2 改登录表单字段

`PortalClient.LoginInternalAsync` 里：

```csharp
form["account"] = account;
form["password"] = password;
if (yys.Length > 0) form["yys"] = yys;
```

改成你学校的字段名。如果学校没有「运营商出口」这个概念，
把 `Operator` 枚举和 `yys` 一起去掉即可（界面上的「网络出口」下拉框也要一起删）。

### 3.3 改「是否已登录」的判定

`PortalClient.GetStatusAsync` 里：

```csharp
if (html.Contains("当前登录账号", StringComparison.Ordinal))
{
    var acc = Match(html, @"当前登录账号[：:]\s*(?:<[^>]+>\s*)*([^<\s]+)");
    var ip  = Match(html, @"当前IP[：:]\s*(?:<[^>]+>\s*)*([0-9]{1,3}(?:\.[0-9]{1,3}){3})");
    var time= Match(html, @"登录时间[：:]\s*(?:<[^>]+>\s*)*([0-9]{4}-[0-9]{2}-[0-9]{2}\s+[0-9]{2}:[0-9]{2}:[0-9]{2})");
    return LoginResult.Online(acc, ip, time);
}
```

把「当前登录账号」「当前IP」「登录时间」换成你学校页面上的字样。
`Match` 用的是正则，`：` 和 `:` 两种写法都兼容。

### 3.4 改在线设备表格的解析

`Services/DeviceListParser.cs` —— 这是纯函数，改起来最安全。

本项目门户的表格长这样：

```html
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
        <input type="button" value="强制下线" onclick="dm('YT-ME6001...')"/>
    </td>
  </tr>
</tbody>
```

**注意三个关键点**：

1. **表头用 `<th>`，数据行用 `<td>`** —— 解析时只取 `<td>`，表头自然被跳过。
2. **最后一列是操作位**，别的会话那里有「强制下线」按钮，
   `onclick="dm('会话标识')"` 里就是下线所需的标识。
3. **本机当前这条连接没有会话标识**（操作位是空的）—— 它只能「断开」，不能「强制下线」。

改法：

```csharp
// 表体定位：换成你学校的 id 或结构
private static readonly Regex BodyRegex = new(
    @"<tbody[^>]*\bid\s*=\s*[""']ec_table_body[""'][^>]*>([\s\S]*?)</tbody>", ...);

// 会话标识：换成你学校 onclick 里的函数名
private static readonly Regex SessionRegex = new(
    @"dm\(\s*[""']([^""']+)[""']\s*\)", ...);
```

如果表格没有 `<tbody id="...">`，就把 `BodyRegex` 改成匹配第一个 `<table>`，
或者干脆按 `<tr>` 逐行扫、跳过没有 `<td>` 的行。

### 3.5 改「下线」的地址

门户网页上的「强制下线」一般是个跳转：

```javascript
function dm(sessionid) {
    window.location = 'auth_my_state.php?type=dm&sessionid=' + sessionid +
        '&vloginid=<账号>&ts=<时间戳>&id=<令牌>';
}
```

`DeviceListParser.BuildForceOfflineUrl` 就是照着这个拼的：

```csharp
public static string BuildForceOfflineUrl(string frameUrl, string sessionId)
{
    var uri = new Uri(frameUrl);
    var query = uri.Query.TrimStart('?');

    var sb = new StringBuilder();
    sb.Append(uri.GetLeftPart(UriPartial.Path));
    sb.Append("?type=dm&sessionid=").Append(Uri.EscapeDataString(sessionId));
    if (query.Length > 0) sb.Append('&').Append(query);   // 保留原有的 vloginid/ts/id
    return sb.ToString();
}
```

**保留原查询串**很关键 —— 那些 `vloginid` / `ts` / `id` 是会话令牌，丢了就不认。

如果你的门户下线是 POST 或接口形式，改这个方法返回完整 URL，
并在 `PortalClient.ForceOfflineAsync` 里改成对应的请求方式。

### 3.6 改「断开本机连接」

门户状态页底部有个大「断开连接」按钮：

```html
<a href="logoff"><img src="image/duankai.gif" height="58" width="224"></a>
```

它断的是**本机当前这条会话**（和设备表格里踢别人的「强制下线」是两件事）。
本项目把它做成了「在线设备」卡片底部的页面级按钮：

```csharp
// PortalClient.DisconnectAsync
var url = DeviceListParser.BuildDisconnectUrl(RootFor(port));   // → .../snnuportal/logoff
using var resp = await _http.GetAsync(url, ct);
```

你学校可能叫 `logout` / `doLogout` / `signout`，
从状态页 HTML 里找那个 `<a href="...">` 就行，
然后改 `DeviceListParser.BuildDisconnectUrl` 里的路径即可。

**注意行为差异**：门户网页上断开后不会自己连回来，但本软件开着自动重连，
断开几秒后巡检会重新认证 —— 与门户网页行为一致，但用户会看到「刚断开又连上」。
若想让断开真正生效，需自行关掉「断线自动重连」开关。

---

## 4. 第三步：验证

### 4.1 跑解析测试

```bash
dotnet run --project tests/DeviceListTests -c Release
```

这个测试用**真实抓下来的页面**做样本（`tests/DeviceListTests/fixture_devices.html`）。
换学校时：**抓一份你学校的新样本替换掉它**，然后改断言里的期望值。

测试分三块：

1. **解析** —— 从真实页面里取出设备行，断言字段与边界情况
2. **地址构造** —— 强制下线（`type=dm`）与断开连接（`logoff`）两种地址拼得对
3. **状态流转** —— 用 `FakePortal` 替换掉真实网络，验证 `DeviceManager` 的
   拉列表 / 下线 / 断开各自走对接口，失败时状态与错误文案正确

测试里覆盖了这些边界，换学校后请逐条确认：

- 表头行不会被当成设备
- 空页面 / 无关页面解析出 0 台，不抛异常
- 当前 IP 不在列表里时，没有设备被误标为「本机」
- 下线地址保留了 `vloginid` / `ts` / `id`

### 4.2 跑已有回归测试

```bash
dotnet run --project tests/StartupTests -c Release
dotnet run --project tests/CredentialTests -c Release
```

这两个测的是开机自启和凭据保存，跟门户无关，正常情况下**不该改动**。

### 4.3 编译整个程序

```bash
dotnet build SNNUAutoLogin.csproj -c Release
```

### 4.4 手工验收清单

- [ ] 填账号密码 → 点「立即登录」→ 状态变「已连接」，出口 IP 正确
- [ ] 断网再联网 → 自动补认证，不用手动点
- [ ] 端口填错 → 状态变「登录失败」，日志里有原因
- [ ] 「在线设备」卡片能拉到设备列表，设备名/IP 正确
- [ ] 自己的那台显示「断开连接」，别人的显示「点击下线」
- [ ] 点「点击下线」→ 对方真的掉线了，列表刷新后少一台
- [ ] 点「断开连接」→ 本机掉线（注意：巡检会自动重新连回来，这是预期行为）
- [ ] 不在校园网时 → 提示「无法连接 Portal」，程序不崩

---

## 5. 常见坑

| 现象 | 原因 | 怎么办 |
|---|---|---|
| 登录成功但状态还是「未登录」 | 「已登录」的判定字样不对 | 打开门户页面 Ctrl+F 搜你学校用的词，改 `GetStatusAsync` |
| 中文全是乱码 | 页面编码判断错了 | 用「另存为」看页面实际编码；GB2312 用 `gb18030` 解，UTF-8 用 `Encoding.UTF8` |
| 设备列表能拉到但设备名是空的 | 表格列数不同 | 改 `DeviceListParser.Parse` 里的列下标 |
| 点「点击下线」没反应 | 少了会话令牌 | 检查 `BuildForceOfflineUrl` 有没有保留原查询串 |
| **状态页能打开，设备列表却是空的** | **请求走了 HTTP 代理** | 见下方「代理」一节 |
| 时好时坏 | 没绑物理网卡，出口 IP 漂了 | 看 `Services/NetworkWatch.cs` |
| 端口 8603 不通但 8605 通 | 接入点不同 | 改 `KnownPorts`，并让程序自动探测可用端口 |

### ⚠️ 代理：内网门户必须绕过 HTTP 代理

认证门户是**校园网内网地址**。如果机器上配了系统代理或环境变量里有
`HTTP_PROXY` / `http_proxy`，`HttpClient` 会自动走代理 —— 而代理通常连不进
校园网内网，于是：

- 状态页（`8603` 端口）偶尔还能通；
- 设备列表（`80` 端口的 `zili`）**直接 404**，界面显示「在线设备读取失败」。

浏览器和 curl 常常会自动绕过内网代理，所以手动测试时一切正常，
程序里却失败 —— **这个坑非常隐蔽**。

`PortalClient` 里已经处理好了：

```csharp
var handler = new SocketsHttpHandler
{
    ...
    // 认证门户是校园网内网地址，绝不能走系统或环境变量里的 HTTP 代理
    UseProxy = false
};
```

你改成自己学校后，**这一行务必保留**。
另外，在 `MainWindow` 里若要打开门户网页，也应该用
`Process.Start` 直接开而不是走代理。

---

## 6. 交给 AI 时的提示词模板

把这份文档连同代码一起给 AI，它就能按你的需求改：

```
我需要把这份校园网自动登录工具改成我们学校的版本。

背景资料：
- 我附上了 docs/porting-guide.md，请先完整读一遍，按里面的步骤走。
- 门户协议我已调查清楚，实际情况是：
  <在这里填：门户地址、端口、登录表单字段名、
   「已登录」判定字样、设备表格的 HTML 结构片段、
   下线接口的地址与请求方式、页面编码>

要求：
1. 只改动与门户协议相关的部分（Services/PortalClient.cs、
   Services/DeviceListParser.cs，以及必要的界面文案）。
2. 不要动 Services/NetworkWatch.cs、Services/CredentialStore.cs、
   Services/StartupManager.cs —— 这些和门户无关。
3. 保持现有代码风格：中文注释、/// XML 文档注释、
   私有方法在 public 方法之后。
4. 改完请：
   - 把新的门户页面存成 tests/DeviceListTests/fixture_devices.html
   - 更新 tests/DeviceListTests/Program.cs 里的期望值
   - 运行 dotnet run --project tests/DeviceListTests -c Release
     和 dotnet build SNNUAutoLogin.csproj -c Release，把结果贴给我
5. 如果有协议细节我上面没写清楚，先问我，不要猜。
```

---

## 7. 许可

本项目 MIT 许可。改造成自己学校的版本后，署名里保留原作者与来源即可。
