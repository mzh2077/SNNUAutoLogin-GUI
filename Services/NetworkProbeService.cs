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
    /// <summary>默认探测目标。</summary>
    public const string DefaultTarget = "223.5.5.5";

    /// <summary>默认探测超时（秒）。</summary>
    public const int DefaultTimeoutSeconds = 3;

    private readonly HttpClient _http;
    private bool _disposed;

    public NetworkProbeService()
    {
        _http = new HttpClient(new SocketsHttpHandler
        {
            AllowAutoRedirect = false,   // 门户会把请求 302 到登录页，跟随重定向会把「被劫持」当成「能出网」
            UseProxy = false,             // 校园网门户在内网，走代理反而连不上
            ConnectTimeout = TimeSpan.FromSeconds(5)
        })
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

    /// <summary>
    /// ICMP 探测。
    ///
    /// .NET 8 的 Ping 没有带 CancellationToken 的重载，光靠 timeout 参数
    /// 不够 —— 那只是「等多久算超时」，底层 socket 还可能挂着。
    /// 这里用 WhenAny 兜一层，保证调用方取消后能立刻返回。
    /// </summary>
    private async Task<ProbeResult> ProbeIcmpAsync(string target, TimeSpan timeout, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();

        try
        {
            using var ping = new Ping();
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeout);

            var task = ping.SendPingAsync(target, (int)timeout.TotalMilliseconds);
            var delay = Task.Delay(timeout, CancellationToken.None);
            var done = await Task.WhenAny(task, delay).ConfigureAwait(false);

            if (done != task)
            {
                // 超时或被取消：Ping 任务还在后台跑，直接放弃它
                return new ProbeResult
                {
                    Success = false,
                    LatencyMs = sw.ElapsedMilliseconds,
                    Transport = ProbeTransport.Icmp,
                    Detail = ct.IsCancellationRequested ? "探测已取消" : $"探测超时（{timeout.TotalSeconds:0.#} 秒无响应）"
                };
            }

            var reply = await task.ConfigureAwait(false);
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
                    Detail = "探测超时，目标不可达"
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

    /// <summary>
    /// HTTP 探测：只有 2xx 才算通过。
    /// 3xx 一律判失败 —— 校园网的 Web 认证门户会把任意请求重定向到登录页，
    /// 跟随重定向会把「被门户劫持」误判成「能出网」。
    /// </summary>
    private async Task<ProbeResult> ProbeHttpAsync(string target, TimeSpan timeout, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();

        try
        {
            string url = target.Contains("://") ? target : "http://" + target;

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeout);

            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            using var resp = await _http
                .SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token)
                .ConfigureAwait(false);

            sw.Stop();

            bool ok = (int)resp.StatusCode is >= 200 and < 300;
            return new ProbeResult
            {
                Success = ok,
                LatencyMs = sw.ElapsedMilliseconds,
                Transport = ProbeTransport.Http,
                Detail = ok
                    ? $"出网正常（HTTP {(int)resp.StatusCode} · {sw.ElapsedMilliseconds} ms）"
                    : $"出网异常（HTTP {(int)resp.StatusCode}）"
            };
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return new ProbeResult
            {
                Success = false,
                LatencyMs = sw.ElapsedMilliseconds,
                Transport = ProbeTransport.Http,
                Detail = "探测已取消"
            };
        }
        catch (OperationCanceledException)
        {
            return new ProbeResult
            {
                Success = false,
                LatencyMs = sw.ElapsedMilliseconds,
                Transport = ProbeTransport.Http,
                Detail = $"探测超时（{timeout.TotalSeconds:0.#} 秒无响应）"
            };
        }
        catch (HttpRequestException ex)
        {
            return new ProbeResult
            {
                Success = false,
                LatencyMs = sw.ElapsedMilliseconds,
                Transport = ProbeTransport.Http,
                Detail = $"探测失败：{ex.Message}"
            };
        }
        catch (Exception ex)
        {
            return new ProbeResult
            {
                Success = false,
                LatencyMs = sw.ElapsedMilliseconds,
                Transport = ProbeTransport.Http,
                Detail = $"探测失败：{ex.Message}"
            };
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _http.Dispose();
    }
}
