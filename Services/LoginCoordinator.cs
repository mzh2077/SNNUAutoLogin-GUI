using System;
using System.Threading;
using System.Threading.Tasks;

namespace SNNUAutoLogin.Services;

public enum ConnState
{
    Idle,
    Checking,
    Online,
    Offline,
    Connecting,
    Failed,

    /// <summary>Portal 要求图形验证码，等用户在界面上输入。</summary>
    CaptchaRequired
}

/// <summary>连接状态机：定时巡检出口认证状态，掉线自动重新登录，开机网络未就绪时指数退避。</summary>
public sealed class LoginCoordinator : IDisposable, IAsyncDisposable
{
    private const int IdleDelaySeconds = 30;      // 没配账号 / 关了自动重连时的慢速巡检
    private const int PortalDownDelayMax = 60;    // Portal 不可达（多半不在校园网）
    private const int FailureDelayMax = 120;      // 登录一直失败时的封顶间隔

    private readonly PortalClient _client;
    private readonly bool _ownsClient;
    private readonly ICredentialSource _store;
    private readonly IEventDispatcher _dispatcher;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private readonly OperationLifetime _operations = new();
    private Task? _loop;
    private bool _disposed;
    private bool _suppressAuto;
    private int _failures;
    private bool _portalDown;
    private string? _idleReason;
    private int _pendingCheck;
    private TaskCompletionSource<object?>? _wake;
    private bool _startupLoginPending;
    private int _networkVersion;
    private int _captchaNetworkVersion;
    private EffectiveProfile? _captchaProfile;

    /// <summary>有守卫要求强制重新认证：下一轮不能走「已在线」的快捷路径。</summary>
    private int _forceRelogin;

    /// <summary>解析当前生效的账号 / 密码 / 端口。没有它就只会用全局默认账号。</summary>
    private readonly IConnectionProfileResolver? _resolver;

    /// <summary>门户端口发现者：接入端口是服务端分配的，程序自己问门户要。</summary>
    private readonly IPortalPortDiscovery? _ports;

    public ConnState State { get; private set; } = ConnState.Idle;

    /// <summary>待输入的验证码图片；为空表示当前不需要验证码。</summary>
    public CaptchaImage? PendingCaptcha { get; private set; }
    public bool HasPendingCaptcha => _awaitingCaptcha && _captchaNetworkVersion == Volatile.Read(ref _networkVersion);

    private bool _awaitingCaptcha;
    public LoginResult? LastResult { get; private set; }
    public DateTime? LastCheckAt { get; private set; }

    /// <summary>当前巡检节奏（秒），供界面显示。</summary>
    public int CurrentDelaySeconds { get; private set; } = 8;

    public event Action<ConnState, LoginResult?>? StateChanged;
    public event Action<string>? Log;

    /// <summary>验证码图片变化（到达 / 刷新 / 清除）。</summary>
    public event Action? CaptchaChanged;

    public LoginCoordinator(ICredentialSource store, IEventDispatcher dispatcher, bool launchedAtStartup = false)
        : this(store, dispatcher, null, null, launchedAtStartup)
    {
    }

    public LoginCoordinator(
        ICredentialSource store,
        IEventDispatcher dispatcher,
        IConnectionProfileResolver? resolver,
        bool launchedAtStartup = false)
        : this(store, dispatcher, resolver, null, launchedAtStartup)
    {
    }

    public LoginCoordinator(
        ICredentialSource store,
        IEventDispatcher dispatcher,
        IConnectionProfileResolver? resolver,
        IPortalPortDiscovery? ports,
        bool launchedAtStartup = false, PortalClient? client = null)
    {
        _client = client ?? new PortalClient();
        _ownsClient = client is null;
        _store = store;
        _dispatcher = dispatcher;
        _resolver = resolver;
        _ports = ports;
        _startupLoginPending = launchedAtStartup && store.Settings.AutoStart;
    }

    private bool AutoLoginAllowed => _store.Settings.AutoReconnect
        || (_startupLoginPending && _store.Settings.AutoStart);

    /// <summary>开机认证成功前持续重试；成功后的掉线认证由自动重连开关控制。</summary>
    private bool AutoReady =>
        AutoLoginAllowed
        && HasEffectiveCredentials();

    private bool HasEffectiveCredentials()
    {
        var profile = CurrentProfile();
        return !string.IsNullOrWhiteSpace(profile.Account) && !string.IsNullOrEmpty(profile.Password);
    }

    public void Start()
    {
        if (_disposed || _loop is not null) return;
        _failures = 0;
        _loop = RunUntilShutdownAsync();
    }

    private async Task RunUntilShutdownAsync()
    {
        using var operation = _operations.TryEnter();
        if (operation is not null)
            await RunAsync(operation.Token).ConfigureAwait(false);
    }

    public void SuppressAuto(bool suppress) => _suppressAuto = suppress;

    public void SetStartupLogin(bool pending) => _startupLoginPending = pending;

    public void InvalidateNetwork()
    {
        Interlocked.Increment(ref _networkVersion);
        RequestImmediateCheck();
    }

    /// <summary>
    /// 请求立刻巡检一次（网络恢复、用户手动刷新时用）。
    /// 正在睡的那一轮会被提前叫醒，不用等到下一轮。
    /// </summary>
    public void RequestImmediateCheck()
    {
        Interlocked.Exchange(ref _pendingCheck, 1);
        Volatile.Read(ref _wake)?.TrySetResult(null);
    }

    /// <summary>
    /// 请求重新认证（设备数守卫处置完本机连接后调用）。
    ///
    /// 刻意做成无锁：只置一个标志位 + 唤醒等待中的循环。
    /// 守卫那边正拿着自己的锁，如果这里去取协调器的锁就会形成「守卫等协调器、协调器等守卫」的环，
    /// 无锁唤醒从根上避免了这种死锁。
    /// </summary>
    public void RequestReauthenticate()
    {
        Interlocked.Exchange(ref _forceRelogin, 1);
        Interlocked.Exchange(ref _pendingCheck, 1);
        Volatile.Read(ref _wake)?.TrySetResult(null);
    }

    private async Task RunAsync(CancellationToken ct)
    {
        bool first = true;

        while (!ct.IsCancellationRequested)
        {
            TimeSpan delay;
            try
            {
                var (ok, portalDown) = await TickAsync(ct).ConfigureAwait(false);
                _portalDown = portalDown;
                _failures = ok ? 0 : _failures + 1;

                delay = NextDelay(first);
                first = false;
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                LogLine($"巡检异常：{ex.Message}");
                delay = TimeSpan.FromSeconds(15);
            }

            if (!await SleepAsync(delay, ct).ConfigureAwait(false)) return;
        }
    }

    /// <summary>
    /// 下一轮间隔。原则：能用才快，不能用就慢 —— 避免无意义地反复请求 Portal。
    /// </summary>
    private TimeSpan NextDelay(bool first)
    {
        TimeSpan delay;

        if (State == ConnState.Online)
        {
            delay = TimeSpan.FromSeconds(Math.Max(4, _store.Settings.CheckIntervalSeconds));
        }
        else if (!AutoReady)
        {
            // 账号/密码没填、或用户关掉了自动重连：没必要每 3 秒去敲一次 Portal
            delay = TimeSpan.FromSeconds(IdleDelaySeconds);
        }
        else if (_failures <= 0)
        {
            delay = TimeSpan.FromSeconds(first ? 2 : 10);
        }
        else if (_portalDown)
        {
            delay = TimeSpan.FromSeconds(Math.Min(PortalDownDelayMax, 10 * _failures));
        }
        else
        {
            // 登录失败（密码错、被锁、欠费…）：10 → 20 → 40 → 80 → 120 指数退避
            delay = TimeSpan.FromSeconds(Math.Min(FailureDelayMax,
                10 * Math.Pow(2, Math.Min(4, _failures - 1))));
        }

        CurrentDelaySeconds = (int)delay.TotalSeconds;
        return delay;
    }

    /// <summary>
    /// 睡到下一轮；期间若有人 RequestImmediateCheck 会被立刻唤醒。
    /// 返回 false 表示已取消（退出）。
    /// </summary>
    private async Task<bool> SleepAsync(TimeSpan delay, CancellationToken ct)
    {
        if (Interlocked.Exchange(ref _pendingCheck, 0) == 1) return true;
        if (delay <= TimeSpan.Zero) return true;

        var tcs = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        Volatile.Write(ref _wake, tcs);

        // 发布唤醒点之后再查一次：避免「刚检测完 pending、还没挂上 tcs」这一拍丢信号
        if (Interlocked.Exchange(ref _pendingCheck, 0) == 1)
        {
            Volatile.Write(ref _wake, null);
            return true;
        }

        try
        {
            var delayTask = Task.Delay(delay, ct);
            var done = await Task.WhenAny(delayTask, tcs.Task).ConfigureAwait(false);

            if (done == tcs.Task)
            {
                Interlocked.Exchange(ref _pendingCheck, 0);
                LogLine("网络状态变化，立即重新检测");
            }
            return !ct.IsCancellationRequested;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        finally
        {
            Volatile.Write(ref _wake, null);
        }
    }

    /// <summary>
    /// 取当前生效的登录档案：命中的连接规则优先，没命中就用全局默认。
    /// 没有注入解析器时（老构造）行为与从前完全一致。
    /// </summary>
    private EffectiveProfile CurrentProfile()
    {
        if (_resolver is not null) return _resolver.Resolve();

        var s = _store.Settings;
        return new EffectiveProfile
        {
            Account = s.Account ?? "",
            Password = _store.LoadPassword(),
            Port = PortalClient.IsKnownPort(s.DefaultPort) ? s.DefaultPort : 8603,
            Operator = s.Operator,
            Rule = null,
            IsDefault = true
        };
    }

    /// <summary>
    /// 解析当前生效档案，并确保接入端口是最新的。
    ///
    /// 端口由门户分配，所以每轮巡检前都要问一次门户「我该走哪个端口」；
    /// 发现服务内部有 30 秒缓存，不会每次都真的发请求。
    /// </summary>
    private async Task<EffectiveProfile> ResolveProfileAsync(CancellationToken ct)
    {
        if (_ports is not null)
        {
            try
            {
                var discovered = await _ports.DiscoverAsync(ct).ConfigureAwait(false);
                if (discovered is > 0) _resolver?.SetDiscoveredPort(discovered.Value);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch
            {
                // 读不到端口不是致命错误：继续用兜底端口
            }
        }

        return CurrentProfile();
    }

    /// <summary>一次完整巡检：查状态 → 必要时登录。返回 (是否正常, Portal 是否不可达)。</summary>
    private async Task<(bool ok, bool portalDown)> TickAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            int networkVersion = Volatile.Read(ref _networkVersion);
            if (_awaitingCaptcha && _captchaNetworkVersion != networkVersion) ClearCaptcha();
            var profile = await ResolveProfileAsync(ct).ConfigureAwait(false);
            if (networkVersion != Volatile.Read(ref _networkVersion)) return (true, false);
            int port = profile.Port;

            bool force = Interlocked.Exchange(ref _forceRelogin, 0) == 1;

            var status = await _client.GetStatusAsync(port, ct).ConfigureAwait(false);
            if (networkVersion != Volatile.Read(ref _networkVersion)) return (true, false);
            LastCheckAt = DateTime.Now;

            if (status.Success && !force)
            {
                _startupLoginPending = false;
                NoteIdleReason(null);
                if (_awaitingCaptcha) ClearCaptcha();   // 用户在浏览器里登上了，面板该收起来
                SetState(ConnState.Online, status);
                return (true, false);
            }

            if (force)
            {
                // 守卫刚把本机连接断掉了：即使状态查询还看到旧的在线会话，也按重新认证处理
                LogLine("收到重新认证请求，正在重新登录…");
                NoteIdleReason(null);
            }

            bool portalDown = status.Message.Contains("Portal", StringComparison.Ordinal)
                              || status.Message.Contains("超时", StringComparison.Ordinal);

            if (portalDown)
            {
                NoteIdleReason("portal");
                SetState(ConnState.Failed, status);
                return (false, true);
            }

            // 已经在等用户填验证码：别再自动提交，否则会把用户手里的验证码刷掉
            if (_awaitingCaptcha)
            {
                SetState(ConnState.CaptchaRequired, LastResult);
                return (true, false);
            }

            // 离线：是否允许自动登录
            var account = profile.Account;
            var password = profile.Password;

            if (_suppressAuto || (!AutoLoginAllowed && !force)
                || string.IsNullOrWhiteSpace(account) || string.IsNullOrEmpty(password))
            {
                NoteIdleReason("idle");
                SetState(ConnState.Offline, status);
                return (true, false);
            }

            NoteIdleReason(null);
            SetState(ConnState.Connecting, status);
            LogLine("检测到未认证，正在自动登录…");

            var r = await _client
                .LoginAsync(port, account, password, profile.Operator, ct, force)
                .ConfigureAwait(false);
            if (networkVersion != Volatile.Read(ref _networkVersion)) return (true, false);

            if (r.Success)
            {
                ClearCaptcha();
                _startupLoginPending = false;
                SetState(ConnState.Online, r);
                LogLine($"登录成功 · {r.Account ?? account} · {r.Ip ?? "IP 未知"}");
                return (true, false);
            }

            if (r.NeedCaptcha)
            {
                await EnterCaptchaAsync(r, profile, networkVersion, ct).ConfigureAwait(false);
                return (true, false);
            }

            SetState(ConnState.Failed, r);
            LogLine($"登录失败：{r.Message}");
            return (false, false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>同一个原因只写一条日志，避免慢速巡检把日志刷满。</summary>
    private void NoteIdleReason(string? reason)
    {
        if (_idleReason == reason) return;
        _idleReason = reason;

        if (reason == "idle")
            LogLine($"未配置账号/密码或已关闭自动重连，巡检放慢到 {IdleDelaySeconds} 秒一次");
        else if (reason == "portal")
            LogLine("认证门户不可达（可能不在校园网），已放慢巡检");
    }

    /// <summary>用户手动点击登录/重连。</summary>
    public async Task ManualConnectAsync()
    {
        using var operation = _operations.TryEnter();
        if (operation is null || !await WaitForGateAsync(operation.Token).ConfigureAwait(false)) return;
        var ct = operation.Token;
        try
        {
            int networkVersion = Volatile.Read(ref _networkVersion);
            var profile = await ResolveProfileAsync(ct).ConfigureAwait(false);
            if (networkVersion != Volatile.Read(ref _networkVersion)) return;
            int port = profile.Port;

            var account = profile.Account;
            var password = profile.Password;

            if (string.IsNullOrWhiteSpace(account) || string.IsNullOrEmpty(password))
            {
                Push(ConnState.Offline, LoginResult.Fail("请先填写账号与密码"));
                return;
            }

            SetState(ConnState.Connecting, null);
            LogLine("正在登录…");

            var r = await _client.LoginAsync(port, account, password, profile.Operator, ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            if (networkVersion != Volatile.Read(ref _networkVersion)) return;
            if (r.Success)
            {
                _startupLoginPending = false;
                _failures = 0;
                ClearCaptcha();
                SetState(ConnState.Online, r);
                LogLine(r.AlreadyOnline ? "当前已在线" : $"登录成功 · {r.Account ?? account} · {r.Ip ?? "IP 未知"}");
            }
            else if (r.NeedCaptcha)
            {
                await EnterCaptchaAsync(r, profile, networkVersion, ct).ConfigureAwait(false);
            }
            else
            {
                SetState(ConnState.Failed, r);
                LogLine($"登录失败：{r.Message}");
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        finally
        {
            _gate.Release();
        }
    }

    // ────────────────────────── 图形验证码 ──────────────────────────

    /// <summary>取一张验证码并切到「等输入」状态。</summary>
    private async Task EnterCaptchaAsync(LoginResult r, EffectiveProfile profile, int networkVersion, CancellationToken ct)
    {
        if (networkVersion != Volatile.Read(ref _networkVersion)) return;
        _captchaProfile = profile;
        _captchaNetworkVersion = networkVersion;
        _awaitingCaptcha = true;
        LastResult = r;

        try
        {
            PendingCaptcha = await _client
                .FetchCaptchaAsync(profile.Port, ct)
                .ConfigureAwait(false);
        }
        catch
        {
            PendingCaptcha = null;
        }

        if (networkVersion != Volatile.Read(ref _networkVersion))
        {
            ClearCaptcha();
            return;
        }

        LogLine(PendingCaptcha is null
            ? "Portal 要求验证码，但没能取到图片"
            : "Portal 要求输入验证码，请在窗口里填写");

        SetState(ConnState.CaptchaRequired, r);
        PushCaptcha();
    }

    /// <summary>提交用户输入的验证码。</summary>
    public async Task<LoginResult> SubmitCaptchaAsync(string code)
    {
        using var operation = _operations.TryEnter();
        if (operation is null) return LoginResult.Fail("程序正在退出");
        var ct = operation.Token;
        if (string.IsNullOrWhiteSpace(code))
            return LoginResult.Captcha("请输入验证码");

        if (!await WaitForGateAsync(ct).ConfigureAwait(false)) return LoginResult.Fail("程序正在退出");
        try
        {
            if (_captchaProfile is not { } profile || _captchaNetworkVersion != Volatile.Read(ref _networkVersion))
            {
                ClearCaptcha();
                return LoginResult.Fail("验证码已过期，请重新登录");
            }
            var account = profile.Account;
            SetState(ConnState.Connecting, null);
            LogLine("正在提交验证码…");

            var r = await _client
                .LoginAsync(profile.Port, account, profile.Password, profile.Operator, code.Trim(), ct)
                .ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            if (_captchaNetworkVersion != Volatile.Read(ref _networkVersion))
            {
                ClearCaptcha();
                return LoginResult.Fail("网络已切换，请重新登录");
            }

            if (r.Success)
            {
                _startupLoginPending = false;
                _failures = 0;
                ClearCaptcha();
                SetState(ConnState.Online, r);
                LogLine($"登录成功 · {r.Account ?? account} · {r.Ip ?? "IP 未知"}");
                return r;
            }

            if (r.NeedCaptcha)
            {
                // 码错/过期：换一张，继续等输入
                _awaitingCaptcha = true;
                try { PendingCaptcha = await _client.FetchCaptchaAsync(profile.Port, ct).ConfigureAwait(false); }
                catch { PendingCaptcha = null; }

                SetState(ConnState.CaptchaRequired, r);
                PushCaptcha();
                LogLine($"验证码未通过：{r.Message}");
                return r;
            }

            ClearCaptcha();
            SetState(ConnState.Failed, r);
            LogLine($"登录失败：{r.Message}");
            return r;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { return LoginResult.Fail("程序正在退出"); }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>开发自检用：直接塞一张验证码进来，看界面表现（不联网）。</summary>
    internal void InjectCaptchaForTest(byte[] png)
    {
        _awaitingCaptcha = true;
        PendingCaptcha = new CaptchaImage(png, "演示：按图输入后回车");
        SetState(ConnState.CaptchaRequired, LoginResult.Captcha("本次登录需要输入图片验证码"));
        PushCaptcha();
    }

    /// <summary>换一张验证码。</summary>
    public async Task RefreshCaptchaAsync()
    {
        using var operation = _operations.TryEnter();
        if (operation is null || !await WaitForGateAsync(operation.Token).ConfigureAwait(false)) return;
        try
        {
            if (!_awaitingCaptcha || _captchaProfile is not { } profile
                || _captchaNetworkVersion != Volatile.Read(ref _networkVersion)) return;

            try
            {
                PendingCaptcha = await _client.FetchCaptchaAsync(profile.Port, operation.Token).ConfigureAwait(false);
            }
            catch
            {
                PendingCaptcha = null;
            }

            PushCaptcha();
        }
        finally
        {
            _gate.Release();
        }
    }

    private void ClearCaptcha()
    {
        _captchaProfile = null;
        _awaitingCaptcha = false;
        PendingCaptcha = null;
        PushCaptcha();
    }

    /// <summary>配置变更后立即重新巡检一次。</summary>
    public async Task RefreshAsync()
    {
        using var operation = _operations.TryEnter();
        if (operation is null) return;
        // Wait(0) 是原子的：巡检正忙时不再插一脚，改为让下一轮提前跑
        if (!_gate.Wait(0))
        {
            RequestImmediateCheck();
            return;
        }
        _gate.Release();

        try
        {
            await TickAsync(operation.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { }
    }

    private void SetState(ConnState state, LoginResult? result)
    {
        State = state;
        LastResult = result;
        Push(state, result);
    }

    private void Push(ConnState state, LoginResult? result)
    {
        _dispatcher.TryEnqueue(() => StateChanged?.Invoke(state, result));
    }

    private void LogLine(string text) => _dispatcher.TryEnqueue(() => Log?.Invoke(text));

    private void PushCaptcha() => _dispatcher.TryEnqueue(() =>
    {
        try { CaptchaChanged?.Invoke(); } catch { }
    });

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
        var stopping = _operations.StopAsync();
        Volatile.Read(ref _wake)?.TrySetResult(null);
        await stopping.ConfigureAwait(false);
        if (_loop is not null) await _loop.ConfigureAwait(false);

        if (_ownsClient) _client.Dispose();
        _gate.Dispose();
    }
}
