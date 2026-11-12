using System;
using System.Threading;
using System.Threading.Tasks;

namespace SNNUAutoLogin.Services;

/// <summary>设备数守卫的状态。</summary>
public enum DeviceGuardState
{
    /// <summary>守卫关闭。</summary>
    Disabled = 0,

    /// <summary>等待下一轮评估。</summary>
    Idle = 1,

    /// <summary>正在探测外网连通性。</summary>
    Probing = 2,

    /// <summary>正在断开本机连接。</summary>
    Disposing = 3,

    /// <summary>正在重新认证。</summary>
    Reauthenticating = 4,

    /// <summary>冷却中。</summary>
    Cooldown = 5,

    /// <summary>连续处置失败达上限，已停止自动处置。</summary>
    Stopped = 6
}

/// <summary>守卫卡片要显示的状态。</summary>
public sealed record DeviceGuardStatus
{
    /// <summary>当前状态。</summary>
    public DeviceGuardState State { get; init; } = DeviceGuardState.Disabled;

    /// <summary>最近一次取得的在线设备数（含本机）。</summary>
    public int DeviceCount { get; init; }

    /// <summary>设备数阈值。</summary>
    public int Threshold { get; init; } = 2;

    /// <summary>统计端口（当前生效出口端口）。</summary>
    public int StatisticPort { get; init; } = 8603;

    /// <summary>最近一次探测时间。</summary>
    public DateTime? LastProbeAt { get; init; }

    /// <summary>最近一次探测是否通过。</summary>
    public bool? LastProbeOk { get; init; }

    /// <summary>最近一次结果的人话描述。</summary>
    public string LastMessage { get; init; } = "";

    /// <summary>冷却结束时间。</summary>
    public DateTime? NextAllowedAt { get; init; }

    /// <summary>处置进行中（界面据此禁用按钮）。</summary>
    public bool Busy { get; init; }
}

/// <summary>
/// 设备数守卫。
///
/// 逻辑很直白：账号下的在线设备数超过阈值，说明可能有人在蹭网或者会话已经废了；
/// 这时候拿独立的外网探测确认一下「我这台机器到底还能不能出网」——
/// 出不去就说明本机会话有问题，于是断开本机连接再重新登录一次，换一条干净会话。
///
/// 并发上有三条铁律：
///   1. 守卫有自己的锁，和巡检、设备列表的锁**永不嵌套**；
///   2. 唤醒巡检走的是无锁的 RequestReauthenticate，不会在持锁时去要巡检的锁；
///   3. 取设备数自己向门户要，不经过 DeviceManager —— 否则会把用户在
///      「在线设备」卡片上选的端口冲掉，还会跟那边抢锁。
/// </summary>
public sealed class DeviceGuardService : IDisposable, IAsyncDisposable
{
    /// <summary>守卫评估间隔（秒）。</summary>
    public const int GuardIntervalSeconds = 60;

    /// <summary>处置后的最短冷却（秒）。网络本身不通时反复断连只会把网络搞得更糟。</summary>
    public const int GuardCooldownSeconds = 600;

    /// <summary>连续处置失败上限。达到后停止自动处置，等用户手动处理。</summary>
    public const int GuardMaxConsecutiveFailures = 3;

    private readonly ICredentialSource _store;
    private readonly IOnlineDevicePortal _portal;
    private readonly INetworkProbe _probe;
    private readonly Func<int> _effectivePort;
    private readonly Action _requestReauthenticate;
    private readonly Action<string> _log;
    private readonly Func<bool> _canEvaluate;

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _stateGate = new();
    private readonly object _evaluationGate = new();
    private readonly OperationLifetime _operations = new();
    private Task _evaluation = Task.CompletedTask;
    private bool _evaluationPending;
    private int _networkVersion;

    private CancellationTokenSource? _cts;
    private Task? _loop;
    private bool _disposed;

    private DeviceGuardState _state = DeviceGuardState.Disabled;
    private int _deviceCount;
    private int _statisticPort = 8603;
    private DateTime? _lastProbeAt;
    private bool? _lastProbeOk;
    private string _lastMessage = "";
    private DateTime? _nextAllowedAt;
    private int _consecutiveFailures;

    /// <summary>状态变化（界面订阅）。</summary>
    public event Action<DeviceGuardStatus>? Changed;

    /// <summary>
    /// 构造。
    /// </summary>
    /// <param name="store">设置与凭据。</param>
    /// <param name="portal">门户客户端（取设备数、断开本机）。</param>
    /// <param name="probe">独立外网探测。</param>
    /// <param name="effectivePort">取当前生效端口的回调（由档案解析器提供）。</param>
    /// <param name="requestReauthenticate">唤醒巡检重新认证的回调（无锁）。</param>
    /// <param name="log">日志输出回调。</param>
    public DeviceGuardService(
        ICredentialSource store,
        IOnlineDevicePortal portal,
        INetworkProbe probe,
        Func<int> effectivePort,
        Action requestReauthenticate,
        Action<string> log, Func<bool>? canEvaluate = null)
    {
        _store = store;
        _portal = portal;
        _probe = probe;
        _effectivePort = effectivePort;
        _requestReauthenticate = requestReauthenticate;
        _log = log;
        _canEvaluate = canEvaluate ?? (() => true);
    }

    /// <summary>当前状态快照。</summary>
    public DeviceGuardStatus Status
    {
        get
        {
            lock (_stateGate)
            {
                return new DeviceGuardStatus
                {
                    State = _state,
                    DeviceCount = _deviceCount,
                    Threshold = Threshold,
                    StatisticPort = _statisticPort,
                    LastProbeAt = _lastProbeAt,
                    LastProbeOk = _lastProbeOk,
                    LastMessage = _lastMessage,
                    NextAllowedAt = _nextAllowedAt,
                    Busy = _state is DeviceGuardState.Probing
                        or DeviceGuardState.Disposing
                        or DeviceGuardState.Reauthenticating
                };
            }
        }
    }

    private int Threshold
    {
        get
        {
            int t = _store.Settings.DeviceGuardThreshold;
            return t is < 1 or > 50 ? 2 : t;
        }
    }

    /// <summary>启动后台循环。</summary>
    public void Start()
    {
        if (_disposed || _loop is not null) return;

        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        _loop = Task.Run(() => LoopAsync(token), CancellationToken.None);
    }

    /// <summary>停止后台循环。</summary>
    /// <summary>网络状态变化时触发即时评估。</summary>
    public void NotifyNetworkChanged()
    {
        Interlocked.Increment(ref _networkVersion);
        lock (_stateGate)
        {
            _deviceCount = 0;
            _lastProbeAt = null;
            _lastProbeOk = null;
        }
        _ = EvaluateAsync();
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        // 启动后先等一小会儿，别和刚起来的巡检抢门户
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(10), ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { return; }

        while (!ct.IsCancellationRequested)
        {
            try
            {
                await EvaluateAsync().ConfigureAwait(false);
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex)
            {
                Write($"守卫评估异常：{ex.Message}");
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(GuardIntervalSeconds), ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { return; }
        }
    }

    /// <summary>
    /// 一轮评估：取设备数 → 判断阈值 → 探测 → 必要时处置。
    /// </summary>
    public Task EvaluateAsync()
    {
        lock (_evaluationGate)
        {
            if (_disposed) return Task.CompletedTask;
            _evaluationPending = true;
            if (_evaluation.IsCompleted)
                _evaluation = Task.Run(RunEvaluationsAsync);
            return _evaluation;
        }
    }

    private async Task RunEvaluationsAsync()
    {
        using var operation = _operations.TryEnter();
        if (operation is null) return;
        var ct = operation.Token;
        while (true)
        {
            lock (_evaluationGate)
            {
                if (!_evaluationPending || _disposed)
                {
                    _evaluation = Task.CompletedTask;
                    return;
                }
                _evaluationPending = false;
            }

            try
            {
                await EvaluateOnceAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
            catch (Exception ex)
            {
                Write($"守卫评估异常：{ex.Message}");
            }
        }
    }

    private async Task EvaluateOnceAsync(CancellationToken ct)
    {
        if (!_canEvaluate()) return;
        if (_disposed) return;

        var settings = _store.Settings;
        if (!settings.DeviceGuardEnabled)
        {
            SetState(DeviceGuardState.Disabled, "设备数守卫已关闭");
            return;
        }

        // 上限停止后不再自动处置，除非用户手动重置
        if (CurrentState() == DeviceGuardState.Stopped) return;

        // 冷却期内不重复处置
        if (IsCoolingDown())
        {
            SetState(DeviceGuardState.Cooldown, DescribeCooldown());
            return;
        }

        int networkVersion = Volatile.Read(ref _networkVersion);
        int port = _effectivePort();
        int threshold = Threshold;

        // 取设备数。失败必须跳过本轮 —— 拿不到列表不等于「0 台」，
        // 把它当成 0 台会在门户异常时触发一通无意义的断开重连。
        DeviceListResult list;
        try
        {
            list = await _portal.GetOnlineDevicesAsync(port, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            Write($"获取在线设备失败，本轮守卫跳过：{ex.Message}");
            SetState(DeviceGuardState.Idle, "获取在线设备失败，本轮跳过");
            return;
        }

        ct.ThrowIfCancellationRequested();
        if (networkVersion != Volatile.Read(ref _networkVersion)) return;
        if (!list.Success)
        {
            Write($"获取在线设备失败，本轮守卫跳过：{list.Message}");
            SetState(DeviceGuardState.Idle, $"获取在线设备失败，本轮跳过（{list.Message}）");
            return;
        }

        int count = list.Devices.Count;
        SetDeviceCount(count, port);

        if (count <= threshold)
        {
            SetState(DeviceGuardState.Idle, $"在线设备 {count} 台，未超过阈值 {threshold}");
            return;
        }

        Write($"在线设备 {count} 台，超过阈值 {threshold}，开始检测出网连通性（统计端口 {port}）");

        var probe = await RunProbeAsync(networkVersion, ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        if (networkVersion != Volatile.Read(ref _networkVersion)) return;

        if (probe.Success)
        {
            Write($"出网连通性正常（{probe.Detail}）");
            SetState(DeviceGuardState.Idle, $"设备 {count} 台 · 出网正常");
            return;
        }

        Write($"出网连通性检测失败：{probe.Detail}");

        await DisposeAndReauthenticateAsync(count, port, ct, networkVersion: networkVersion).ConfigureAwait(false);
    }

    /// <summary>「立即检测一次」：只探测，不处置。</summary>
    public async Task<ProbeResult> ProbeNowAsync()
    {
        if (_disposed) return new ProbeResult { Success = false, Detail = "守卫未启动" };
        using var operation = _operations.TryEnter();
        if (operation is null) return new ProbeResult { Success = false, Detail = "守卫未启动" };
        int networkVersion = Volatile.Read(ref _networkVersion);

        SetState(DeviceGuardState.Probing, "正在检测出网连通性…");

        int port = _effectivePort();
        SetDeviceCount(_deviceCount, port);

        var probe = await RunProbeAsync(networkVersion, operation.Token).ConfigureAwait(false);
        if (networkVersion != Volatile.Read(ref _networkVersion))
            return new ProbeResult { Success = false, Detail = "网络已切换，请重新检测" };
        if (operation.Token.IsCancellationRequested) return probe;

        SetState(probe.Success ? DeviceGuardState.Idle : DeviceGuardState.Idle,
            probe.Success ? $"出网正常（{probe.Detail}）" : $"出网异常（{probe.Detail}）");

        return probe;
    }

    /// <summary>「现在重新建立连接」：走一次完整处置。</summary>
    public async Task<bool> DisposeNowAsync()
    {
        if (_disposed) return false;
        using var operation = _operations.TryEnter();
        if (operation is null) return false;

        int port = _effectivePort();
        int count = _deviceCount;

        // 手动触发不受冷却与上限限制 —— 用户明确要求了
        return await DisposeAndReauthenticateAsync(count, port, operation.Token, manual: true).ConfigureAwait(false);
    }

    /// <summary>断开本机连接并唤醒重新认证。</summary>
    private async Task<bool> DisposeAndReauthenticateAsync(int count, int port, CancellationToken ct, bool manual = false, int networkVersion = 0)
    {
        // 守卫只持自己的锁；这里调用的门户请求与唤醒都是无锁的
        try { await _gate.WaitAsync(ct).ConfigureAwait(false); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { return false; }
        try
        {
            if (_disposed) return false;
            if (!manual && (!_canEvaluate() || !_store.Settings.DeviceGuardEnabled || IsCoolingDown()
                || CurrentState() == DeviceGuardState.Stopped || count <= Threshold
                || networkVersion != Volatile.Read(ref _networkVersion) || port != _effectivePort()))
                return false;

            SetState(DeviceGuardState.Disposing, $"正在断开本机连接（端口 {port}）");

            bool disconnected = false;
            try
            {
                disconnected = await _portal.DisconnectAsync(port, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return false; }
            catch (Exception ex)
            {
                Write($"断开本机连接异常：{ex.Message}");
            }

            if (ct.IsCancellationRequested) return false;
            if (disconnected)
            {
                Write("已断开本机连接，正在重新认证");
                _consecutiveFailures = 0;
            }
            else
            {
                _consecutiveFailures++;
                Write($"断开本机连接失败，转入重新认证（连续失败 {_consecutiveFailures} 次）");
            }

            SetState(DeviceGuardState.Reauthenticating, "正在重新认证…");

            // 断开失败也要走重新认证 —— 认证流程本身会重试
            _requestReauthenticate();

            if (_consecutiveFailures >= GuardMaxConsecutiveFailures)
            {
                SetState(DeviceGuardState.Stopped,
                    $"已停止自动处置（连续 {GuardMaxConsecutiveFailures} 次断开失败），请检查网络或手动重新连接");
                Write($"连续 {GuardMaxConsecutiveFailures} 次处置失败，已停止自动处置");
                return false;
            }

            var until = DateTime.Now.AddSeconds(GuardCooldownSeconds);
            SetCooldown(until);
            SetState(DeviceGuardState.Cooldown, DescribeCooldown());

            return disconnected;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>执行一次探测，异常一律吞掉转成失败结果。</summary>
    private async Task<ProbeResult> RunProbeAsync(int networkVersion, CancellationToken ct)
    {
        var settings = _store.Settings;
        string target = NetworkProbeService.NormalizeTarget(settings.DeviceGuardProbeTarget);
        int seconds = NetworkProbeService.NormalizeTimeoutSeconds(settings.DeviceGuardProbeTimeoutSeconds);
        var timeout = TimeSpan.FromSeconds(seconds);

        SetState(DeviceGuardState.Probing, $"正在检测出网连通性（{target}，{seconds} 秒）…");

        try
        {
            var result = await _probe.ProbeAsync(target, timeout, ct).ConfigureAwait(false);
            SetProbeResult(result.Success, result.Detail, networkVersion);
            return result;
        }
        catch (Exception ex)
        {
            var failed = new ProbeResult { Success = false, Detail = $"探测失败：{ex.Message}" };
            SetProbeResult(false, failed.Detail, networkVersion);
            return failed;
        }
    }

    private bool IsCoolingDown()
    {
        lock (_stateGate)
        {
            return _nextAllowedAt is not null && DateTime.Now < _nextAllowedAt.Value;
        }
    }

    private string DescribeCooldown()
    {
        lock (_stateGate)
        {
            if (_nextAllowedAt is null) return "冷却中";
            int left = (int)Math.Ceiling((_nextAllowedAt.Value - DateTime.Now).TotalSeconds);
            return left > 0 ? $"冷却中，{left} 秒后重新检测" : "冷却中";
        }
    }

    private DeviceGuardState CurrentState()
    {
        lock (_stateGate) return _state;
    }

    private void SetState(DeviceGuardState state, string message)
    {
        bool changed;
        lock (_stateGate)
        {
            changed = _state != state || _lastMessage != message;
            _state = state;
            _lastMessage = message;
        }

        // 状态变了才记日志 —— 守卫每分钟评估一次，不去重的话日志会被同一个状态刷屏
        if (changed) Write($"守卫状态：{DescribeState(state)} · {message}");

        RaiseChanged();
    }

    /// <summary>守卫状态的中文名，写日志用。</summary>
    private static string DescribeState(DeviceGuardState state) => state switch
    {
        DeviceGuardState.Disabled => "已关闭",
        DeviceGuardState.Idle => "待命",
        DeviceGuardState.Probing => "探测中",
        DeviceGuardState.Disposing => "断开中",
        DeviceGuardState.Reauthenticating => "重认证中",
        DeviceGuardState.Cooldown => "冷却中",
        DeviceGuardState.Stopped => "已停止",
        _ => state.ToString()
    };

    private void SetDeviceCount(int count, int port)
    {
        lock (_stateGate)
        {
            _deviceCount = count;
            _statisticPort = port;
        }
        RaiseChanged();
    }

    private void SetProbeResult(bool ok, string message, int networkVersion)
    {
        lock (_stateGate)
        {
            if (networkVersion != Volatile.Read(ref _networkVersion)) return;
            _lastProbeAt = DateTime.Now;
            _lastProbeOk = ok;
            _lastMessage = message;
        }
        RaiseChanged();
    }

    private void SetCooldown(DateTime until)
    {
        lock (_stateGate) _nextAllowedAt = until;
        RaiseChanged();
    }

    private void RaiseChanged()
    {
        try { Changed?.Invoke(Status); } catch { }
    }

    private void Write(string text)
    {
        try { _log?.Invoke(text); } catch { }
    }

    public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        _cts?.Cancel();
        await _operations.StopAsync().ConfigureAwait(false);
        if (_loop is not null) await _loop.ConfigureAwait(false);
        await _evaluation.ConfigureAwait(false);
        _cts?.Dispose();
        _gate.Dispose();
    }
}
