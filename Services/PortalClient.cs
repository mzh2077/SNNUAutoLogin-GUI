using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace SNNUAutoLogin.Services;

/// <summary>运营商出口。Campus 表示不选择运营商（校内直连/教育网）。</summary>
public enum Operator
{
    Campus = 0,
    Mobile = 1,
    Unicom = 2,
    Telecom = 3
}

public sealed class LoginResult
{
    public bool Success { get; init; }
    public bool AlreadyOnline { get; init; }

    /// <summary>Portal 要求输入图形验证码（此时未提交登录）。</summary>
    public bool NeedCaptcha { get; init; }

    public string Message { get; init; } = "";
    public string? Account { get; init; }
    public string? Ip { get; init; }
    public string? LoginTime { get; init; }

    public static LoginResult Ok(string msg, string? acc = null, string? ip = null, string? time = null)
        => new() { Success = true, Message = msg, Account = acc, Ip = ip, LoginTime = time };

    public static LoginResult Online(string? acc, string? ip, string? time)
        => new() { Success = true, AlreadyOnline = true, Message = "已在线", Account = acc, Ip = ip, LoginTime = time };

    public static LoginResult Fail(string msg) => new() { Success = false, Message = msg };

    public static LoginResult Captcha(string msg) => new() { Success = false, NeedCaptcha = true, Message = msg };
}

/// <summary>一张验证码图片（原始字节，界面自行解码显示）。</summary>
public sealed record CaptchaImage(byte[] Bytes, string Hint);

/// <summary>登录页解析结果：提交地址、隐藏域与验证码相关元素。</summary>
internal sealed class LoginPage
{
    public Uri? BaseUri { get; init; }
    public string? FormAction { get; init; }
    public Dictionary<string, string> Hidden { get; } = new(StringComparer.OrdinalIgnoreCase);
    public string? CaptchaField { get; set; }
    public string? CaptchaImageUrl { get; set; }
    public bool AlreadyOnline { get; init; }
    public bool HasPasswordField { get; init; }
}

/// <summary>
/// 陕西师范大学校园网 Portal 客户端。
/// 协议：POST application/x-www-form-urlencoded 到 /snnuportal/login，
/// 字段 account / password / yys；认证结果按出口 IP 判定，页面为 GB2312 编码。
/// </summary>
public sealed class PortalClient : IDisposable, IOnlineDevicePortal
{
    private const string Root = "http://202.117.144.205:8603/snnuportal/";
    private const string PortalHost = "202.117.144.205";

    /// <summary>可选接入端口：不同端口对应不同接入方式（有线 / 无线 / 各运营商出口）。</summary>
    public static readonly int[] KnownPorts = { 8603, 8601, 8602, 8605, 8609, 8604, 8606, 8607, 8608, 8610, 8600 };

    /// <summary>指定端口下的门户根地址。</summary>
    private static string RootFor(int port) => $"http://{PortalHost}:{port}/snnuportal/";

    private static readonly Encoding Gb = CreateGbEncoding();

    private readonly HttpClient _http;

    private static Encoding CreateGbEncoding()
    {
        try
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            return Encoding.GetEncoding("gb18030");
        }
        catch
        {
            return Encoding.UTF8;
        }
    }

    public PortalClient()
    {
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = true,
            MaxAutomaticRedirections = 8,
            UseCookies = true,
            CookieContainer = new CookieContainer(),
            ConnectTimeout = TimeSpan.FromSeconds(6),
            PooledConnectionLifetime = TimeSpan.FromMinutes(2),

            // 认证门户是校园网内网地址，绝不能走系统或环境变量里的 HTTP 代理：
            // 代理往往连不进内网，设备列表（80 端口的 zili）会直接 404。
            UseProxy = false
        };

        _http = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(15)
        };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/126.0.0.0 Safari/537.36");
        _http.DefaultRequestHeaders.Accept.ParseAdd("text/html,application/xhtml+xml,*/*");
    }

    /// <summary>检查 Portal 是否可达（即是否处在校园网环境）。</summary>
    public async Task<bool> IsPortalReachableAsync(CancellationToken ct = default)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Head, Root + "login");
            using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>查询当前出口是否已认证在线。</summary>
    public async Task<LoginResult> GetStatusAsync(CancellationToken ct = default)
    {
        try
        {
            using var resp = await _http.GetAsync(Root + "userstatus.jsp", ct).ConfigureAwait(false);
            var html = await ReadHtmlAsync(resp, ct).ConfigureAwait(false);

            if (html.Contains("当前登录账号", StringComparison.Ordinal))
            {
                var acc = Match(html, @"当前登录账号[：:]\s*(?:<[^>]+>\s*)*([^<\s]+)");
                var ip = Match(html, @"当前IP[：:]\s*(?:<[^>]+>\s*)*([0-9]{1,3}(?:\.[0-9]{1,3}){3})");
                var time = Match(html, @"登录时间[：:]\s*(?:<[^>]+>\s*)*([0-9]{4}-[0-9]{2}-[0-9]{2}\s+[0-9]{2}:[0-9]{2}:[0-9]{2})");
                return LoginResult.Online(acc, ip, time);
            }

            return LoginResult.Fail("未认证");
        }
        catch (OperationCanceledException)
        {
            return LoginResult.Fail("连接超时");
        }
        catch (Exception ex)
        {
            return LoginResult.Fail(Describe(ex));
        }
    }

    /// <summary>最近一次解析到的登录页；提交表单时复用它带的 action 与隐藏域。</summary>
    private LoginPage? _page;

    /// <summary>
    /// 取当前账号下的在线设备列表。
    ///
    /// 协议：userstatus.jsp 页面里嵌了一个 iframe（zili/auth_my_state.php），
    /// 设备表格在 iframe 里。iframe 页面是 UTF-8 编码，和外层的 GB2312 不一样。
    /// </summary>
    public async Task<DeviceListResult> GetOnlineDevicesAsync(int port, CancellationToken ct = default)
    {
        try
        {
            using var resp = await _http.GetAsync(RootFor(port) + "userstatus.jsp", ct).ConfigureAwait(false);
            var html = await ReadHtmlAsync(resp, ct).ConfigureAwait(false);

            // 没登录时这个页面没有 iframe，也没有「当前登录账号」
            var currentIp = Match(html, @"当前IP[：:]\s*(?:<[^>]+>\s*)*([0-9]{1,3}(?:\.[0-9]{1,3}){3})");
            var frameUrl = Match(html, @"<iframe[^>]*\ssrc\s*=\s*[""']([^""']+)[""']");
            if (string.IsNullOrEmpty(frameUrl))
                return DeviceListResult.Fail("未认证，无法获取在线设备");

            // iframe 在 80 端口的 zili 目录下，是绝对地址
            using var frameResp = await _http.GetAsync(frameUrl, ct).ConfigureAwait(false);
            var frameBytes = await frameResp.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
            var frameHtml = Encoding.UTF8.GetString(frameBytes);

            var devices = DeviceListParser.Parse(frameHtml, currentIp);
            return DeviceListResult.Ok(devices, frameUrl);
        }
        catch (OperationCanceledException)
        {
            return DeviceListResult.Fail("连接超时");
        }
        catch (Exception ex)
        {
            return DeviceListResult.Fail(Describe(ex));
        }
    }

    /// <summary>
    /// 强制下线某台设备（别的会话）。
    /// 走在线控制页自己的 type=dm 接口，成功后由调用方刷新列表。
    /// </summary>
    public async Task<bool> ForceOfflineAsync(string frameUrl, string sessionId, CancellationToken ct = default)
    {
        try
        {
            var url = DeviceListParser.BuildForceOfflineUrl(frameUrl, sessionId);
            using var resp = await _http.GetAsync(url, ct).ConfigureAwait(false);
            return resp.IsSuccessStatusCode;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 断开本机当前这条连接。
    /// 对应门户 userstatus.jsp 底部那个「断开连接」按钮（logoff）。
    /// </summary>
    public async Task<bool> DisconnectAsync(int port, CancellationToken ct = default)
    {
        try
        {
            var url = DeviceListParser.BuildDisconnectUrl(RootFor(port));
            using var resp = await _http.GetAsync(url, ct).ConfigureAwait(false);
            return resp.IsSuccessStatusCode;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>执行登录（无验证码路径）。</summary>
    public Task<LoginResult> LoginAsync(string account, string password, Operator op, CancellationToken ct = default)
        => LoginInternalAsync(account, password, op, null, ct);

    /// <summary>执行登录，带上用户填写的图形验证码。</summary>
    public Task<LoginResult> LoginAsync(
        string account, string password, Operator op, string captcha, CancellationToken ct = default)
        => LoginInternalAsync(account, password, op, captcha, ct);

    /// <summary>
    /// 登录主流程：先看是否已在线 → 抓登录页 → 需要验证码且没提供就返回 NeedCaptcha
    /// → 按页面里的 action + 隐藏域提交 → 判定结果。
    /// </summary>
    private async Task<LoginResult> LoginInternalAsync(
        string account, string password, Operator op, string? captcha, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(account)) return LoginResult.Fail("账号不能为空");
        if (string.IsNullOrEmpty(password)) return LoginResult.Fail("密码不能为空");

        account = account.Trim();

        // 账号自带 @mobile / @unicom / @telecom 后缀时，用后缀覆盖出口设置
        var yys = op switch
        {
            Operator.Mobile => "mobile",
            Operator.Unicom => "unicom",
            Operator.Telecom => "telecom",
            _ => ""
        };

        var at = account.IndexOf('@');
        if (at > 0)
        {
            var suffix = account[(at + 1)..].Trim().ToLowerInvariant();
            if (suffix is "mobile" or "unicom" or "telecom" or "campus" or "edu")
            {
                account = account[..at];
                yys = suffix is "campus" or "edu" ? "" : suffix;
            }
        }

        // 先探测：已在线则直接返回
        var status = await GetStatusAsync(ct).ConfigureAwait(false);
        if (status.Success) return status;

        LoginPage? page;
        try
        {
            page = await LoadLoginPageAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return LoginResult.Fail("连接超时");
        }
        catch (Exception ex)
        {
            return LoginResult.Fail(Describe(ex));
        }

        if (page is null) return LoginResult.Fail("无法解析登录页（Portal 可能已改版）");
        if (page.AlreadyOnline) return LoginResult.Online(null, null, null);

        if (!page.HasPasswordField && page.CaptchaField is null)
            return LoginResult.Fail("登录页里没有密码输入框（Portal 可能已改版）");

        // 页面带验证码但本次没带：不提交，先把需求抛给界面
        if (page.CaptchaField is { Length: > 0 } captchaField && string.IsNullOrWhiteSpace(captcha))
            return LoginResult.Captcha("本次登录需要输入图片验证码");

        var form = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        // 登录页里的隐藏域（NAS 参数、跳转地址等）原样带上，不同接入点差别很大
        foreach (var kv in page.Hidden)
        {
            if (kv.Key.Equals("account", StringComparison.OrdinalIgnoreCase)
                || kv.Key.Equals("password", StringComparison.OrdinalIgnoreCase)
                || kv.Key.Equals("checkcode", StringComparison.OrdinalIgnoreCase))
                continue;

            form[kv.Key] = kv.Value;
        }

        form["account"] = account;
        form["password"] = password;
        if (yys.Length > 0) form["yys"] = yys;

        if (page.CaptchaField is { Length: > 0 } cf && !string.IsNullOrWhiteSpace(captcha))
            form[cf] = captcha.Trim();

        // 把登录页 URL 上的 NAS 参数也补一份（有的接入点只认 query 里的值）
        foreach (var kv in ParseQuery(page.BaseUri?.Query ?? ""))
        {
            if (!form.ContainsKey(kv.Key)) form[kv.Key] = kv.Value;
        }

        var endpoints = new List<string>();
        if (page.FormAction is { Length: > 0 } action) endpoints.Add(action);
        endpoints.Add(Root + "login");
        endpoints.Add(Root + "login.jsp");

        string lastError = "登录失败";

        foreach (var url in endpoints.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                using var content = new StringContent(BuildFormBody(form), Gb, "application/x-www-form-urlencoded");
                using var resp = await _http.PostAsync(url, content, ct).ConfigureAwait(false);
                var html = await ReadHtmlAsync(resp, ct).ConfigureAwait(false);

                if (html.Contains("当前登录账号", StringComparison.Ordinal))
                {
                    var acc = Match(html, @"当前登录账号[：:]\s*(?:<[^>]+>\s*)*([^<\s]+)") ?? account;
                    var ip = Match(html, @"当前IP[：:]\s*(?:<[^>]+>\s*)*([0-9]{1,3}(?:\.[0-9]{1,3}){3})");
                    var time = Match(html, @"登录时间[：:]\s*(?:<[^>]+>\s*)*([0-9]{4}-[0-9]{2}-[0-9]{2}\s+[0-9]{2}:[0-9]{2}:[0-9]{2})");
                    return LoginResult.Ok("登录成功", acc, ip, time);
                }

                lastError = ExtractError(html) ?? lastError;

                // 还要求填验证码 => 上一次的码不对或已过期
                var again = ParseLoginPage(html, resp.RequestMessage?.RequestUri);
                if (again.CaptchaField is { Length: > 0 })
                {
                    _page = again;
                    return LoginResult.Captcha(lastError.Contains("验证码", StringComparison.Ordinal)
                        ? "验证码不正确，已刷新，请重新输入"
                        : lastError);
                }

                // 表单仍在 => 该端点不是登录处理入口，试下一个
                if (!Regex.IsMatch(html, @"name\s*=\s*[""']?password", RegexOptions.IgnoreCase))
                    break;
            }
            catch (OperationCanceledException)
            {
                return LoginResult.Fail("连接超时");
            }
            catch (Exception ex)
            {
                lastError = Describe(ex);
            }
        }

        // 最后确认一次真实状态（有的实现登录成功但页面跳转延迟）
        var verify = await GetStatusAsync(ct).ConfigureAwait(false);
        if (verify.Success) return verify;

        return LoginResult.Fail(lastError);
    }

    /// <summary>
    /// 取一张验证码图片。每次都重新拉一遍登录页，保证「图片 → 提交」落在同一个会话里
    /// （验证码是按会话存的，中间夹一次别的请求就可能被重置）。
    /// </summary>
    public async Task<CaptchaImage?> FetchCaptchaAsync(CancellationToken ct = default)
    {
        try
        {
            var page = await LoadLoginPageAsync(ct).ConfigureAwait(false);
            if (page is null || page.AlreadyOnline) return null;

            if (string.IsNullOrEmpty(page.CaptchaImageUrl)) return null;

            using var resp = await _http.GetAsync(page.CaptchaImageUrl, ct).ConfigureAwait(false);
            var media = resp.Content.Headers.ContentType?.MediaType ?? "";
            var bytes = await resp.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);

            // 会话失效时这个地址会返回登录页 HTML，不能当图片显示
            if (bytes.Length < 64 || !media.StartsWith("image", StringComparison.OrdinalIgnoreCase))
                return null;

            return new CaptchaImage(bytes, "看不清可以点「换一张」");
        }
        catch
        {
            return null;
        }
    }

    private async Task<LoginPage?> LoadLoginPageAsync(CancellationToken ct)
    {
        using var resp = await _http.GetAsync(Root + "login", ct).ConfigureAwait(false);
        var html = await ReadHtmlAsync(resp, ct).ConfigureAwait(false);

        if (html.Contains("当前登录账号", StringComparison.Ordinal))
            return new LoginPage { BaseUri = resp.RequestMessage?.RequestUri, AlreadyOnline = true };

        var page = ParseLoginPage(html, resp.RequestMessage?.RequestUri);
        _page = page;
        return page;
    }

    // ────────────────────────── 登录页解析 ──────────────────────────

    private static readonly Regex TagRegex = new(@"<(input|img|form)\b[^>]*>", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex AttrRegex = new(@"([a-zA-Z_:][-a-zA-Z0-9_:.]*)\s*=\s*([""'])(.*?)\2|([a-zA-Z_:][-a-zA-Z0-9_:.]*)\s*=\s*([^\s""'>]+)", RegexOptions.Compiled);

    /// <summary>
    /// 不依赖具体 Portal 模板：从 HTML 里找出表单地址、隐藏域，以及验证码的输入名与图片地址。
    /// 各校 Portal / NAS 页面写法差别很大，靠「属性名像不像验证码」来认比写死选择器可靠。
    /// </summary>
    internal static LoginPage ParseLoginPage(string html, Uri? baseUri)
    {
        string? formAction = null;
        bool hasPassword = false;
        string? captchaField = null;
        string? captchaImg = null;

        foreach (Match m in TagRegex.Matches(html))
        {
            var tag = m.Groups[1].Value.ToLowerInvariant();
            var attrs = ParseAttributes(m.Value);

            if (tag == "form")
            {
                if (formAction is null && attrs.TryGetValue("action", out var a) && !string.IsNullOrWhiteSpace(a))
                    formAction = a;
                continue;
            }

            if (tag == "input")
            {
                attrs.TryGetValue("name", out var name);
                attrs.TryGetValue("type", out var type);

                if (string.Equals(name, "password", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(type, "password", StringComparison.OrdinalIgnoreCase))
                    hasPassword = true;

                if (captchaField is null && !string.IsNullOrWhiteSpace(name) && LooksLikeCaptcha(name))
                    captchaField = name;

                if (string.Equals(type, "hidden", StringComparison.OrdinalIgnoreCase)
                    && !string.IsNullOrWhiteSpace(name))
                    continue;   // 隐藏域在下面统一收
                continue;
            }

            // img：src 里带 code / captcha / rand 之类字样
            if (tag == "img" && captchaImg is null
                && attrs.TryGetValue("src", out var src) && !string.IsNullOrWhiteSpace(src)
                && LooksLikeCaptcha(src))
                captchaImg = Absolute(baseUri, src);
        }

        // 隐藏域（name + value 同时存在）
        var hidden = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match m in TagRegex.Matches(html))
        {
            if (!m.Groups[1].Value.Equals("input", StringComparison.OrdinalIgnoreCase)) continue;
            var attrs = ParseAttributes(m.Value);
            if (!attrs.TryGetValue("type", out var t) || !t.Equals("hidden", StringComparison.OrdinalIgnoreCase)) continue;
            if (!attrs.TryGetValue("name", out var n) || string.IsNullOrWhiteSpace(n)) continue;

            hidden[n] = attrs.TryGetValue("value", out var v) ? v : "";
        }

        var page = new LoginPage
        {
            BaseUri = baseUri,
            FormAction = Absolute(baseUri, formAction),
            CaptchaField = captchaField,
            CaptchaImageUrl = captchaImg,
            HasPasswordField = hasPassword
        };

        foreach (var kv in hidden) page.Hidden[kv.Key] = kv.Value;

        return page;
    }

    private static bool LooksLikeCaptcha(string text)
    {
        var s = text.ToLowerInvariant();
        return s.Contains("checkcode") || s.Contains("captcha") || s.Contains("verifycode")
            || s.Contains("vcode") || s.Contains("authcode") || s.Contains("randcode")
            || s.Contains("/code") || s.Contains("code=") || s.Contains("validatecode");
    }

    private static Dictionary<string, string> ParseAttributes(string tag)
    {
        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (Match m in AttrRegex.Matches(tag))
        {
            var key = m.Groups[1].Success ? m.Groups[1].Value : m.Groups[4].Value;
            var val = m.Groups[1].Success ? m.Groups[3].Value : m.Groups[5].Value;
            if (string.IsNullOrEmpty(key)) continue;

            dict[key] = WebUtility.HtmlDecode(val);
        }

        return dict;
    }

    private static string? Absolute(Uri? baseUri, string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;
        url = url.Trim();

        if (Uri.TryCreate(url, UriKind.Absolute, out var abs)) return abs.ToString();
        if (baseUri is null) return url;

        return Uri.TryCreate(baseUri, url, out var rel) ? rel.ToString() : url;
    }

    private static string BuildFormBody(Dictionary<string, string> form)
    {
        var sb = new StringBuilder();
        foreach (var kv in form)
        {
            if (kv.Value is null) continue;
            if (sb.Length > 0) sb.Append('&');
            sb.Append(Uri.EscapeDataString(kv.Key)).Append('=').Append(Uri.EscapeDataString(kv.Value));
        }
        return sb.ToString();
    }

    private static async Task<string> ReadHtmlAsync(HttpResponseMessage resp, CancellationToken ct)
    {
        var bytes = await resp.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
        if (bytes.Length == 0) return string.Empty;

        var text = Gb.GetString(bytes);
        // 部分页面实际为 UTF-8 但声明 GB2312，出现大量替换符时回退
        if (text.Contains('\uFFFD'))
        {
            var utf8 = Encoding.UTF8.GetString(bytes);
            if (!utf8.Contains('\uFFFD')) return utf8;
        }
        return text;
    }

    private static IEnumerable<KeyValuePair<string, string>> ParseQuery(string query)
    {
        var list = new List<KeyValuePair<string, string>>();
        if (string.IsNullOrEmpty(query)) return list;
        var q = query.TrimStart('?');
        foreach (var part in q.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var idx = part.IndexOf('=');
            if (idx <= 0) continue;
            var k = Uri.UnescapeDataString(part[..idx]);
            var v = Uri.UnescapeDataString(part[(idx + 1)..]);
            list.Add(new KeyValuePair<string, string>(k, v));
        }
        return list;
    }

    private static string? Match(string html, string pattern)
    {
        var m = Regex.Match(html, pattern, RegexOptions.IgnoreCase);
        return m.Success ? WebUtility.HtmlDecode(m.Groups[1].Value.Trim()) : null;
    }

    /// <summary>从 Portal 返回的页面里提取人话错误信息。</summary>
    private static string? ExtractError(string html)
    {
        if (string.IsNullOrEmpty(html)) return null;

        var m = Regex.Match(html, @"alert\s*\(\s*[""']([^""']{2,80})[""']", RegexOptions.IgnoreCase);
        if (m.Success) return m.Groups[1].Value;

        var known = new[]
        {
            "密码错误", "密码不正确", "账号不存在", "用户名不存在", "账号或密码",
            "已欠费", "余额不足", "已达到", "超过", "停机", "被锁定", "验证码错误",
            "认证失败", "登录失败", "该账号", "不在服务范围", "绑定"
        };
        var plain = Regex.Replace(html, "<script[\\s\\S]*?</script>", " ");
        plain = Regex.Replace(plain, "<[^>]+>", " ");
        plain = WebUtility.HtmlDecode(plain);
        plain = Regex.Replace(plain, "\\s+", " ").Trim();

        foreach (var k in known)
        {
            var i = plain.IndexOf(k, StringComparison.Ordinal);
            if (i < 0) continue;
            var start = Math.Max(0, i - 20);
            var len = Math.Min(plain.Length - start, 60);
            return plain.Substring(start, len).Trim();
        }
        return null;
    }

    private static string Describe(Exception ex) => ex switch
    {
        HttpRequestException hre when hre.InnerException is System.Net.Sockets.SocketException => "无法连接 Portal（检查是否已接入校园网）",
        HttpRequestException => "无法连接 Portal（检查是否已接入校园网）",
        TaskCanceledException => "连接超时",
        _ => ex.Message
    };

    public void Dispose() => _http.Dispose();
}
