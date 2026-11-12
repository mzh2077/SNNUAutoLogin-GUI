using System;
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

    private readonly PortalClient _client;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private int _port;
    private DateTime _foundAt = DateTime.MinValue;

    public PortalPortDiscovery(PortalClient client)
    {
        _client = client;
    }

    /// <summary>
    /// 取门户当前分配的接入端口。
    ///
    /// 拿不到时返回 null —— 调用方应退回兜底端口继续工作，不能因为读不到就瘫掉。
    /// </summary>
    public async Task<int?> DiscoverAsync(CancellationToken ct = default)
    {
        // 缓存命中就直接返回，省掉一次网络往返
        if (DateTime.Now - _foundAt < CacheTtl && _port > 0)
            return _port;

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // 双重检查：等锁期间可能已经有别的线程拿到了
            if (DateTime.Now - _foundAt < CacheTtl && _port > 0)
                return _port;

            var port = await _client.DiscoverAssignedPortAsync(ct).ConfigureAwait(false);
            if (port is > 0)
            {
                _port = port.Value;
                _foundAt = DateTime.Now;
            }

            return port;
        }
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
        _foundAt = DateTime.MinValue;
    }

    /// <summary>当前已知的端口（没发现过就是 null）。</summary>
    public int? CurrentPort => _port > 0 ? _port : null;

    public void Dispose() => _gate.Dispose();
}
