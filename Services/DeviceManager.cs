using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace SNNUAutoLogin.Services;

/// <summary>在线设备列表的界面状态。</summary>
public enum DeviceListState
{
    /// <summary>还没查过。</summary>
    Idle,

    /// <summary>正在加载。</summary>
    Loading,

    /// <summary>列表已就绪（可能为空）。</summary>
    Ready,

    /// <summary>出错了，错误文案在 <see cref="DeviceManager.Error"/>。</summary>
    Failed
}

/// <summary>
/// 在线设备管理。
///
/// 流程和门户网页一致：
///   1. 访问某个接入端口的 userstatus.jsp；
///   2. 未登录时这个页面没有设备表格 —— 先登录，登录成功后会跳回这个页面；
///   3. 页面里的 iframe（在线控制页）里是设备表格，解析出来展示；
///   4. 下线别的设备走在线控制页自己的 type=dm 接口。
///
/// 界面只管读状态、发命令；网络操作都在后台线程跑。
/// </summary>
public sealed class DeviceManager : IDisposable, IAsyncDisposable
{
    private readonly IOnlineDevicePortal _client;
    private readonly bool _ownsClient;
    private readonly IEventDispatcher _dispatcher;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private readonly OperationLifetime _operations = new();
    private bool _disposed;

    /// <summary>在线设备列表。</summary>
    public IReadOnlyList<OnlineDevice> Devices { get; private set; } = Array.Empty<OnlineDevice>();

    /// <summary>列表状态。</summary>
    public DeviceListState State { get; private set; } = DeviceListState.Idle;

    /// <summary>最近一次错误文案（成功时清空）。</summary>
    public string Error { get; private set; } = "";

    /// <summary>当前查看的接入端口。</summary>
    public int Port { get; private set; } = 8603;

    /// <summary>在线控制页地址；下线时要用它拼 type=dm 的地址。</summary>
    private sealed record Snapshot(int Port, IReadOnlyList<OnlineDevice> Devices, string? FrameUrl);
    private readonly object _snapshotGate = new();
    private Snapshot? _snapshot;
    private int _networkVersion;
    private bool _canOperate;
    private int _requestedPort = 8603;

    public void Invalidate()
    {
        lock (_snapshotGate)
        {
            InvalidateSnapshot();
        }
        RaiseChanged();
    }

    private void InvalidateSnapshot()
    {
        _networkVersion++;
        _snapshot = null;
        _canOperate = false;
        Devices = Array.Empty<OnlineDevice>();
        State = DeviceListState.Idle;
        Error = "";
    }

    /// <summary>列表变化（内容、状态或错误有变动）。</summary>
    public event Action? Changed;

    /// <param name="dispatcher">界面线程调度器。</param>
    /// <param name="client">门户实现；由本类负责释放。</param>
    public DeviceManager(IEventDispatcher dispatcher, IOnlineDevicePortal client)
    {
        _dispatcher = dispatcher;
        _client = client;
        _ownsClient = true;
    }

    /// <summary>测试用：注入自己的门户实现（ownsClient=false 时不负责释放它）。</summary>
    public DeviceManager(IEventDispatcher dispatcher, IOnlineDevicePortal client, bool ownsClient)
    {
        _dispatcher = dispatcher;
        _client = client;
        _ownsClient = ownsClient;
    }

    /// <summary>拉取列表。port 为 0 表示沿用当前端口。</summary>
    public async Task RefreshAsync(int port = 0, CancellationToken ct = default)
    {
        if (_disposed) return;
        using var operation = _operations.TryEnter(ct);
        if (operation is null) return;
        ct = operation.Token;
        int version;
        bool portChanged;
        lock (_snapshotGate)
        {
            portChanged = port > 0 && port != _requestedPort;
            if (portChanged)
            {
                _requestedPort = port;
                InvalidateSnapshot();
            }
            port = _requestedPort;
            version = _networkVersion;
        }
        if (portChanged) RaiseChanged();

        if (!await WaitForGateAsync(ct).ConfigureAwait(false)) return;
        try
        {
            lock (_snapshotGate)
            {
                if (version != _networkVersion) return;
                _canOperate = false;
            }
            SetState(DeviceListState.Loading, "", version);
            await ReloadAsync(port, version, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            SetState(DeviceListState.Idle, "", version);
        }
        catch (Exception ex)
        {
            SetState(DeviceListState.Failed, ex.Message, version);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// 下线一台设备。
    /// 本机当前这条连接只能「断开」（logoff），别的会话才是「强制下线」（type=dm）。
    /// </summary>
    public async Task<bool> TakeOfflineAsync(OnlineDevice device, CancellationToken ct = default)
    {
        if (_disposed || device is null) return false;
        using var operation = _operations.TryEnter(ct);
        if (operation is null) return false;
        ct = operation.Token;
        int version = Volatile.Read(ref _networkVersion);

        if (!await WaitForGateAsync(ct).ConfigureAwait(false)) return false;
        try
        {
            Snapshot snapshot;
            lock (_snapshotGate)
            {
                if (version != _networkVersion || !_canOperate || _snapshot is null
                    || !System.Linq.Enumerable.Contains(_snapshot.Devices, device)) return false;
                snapshot = _snapshot;
                _canOperate = false;
            }
            device.Status = "下线中";
            RaiseChanged();

            bool ok;
            string message;

            if (device.IsCurrent || !device.CanForceOffline)
            {
                // 本机这条连接：走 logoff 断开
                if (version != Volatile.Read(ref _networkVersion)) return false;
                ok = await _client.DisconnectAsync(snapshot.Port, ct).ConfigureAwait(false);
                message = ok ? "已断开本机连接" : "断开失败";
            }
            else
            {
                if (snapshot.FrameUrl is null)
                {
                    device.Status = "在线";
                    SetState(DeviceListState.Failed, "在线控制页地址失效，请刷新列表", version);
                    return false;
                }

                if (version != Volatile.Read(ref _networkVersion)) return false;
                ok = await _client.ForceOfflineAsync(snapshot.Port, snapshot.FrameUrl, device.SessionId!, ct).ConfigureAwait(false);
                message = ok ? $"{device.DisplayName} 已下线" : "下线失败";
            }

            device.Status = ok ? "已下线" : "在线";

            // 下线后端口的在线控制页地址会变，重新拉一次列表
            await ReloadAsync(snapshot.Port, version, ct).ConfigureAwait(false);

            if (!ok) SetState(DeviceListState.Failed, message, version);

            return ok;
        }
        catch (OperationCanceledException)
        {
            device.Status = "在线";
            RaiseChanged();
            return false;
        }
        catch (Exception ex)
        {
            device.Status = "在线";
            SetState(DeviceListState.Failed, ex.Message, version);
            return false;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// 断开本机当前这条连接 —— 对应门户 userstatus.jsp 底部那个「断开连接」按钮。
    ///
    /// 与 <see cref="TakeOfflineAsync"/> 的区别：那个踢的是设备表格里的**别人**，
    /// 这个断的是**本机**（logoff）。断开后本机掉线，巡检会按原有设置自动重新认证
    /// —— 与门户网页上的行为一致。
    /// </summary>
    public async Task<bool> DisconnectSelfAsync(CancellationToken ct = default)
    {
        if (_disposed) return false;
        using var operation = _operations.TryEnter(ct);
        if (operation is null) return false;
        ct = operation.Token;
        int version = Volatile.Read(ref _networkVersion);

        if (!await WaitForGateAsync(ct).ConfigureAwait(false)) return false;
        try
        {
            int port;
            lock (_snapshotGate)
            {
                if (version != _networkVersion || !_canOperate || _snapshot is null) return false;
                port = _snapshot.Port;
                _canOperate = false;
            }
            SetState(DeviceListState.Loading, "", version);

            if (version != Volatile.Read(ref _networkVersion)) return false;
            var ok = await _client.DisconnectAsync(port, ct).ConfigureAwait(false);

            if (!ok)
            {
                SetState(DeviceListState.Failed, "断开失败，请检查是否仍在校园网内", version);
                return false;
            }

            // 断开后本机已不在在线列表里；重新拉一次让界面反映真实状态
            await ReloadAsync(port, version, ct).ConfigureAwait(false);

            return true;
        }
        catch (OperationCanceledException)
        {
            SetState(DeviceListState.Idle, "", version);
            return false;
        }
        catch (Exception ex)
        {
            SetState(DeviceListState.Failed, ex.Message, version);
            return false;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>重新拉列表（不释放 gate，调用方已经持有）。</summary>
    private async Task ReloadAsync(int port, int version, CancellationToken ct)
    {
        try
        {
            if (version != Volatile.Read(ref _networkVersion)) return;
            var result = await _client.GetOnlineDevicesAsync(port, ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            if (!result.Success)
            {
                SetState(DeviceListState.Failed, result.Message, version);
                return;
            }

            var devices = new List<OnlineDevice>(result.Devices.Count);
            foreach (var d in result.Devices)
            {
                d.Ports.Add(port);
                devices.Add(d);
            }

            lock (_snapshotGate)
            {
                if (version != _networkVersion) return;
                _snapshot = new Snapshot(port, devices, result.FrameUrl);
                _canOperate = true;
                Port = port;
                Devices = devices;
                State = DeviceListState.Ready;
                Error = "";
            }
            RaiseChanged();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) { SetState(DeviceListState.Failed, ex.Message, version); }
    }

    private void SetState(DeviceListState state, string error, int version)
    {
        lock (_snapshotGate)
        {
            if (version != _networkVersion) return;
            State = state;
            Error = error;
        }
        RaiseChanged();
    }

    private void RaiseChanged()
    {
        if (_disposed) return;

        if (_dispatcher.HasThreadAccess)
        {
            try { Changed?.Invoke(); } catch { }
        }
        else
        {
            _dispatcher.TryEnqueue(() =>
            {
                try { Changed?.Invoke(); } catch { }
            });
        }
    }

    private async Task<bool> WaitForGateAsync(CancellationToken ct)
    {
        try
        {
            await _gate.WaitAsync(ct).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { return false; }
    }

    public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        await _operations.StopAsync().ConfigureAwait(false);

        if (_ownsClient) _client.Dispose();
        _gate.Dispose();
    }
}
