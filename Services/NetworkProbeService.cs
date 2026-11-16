using System;
using System.Diagnostics;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Threading;
using System.Threading.Tasks;

namespace SNNUAutoLogin.Services;

/// <summary>探测使用的传输方式。由目标地址的形态决定，界面上不暴露这个选项。</summary>
public enum ProbeTransport
{
    /// <summary>ICMP（目标是 IP 字面量）。</summary>
    Icmp = 0,

    /// <summary>HTTP（目标是域名或 URL）。</summary>
    Http = 1
}

/// <summary>一次探测的结果。</summary>
public sealed record ProbeResult
{
    /// <summary>是否探测通过。</summary>
    public bool Success { get; init; }

    /// <summary>耗时（毫秒）。</summary>
    public long LatencyMs { get; init; }

    /// <summary>人话描述，写入日志与卡片。</summary>
    public string Detail { get; init; } = "";

    /// <summary>实际使用的传输方式。</summary>
    public ProbeTransport Transport { get; init; }
}

/// <summary>
/// 独立外网探测：判断这台机器「现在到底能不能出网」。
///
/// 和门户认证状态是两回事 —— 门户说「已连接」不代表真的能访问外网，
/// 会话挂着但出口已经废掉的情况在校园网里很常见。
/// </summary>
public interface INetworkProbe
{
    /// <summary>
    /// 按目标地址与超时做一次出网探测。
    /// 任何异常都转成失败结果，不向调用方抛。
    /// </summary>
    Task<ProbeResult> ProbeAsync(string target, TimeSpan timeout, CancellationToken ct);
}

/// <summary>
/// 探测服务实现。
///
/// 协议由目标地址形态隐含：IP 字面量走 ICMP，域名或 URL 走 HTTP。
/// 这样用户换一下目标地址就等于换协议，既不用在界面上多一个下拉框，
/// 也能绕开「校园网屏蔽 ICMP」和「Web 认证门户劫持 HTTP」两种坑。
/// </summary>
public sealed class NetworkProbeService : INetworkProbe, IDisposable
{
    /// <summary>
    /// 默认探测目标：学校官网 www.snnu.edu.cn。
    ///
    /// 用域名（而不是 IP 字面量）意味着走 HTTP —— 校园网普遍屏蔽 ICMP，
    /// 用域名还能顺带验证 DNS 解析是否正常，比 ping 某个公网 IP 更贴近
    /// 「能不能上网」这件事本身。
    /// </summary>
    public const string DefaultTarget = "www.snnu.edu.cn";
    public const string DefaultExternalTarget = "www.baidu.com";

    /// <summary>
    /// 旧版默认探测目标（公网 DNS）。留着只为识别「还没自定义过」的老配置，
    /// 迁移到 <see cref="DefaultTarget"/> 后就不再出现。
    /// </summary>
    public const string LegacyDefaultTarget = "223.5.5.5";

    /// <summary>默认探测超时（秒）。</summary>
    public const int DefaultTimeoutSeconds = 3;

    private readonly HttpClient _http;
    private bool _disposed;

    public NetworkProbeService() : this(new SocketsHttpHandler
        {
            AllowAutoRedirect = false,   // 门户会把请求 302 到登录页，跟随重定向会把「被劫持」当成「能出网」
            UseProxy = false,             // 校园网门户在内网，走代理反而连不上
            ConnectTimeout = Timeout.InfiniteTimeSpan
        })
    {
    }

    internal NetworkProbeService(HttpMessageHandler handler)
    {
        _http = new HttpClient(handler)
        {
            Timeout = Timeout.InfiniteTimeSpan   // 超时由我们自己按预算控制
        };

        _http.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) SNNUAutoLogin/1.0");
    }

    /// <summary>
    /// 探测目标是否合法。空、超长、不是 IP 也不是域名 → 判为非法。
    /// </summary>
    public static bool IsValidTarget(string? target)
    {
        if (string.IsNullOrWhiteSpace(target)) return false;
        string t = target.Trim();
        if (t.Length > 255) return false;
        if (t.Contains(' ')) return false;
        return true;
    }

    /// <summary>把任意输入规整成可用的探测目标；非法时回落到默认地址。</summary>
    public static string NormalizeTarget(string? target)
    {
        if (!IsValidTarget(target)) return DefaultTarget;
        return target!.Trim();
    }

    /// <summary>把超时秒数钳制到 1–30。</summary>
    public static int NormalizeTimeoutSeconds(int seconds)
        => seconds is < 1 or > 30 ? DefaultTimeoutSeconds : seconds;

    /// <summary>判断目标该走哪种协议：能解析成 IP 就是 ICMP，否则 HTTP。</summary>
    public static ProbeTransport TransportOf(string target)
    {
        string t = (target ?? "").Trim();
        if (t.Contains("://")) return ProbeTransport.Http;

        // 纯 IP 字面量（含 IPv4 / IPv6）
        if (System.Net.IPAddress.TryParse(t, out _)) return ProbeTransport.Icmp;

        // 带端口或路径的也算域名
        return ProbeTransport.Http;
    }

    public async Task<ProbeResult> ProbeAsync(string target, TimeSpan timeout, CancellationToken ct)
    {
        string t = NormalizeTarget(target);
        if (timeout <= TimeSpan.Zero) timeout = TimeSpan.FromSeconds(DefaultTimeoutSeconds);

        return TransportOf(t) == ProbeTransport.Icmp
            ? await ProbeIcmpAsync(t, timeout, ct).ConfigureAwait(false)
            : await ProbeHttpAsync(t, timeout, ct).ConfigureAwait(false);
    }

    private async Task<ProbeResult> ProbeIcmpAsync(string target, TimeSpan timeout, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();

        try
        {
            using var ping = new Ping();
            var reply = await ping.SendPingAsync(System.Net.IPAddress.Parse(target), timeout,
                Array.Empty<byte>(), null, ct).ConfigureAwait(false);
            sw.Stop();

            return reply.Status switch
            {
                IPStatus.Success => new ProbeResult
                {
                    Success = true,
                    LatencyMs = reply.RoundtripTime,
                    Transport = ProbeTransport.Icmp,
                    Detail = $"出网正常（{reply.RoundtripTime} ms）"
                },
                IPStatus.TimedOut => new ProbeResult
                {
                    Success = false,
                    LatencyMs = sw.ElapsedMilliseconds,
                    Transport = ProbeTransport.Icmp,
                    Detail = $"探测超时（{timeout.TotalSeconds:0.#} 秒无响应）"
                },
                _ => new ProbeResult
                {
                    Success = false,
                    LatencyMs = sw.ElapsedMilliseconds,
                    Transport = ProbeTransport.Icmp,
                    Detail = $"探测失败：{reply.Status}"
                }
            };
        }
        catch (OperationCanceledException)
        {
            return new ProbeResult
            {
                Success = false,
                LatencyMs = sw.ElapsedMilliseconds,
                Transport = ProbeTransport.Icmp,
                Detail = "探测已取消"
            };
        }
        catch (Exception ex)
        {
            return new ProbeResult
            {
                Success = false,
                LatencyMs = sw.ElapsedMilliseconds,
                Transport = ProbeTransport.Icmp,
                Detail = $"探测失败：{ex.Message}"
            };
        }
    }

    private async Task<ProbeResult> ProbeHttpAsync(string target, TimeSpan timeout, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        string url = target.Contains("://") ? target : "https://" + target;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            || uri.UserInfo.Length != 0)
        {
            return new ProbeResult { Transport = ProbeTransport.Http, Detail = "探测地址无效" };
        }

        using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
        budget.CancelAfter(timeout);

        ProbeResult Result(bool success, string detail) => new()
        {
            Success = success,
            LatencyMs = sw.ElapsedMilliseconds,
            Transport = ProbeTransport.Http,
            Detail = $"{uri.Scheme.ToUpperInvariant()} {detail}"
        };

        try
        {
            using var response = await _http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead,
                budget.Token).ConfigureAwait(false);
            int status = (int)response.StatusCode;
            // 只接受同主机的 HTTP 到 HTTPS 升级，门户重定向仍判失败。
            if (status is 301 or 302 or 303 or 307 or 308
                && uri.Scheme == Uri.UriSchemeHttp
                && Uri.TryCreate(uri, response.Headers.Location, out var redirect)
                && redirect.Scheme == Uri.UriSchemeHttps
                && redirect.UserInfo.Length == 0
                && string.Equals(uri.IdnHost, redirect.IdnHost, StringComparison.OrdinalIgnoreCase))
            {
                uri = redirect;
                using var upgraded = await _http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead,
                    budget.Token).ConfigureAwait(false);
                status = (int)upgraded.StatusCode;
            }

            return status is >= 200 and < 300
                ? Result(true, $"正常（{status} · {sw.ElapsedMilliseconds} ms）")
                : Result(false, $"响应异常（{status}）");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return Result(false, "探测已取消");
        }
        catch (OperationCanceledException) when (budget.IsCancellationRequested)
        {
            return Result(false, $"探测超时（{timeout.TotalSeconds:0.#} 秒无响应）");
        }
        catch (HttpRequestException ex)
        {
            return Result(false, $"连接失败：{ex.Message}");
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _http.Dispose();
    }
}
