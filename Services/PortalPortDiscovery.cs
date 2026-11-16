using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace SNNUAutoLogin.Services;

/// <summary>
/// 门户接入端口的发现者（真实实现）。
///
/// 校园网的接入端口是**服务端按线路临时分配**的：
/// 访问 `http://202.117.144.205/` 会返回一段 JS
/// `window.location='http://202.117.144.205:8601/snnuportal/login.jsp'`，
/// 端口就写在跳转目标里。实测连续请求会得到 8601 / 8602 / 8603 不一样的端口，
/// 与「有线还是无线」无关 —— 也就意味着它**不是用户能选的东西**。
///
/// 所以程序自己去读门户的分配结果，而不是让用户从下拉框里挑。
/// </summary>
public sealed class PortalPortDiscovery : IPortalPortDiscovery, IDisposable
{
    /// <summary>发现结果的缓存时长。门户的分配在一次会话内基本不变，缓存 30 秒足够。</summary>
    private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(30);

    private readonly Func<CancellationToken, Task<int?>> _discover;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _cacheGate = new();
    private int _port;
    private long _foundAt;
    private int _networkVersion;

    public PortalPortDiscovery(PortalClient client)
    {
        _discover = client.DiscoverAssignedPortAsync;
    }

    internal PortalPortDiscovery(Func<CancellationToken, Task<int?>> discover)
    {
        _discover = discover;
    }

    /// <summary>
    /// 取门户当前分配的接入端口。
    ///
    /// 拿不到时返回 null —— 调用方应退回兜底端口继续工作，不能因为读不到就瘫掉。
    /// </summary>
    public async Task<int?> DiscoverAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            int version;
            lock (_cacheGate)
            {
                if (_port > 0 && Stopwatch.GetElapsedTime(_foundAt) < CacheTtl)
                    return _port;
                version = _networkVersion;
            }

            var port = await _discover(ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            lock (_cacheGate)
            {
                if (version != _networkVersion) return null;
                if (port is > 0)
                {
                    _port = port.Value;
                    _foundAt = Stopwatch.GetTimestamp();
                }
                return port;
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch
        {
            return null;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>门户换了线路或重连后，缓存要作废。</summary>
    public void Invalidate()
    {
        lock (_cacheGate)
        {
            _networkVersion++;
            _port = 0;
        }
    }

    /// <summary>当前已知的端口（没发现过就是 null）。</summary>
    public int? CurrentPort
    {
        get { lock (_cacheGate) return _port > 0 ? _port : null; }
    }

    public void Dispose() => _gate.Dispose();
}
