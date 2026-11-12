using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Dispatching;

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
public sealed class DeviceManager : IDisposable
{
    private readonly IOnlineDevicePortal _client;
    private readonly bool _ownsClient;
    private readonly DispatcherQueue _dispatcher;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private CancellationTokenSource? _cts;
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
    private string? _frameUrl;

    /// <summary>列表变化（内容、状态或错误有变动）。</summary>
    public event Action? Changed;

    /// <param name="dispatcher">界面线程调度器。</param>
    /// <param name="client">门户实现；由本类负责释放。</param>
    public DeviceManager(DispatcherQueue dispatcher, IOnlineDevicePortal client)
    {
        _dispatcher = dispatcher;
        _client = client;
        _ownsClient = true;
    }

    /// <summary>测试用：注入自己的门户实现（ownsClient=false 时不负责释放它）。</summary>
    public DeviceManager(DispatcherQueue dispatcher, IOnlineDevicePortal client, bool ownsClient)
    {
        _dispatcher = dispatcher;
        _client = client;
        _ownsClient = ownsClient;
    }

    /// <summary>拉取列表。port 为 0 表示沿用当前端口。</summary>
    public async Task RefreshAsync(int port = 0, CancellationToken ct = default)
    {
        if (_disposed) return;

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (port > 0) Port = port;

            SetState(DeviceListState.Loading, "");

            var result = await _client.GetOnlineDevicesAsync(Port, ct).ConfigureAwait(false);

            if (!result.Success)
            {
                SetState(DeviceListState.Failed, result.Message);
                return;
            }

            _frameUrl = result.FrameUrl;

            // 把端口记到每台设备上：同一账号可以在多个接入端口上同时在线
            var devices = new List<OnlineDevice>(result.Devices.Count);
            foreach (var d in result.Devices)
            {
                d.Ports.Add(Port);
                devices.Add(d);
            }

            Devices = devices;
            SetState(DeviceListState.Ready, "");
        }
        catch (OperationCanceledException)
        {
            SetState(DeviceListState.Idle, "");
        }
        catch (Exception ex)
        {
            SetState(DeviceListState.Failed, ex.Message);
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

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            device.Status = "下线中";
            RaiseChanged();

            bool ok;
            string message;

            if (device.IsCurrent || !device.CanForceOffline)
            {
                // 本机这条连接：走 logoff 断开
                ok = await _client.DisconnectAsync(Port, ct).ConfigureAwait(false);
                message = ok ? "已断开本机连接" : "断开失败";
            }
            else
            {
                if (_frameUrl is null)
                {
                    device.Status = "在线";
                    SetState(DeviceListState.Failed, "在线控制页地址失效，请刷新列表");
                    return false;
                }

                ok = await _client.ForceOfflineAsync(_frameUrl, device.SessionId!, ct).ConfigureAwait(false);
                message = ok ? $"{device.DisplayName} 已下线" : "下线失败";
            }

            device.Status = ok ? "已下线" : "在线";

            // 下线后端口的在线控制页地址会变，重新拉一次列表
            await ReloadAsync(ct).ConfigureAwait(false);

            if (!ok) SetState(DeviceListState.Failed, message);

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
            SetState(DeviceListState.Failed, ex.Message);
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

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            SetState(DeviceListState.Loading, "");

            var ok = await _client.DisconnectAsync(Port, ct).ConfigureAwait(false);

            if (!ok)
            {
                SetState(DeviceListState.Failed, "断开失败，请检查是否仍在校园网内");
                return false;
            }

            // 断开后本机已不在在线列表里；重新拉一次让界面反映真实状态
            await ReloadAsync(ct).ConfigureAwait(false);

            return true;
        }
        catch (OperationCanceledException)
        {
            SetState(DeviceListState.Idle, "");
            return false;
        }
        catch (Exception ex)
        {
            SetState(DeviceListState.Failed, ex.Message);
            return false;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>重新拉列表（不释放 gate，调用方已经持有）。</summary>
    private async Task ReloadAsync(CancellationToken ct)
    {
        try
        {
            var result = await _client.GetOnlineDevicesAsync(Port, ct).ConfigureAwait(false);
            if (!result.Success) return;

            _frameUrl = result.FrameUrl;

            var devices = new List<OnlineDevice>(result.Devices.Count);
            foreach (var d in result.Devices)
            {
                d.Ports.Add(Port);
                devices.Add(d);
            }

            Devices = devices;
            SetState(DeviceListState.Ready, "");
        }
        catch { /* 刷新失败保留原列表 */ }
    }

    private void SetState(DeviceListState state, string error)
    {
        State = state;
        Error = error;
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

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _cts?.Cancel();
        _cts = null;

        if (_ownsClient) _client.Dispose();
        _gate.Dispose();
    }
}
