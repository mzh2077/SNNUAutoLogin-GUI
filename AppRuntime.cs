using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using SNNUAutoLogin.Services;

namespace SNNUAutoLogin;

public sealed class LogEntry
{
    public string Time { get; init; } = "";
    public string Text { get; init; } = "";
}

/// <summary>
/// 进程级运行时：凭据、巡检状态机、托盘、日志缓冲全部在这里，与 UI 生命周期解耦。
/// 窗口只是一个可随时拆卸的视图 —— 关窗到托盘时把 XAML 内容树整个丢掉，
/// 工作集能从 ~160MB 掉到 11MB 左右，召回来再重新构建。
/// </summary>
public sealed class AppRuntime : IAsyncDisposable
{
    private const int MaxLogLines = 60;

    private readonly List<LogEntry> _logs = new(MaxLogLines + 4);
    private readonly object _logGate = new();

    private MainWindow? _window;
    private NetworkWatch? _netWatch;
    private SessionGuard? _session;
    private readonly StayAwake _stayAwake = new();
    private bool _disposed;

    public static AppRuntime? Current { get; private set; }

    public CredentialStore Store { get; } = new();
    public ThemeColors Theme { get; }
    public LoginCoordinator Coordinator { get; }
    public DeviceManager Devices { get; }
    public DispatcherQueue Dispatcher { get; }
    public TrayIcon? Tray { get; private set; }

    /// <summary>接入方式识别（网卡类型 + SSID）。</summary>
    public NetworkAccessService Access { get; }

    /// <summary>生效档案解析（连接规则 → 账号 / 密码 / 出口）。</summary>
    public ConnectionProfileResolver Profiles { get; }

    /// <summary>门户接入端口发现者：端口由服务端分配，程序自己问门户要。</summary>
    public PortalPortDiscovery Ports { get; }

    /// <summary>独立外网探测。</summary>
    public NetworkProbeService Probe { get; }

    /// <summary>设备数守卫。</summary>
    public DeviceGuardService Guard { get; }

    /// <summary>全程序共用一个门户客户端：省 HTTP 连接池，也保证同一端口的会话是同一份。</summary>
    private readonly PortalClient _portal = new();

    /// <summary>旧版本「网络加速」在系统 hosts 里留下的标记块是否还在。</summary>
    public bool HasLegacyHosts { get; private set; }

    public ConnState State { get; private set; } = ConnState.Idle;
    public LoginResult? LastResult { get; private set; }
    public bool Exiting { get; private set; }
    internal bool ShutdownComplete { get; private set; }

    /// <summary>状态中文标题（托盘提示与窗口共用）。</summary>
    public string StateLabel { get; private set; } = "待机";

    public event Action<ConnState, LoginResult?>? StateChanged;
    public event Action<string>? Logged;

    /// <summary>托盘菜单里改了配置（窗口若开着需要回读）。</summary>
    public event Action? SettingsChanged;

    /// <summary>旧 hosts 残留状态变化（清理完成后界面收起提示条）。</summary>
    public event Action? LegacyHostsChanged;

    public AppRuntime(DispatcherQueue dispatcher, bool launchedAtStartup = false)
    {
        Current = this;
        Dispatcher = dispatcher;

        Store.Load();

        // 首次启动与便携目录移动后也要注册/修复；关闭时清除遗留项。
        if (!StartupManager.SetEnabled(Store.Settings.AutoStart))
        {
            Store.Settings.AutoStart = StartupManager.IsEnabled();
            Store.Save();
            AddLog("开机自启设置失败，请检查当前用户的启动项权限");
        }

        Theme = new ThemeColors(dispatcher);

        Access = new NetworkAccessService();
        Probe = new NetworkProbeService();

        // 接入端口是门户按线路分配的，程序自己问门户要 —— 不让用户从下拉框里挑
        Ports = new PortalPortDiscovery(_portal);

        Profiles = new ConnectionProfileResolver(Store, Access, Ports);

        var eventDispatcher = new EventDispatcher(() => dispatcher.HasThreadAccess, action => dispatcher.TryEnqueue(() => action()));
        Coordinator = new LoginCoordinator(Store, eventDispatcher, Profiles, Ports, launchedAtStartup);
        Coordinator.StateChanged += OnStateChanged;
        Coordinator.Log += AddLog;

        // 门户客户端是共用的，所以这里不让 DeviceManager 负责释放它
        Devices = new DeviceManager(eventDispatcher, _portal, ownsClient: false);

        Guard = new DeviceGuardService(
            Store,
            _portal,
            Probe,
            () => Profiles.Resolve().Port,
            () => Coordinator.RequestReauthenticate(),
            AddLog);

        HasLegacyHosts = HostsCleanup.HasLegacyBlock();
        LegacyHostsEntries = HasLegacyHosts ? HostsCleanup.CountLegacyEntries() : 0;

        if (!string.IsNullOrWhiteSpace(Store.LastNotice))
            AddLog(Store.LastNotice);

        AddLog("程序启动");
        AddLog(Store.Settings.AutoStart && StartupManager.IsEnabled()
            ? "开机自启已注册"
            : "开机自启未启用");
    }

    public MainWindow? Window => _window;

    public IReadOnlyList<LogEntry> LogSnapshot
    {
        get
        {
            lock (_logGate) return _logs.ToArray();
        }
    }

    // ────────────────────────── 托盘 ──────────────────────────

    /// <summary>注册托盘图标；失败只记日志，不影响巡检。</summary>
    public void AttachTray()
    {
        if (Tray is not null) return;

        try
        {
            Tray = new TrayIcon();
            Tray.ShowRequested += () => Dispatcher.TryEnqueue(ShowWindow);
            Tray.CommandInvoked += () => Dispatcher.TryEnqueue(OnTrayCommand);

            bool ok = Tray.Show(MenuState(), Environment.ProcessPath ?? "");
            AddLog(ok ? "已驻留任务栏托盘" : "托盘图标注册失败（系统未接受）");
        }
        catch (Exception ex)
        {
            AddLog($"托盘图标创建失败：{ex.Message}");
        }
    }

    private TrayMenuState MenuState() => new()
    {
        AutoStart = Store.Settings.AutoStart,
        AutoReconnect = Store.Settings.AutoReconnect,
        MinimizeToTray = Store.Settings.MinimizeToTray,
        Tip = $"校园网自动登录 · {StateLabel}"
    };

    public void RefreshTray() => Tray?.UpdateState(MenuState());

    private void OnTrayCommand()
    {
        if (Tray is null) return;

        switch (Tray.LastCommand)
        {
            case TrayIcon.CmdLogin:
                _ = Coordinator.ManualConnectAsync();
                break;

            case TrayIcon.CmdToggleAutoStart:
                SetAutoStart(!Store.Settings.AutoStart);
                break;

            case TrayIcon.CmdToggleReconnect:
                Store.Settings.AutoReconnect = !Store.Settings.AutoReconnect;
                Store.Save();
                SettingsChanged?.Invoke();
                RefreshTray();
                break;

            case TrayIcon.CmdToggleTray:
                Store.Settings.MinimizeToTray = !Store.Settings.MinimizeToTray;
                Store.Save();
                SettingsChanged?.Invoke();
                RefreshTray();
                break;

            case TrayIcon.CmdExit:
                Exit();
                break;
        }
    }

    public void SetAutoStart(bool enabled)
    {
        if (StartupManager.SetEnabled(enabled))
            AddLog(enabled ? "开机自启已注册" : "开机自启已关闭");
        else
            AddLog("开机自启设置失败，请检查当前用户的启动项权限");

        Store.Settings.AutoStart = StartupManager.IsEnabled();
        Store.Save();
        SettingsChanged?.Invoke();
        RefreshTray();
    }

    // ────────────────────────── 巡检 ──────────────────────────

    public void Start()
    {
        if (Exiting || _disposed) return;
        Coordinator.Start();
        StartSessionWatch();

        // 网络一通（重连 Wi-Fi、换网、休眠唤醒）就立刻补一次认证，不等下一个轮询周期
        _netWatch = new NetworkWatch(Dispatcher, () =>
        {
            AddLog("检测到网络状态变化，立即重新认证");
            Access.Invalidate();          // 网卡换了，接入方式要重新判定
            Ports.Invalidate();          // 线路可能变了，门户分配的端口也要重新问
            Devices.Invalidate();
            Guard.NotifyNetworkChanged(); // 换网了，守卫顺手评估一次
            Coordinator.RequestImmediateCheck();
        });
        _netWatch.Start();

        // 设备数守卫：自己一个后台循环，不占巡检的锁
        Guard.Start();

        // 启动期的临时分配（XAML 解析、字体排版、图标解码）用完就没价值了。
        // 6 秒后先归还一次，25 秒后再收一次尾 —— 以前只等 20 秒，任务管理器里
        // 前 20 秒一直挂着 170MB，看起来像是内存泄漏。
        _ = Task.Delay(6000).ContinueWith(_ => MemoryOptimizer.Trim(force: true),
            TaskScheduler.Default);
        _ = Task.Delay(25000).ContinueWith(_ => MemoryOptimizer.Trim(force: true),
            TaskScheduler.Default);
    }

    // ────────────────────────── 锁屏保持 ──────────────────────────

    /// <summary>当前是否处于锁屏状态（开机自启无窗口时同样有效）。</summary>
    public bool IsSessionLocked => _session?.IsLocked == true;

    /// <summary>WTS 会话通知是否注册成功。</summary>
    public bool SessionWatchAvailable => _session?.Available == true;

    /// <summary>是否正持有「系统不睡眠」的请求，界面与自检用。</summary>
    public bool StayAwakeActive => _stayAwake.Active;

    /// <summary>最近一次申领的 Win32 返回值（0 表示失败），自检用。</summary>
    public uint StayAwakeResult => _stayAwake.LastResult;

    /// <summary>最近一次实际提交的 ES_* 标志位，自检用。</summary>
    public uint StayAwakeApplied => _stayAwake.LastApplied;

    private void StartSessionWatch()
    {
        if (_session is not null) return;

        try
        {
            _stayAwake.Start();

            _session = new SessionGuard();
            _session.LockChanged += locked => Dispatcher.TryEnqueue(() => OnSessionLockChanged(locked));
            _session.Resumed += () => Dispatcher.TryEnqueue(OnSystemResumed);
            _session.Start();

            if (!_session.Available)
                AddLog("锁屏监听未注册成功，锁屏保持可能不生效");
            else if (Store.Settings.KeepAliveOnLock)
                AddLog("锁屏保持已开启");

            ApplyLockPolicy();
        }
        catch (Exception ex)
        {
            AddLog($"锁屏监听启动失败：{ex.Message}");
        }
    }

    /// <summary>按「设置开关 + 当前锁屏状态」决定是否让系统保持运行。</summary>
    public void ApplyLockPolicy()
    {
        if (_disposed) return;

        bool hold = Store.Settings.KeepAliveOnLock && IsSessionLocked;
        _stayAwake.Set(hold);
    }

    private void OnSessionLockChanged(bool locked)
    {
        ApplyLockPolicy();

        if (locked)
        {
            AddLog(Store.Settings.KeepAliveOnLock
                ? "已锁屏：保持认证在线，系统暂不睡眠"
                : "已锁屏：按系统电源策略处理");
        }
        else
        {
            AddLog("已解锁：恢复系统电源策略");

            // 锁屏期间如果 Portal 要验证码，界面上是看不见的，解锁后再把窗口叫回来
            if (Coordinator.State == ConnState.CaptchaRequired) ShowWindow();
        }

        // 锁屏 / 解锁各确认一次认证状态，避免正好卡在掉线的空档里
        Coordinator.RequestImmediateCheck();
    }

    private void OnSystemResumed()
    {
        // 睡眠会丢掉执行状态请求，醒来先重新申领，再立刻补一次认证
        _stayAwake.Reassert();
        AddLog("系统从睡眠恢复，重新检测认证状态");
        Access.Invalidate();
        Ports.Invalidate();
        Devices.Invalidate();
        Guard.NotifyNetworkChanged();
        Coordinator.RequestImmediateCheck();
    }

    /// <summary>开发自检：模拟一次锁屏 / 解锁，走真实事件的同一处理路径。</summary>
    internal void SimulateSessionLock(bool locked) => _session?.SimulateLock(locked);

    private void OnStateChanged(ConnState state, LoginResult? result)
    {
        State = state;
        LastResult = result;
        StateLabel = LabelOf(state);

        Tray?.UpdateTip($"校园网自动登录 · {StateLabel}");

        // 要验证码就得有人看着：把窗口叫回来（驻留托盘时也算）。
        // 锁屏时叫了也看不见，等解锁时再召回。
        if (state == ConnState.CaptchaRequired && !IsSessionLocked)
            Dispatcher.TryEnqueue(ShowWindow);

        StateChanged?.Invoke(state, result);
    }

    public static string LabelOf(ConnState s) => s switch
    {
        ConnState.Online => "已连接",
        ConnState.CaptchaRequired => "需要验证码",
        ConnState.Connecting => "正在登录",
        ConnState.Offline => "未登录",
        ConnState.Failed => "登录失败",
        ConnState.Checking => "正在检测",
        _ => "待机"
    };

    public void AddLog(string text)
    {
        var entry = new LogEntry { Time = DateTime.Now.ToString("HH:mm:ss"), Text = text };

        lock (_logGate)
        {
            _logs.Add(entry);
            if (_logs.Count > MaxLogLines) _logs.RemoveRange(0, _logs.Count - MaxLogLines);
        }

        // 运行日志同时落一份到临时目录，程序关掉后还能查
        TraceLine(text);

        // 调用方可能在后台线程（加速服务的测速/写入都在线程池里），
        // 而日志最终会写进绑定 ListView 的 ObservableCollection —— 必须在 UI 线程触发。
        if (Dispatcher.HasThreadAccess)
        {
            try { Logged?.Invoke(text); } catch { }
        }
        else
        {
            Dispatcher.TryEnqueue(() =>
            {
                try { Logged?.Invoke(text); } catch { }
            });
        }
    }

    /// <summary>
    /// 把一行运行日志追加到临时目录的 snnu_trace.txt。
    ///
    /// 界面上的日志只在内存里，程序一关就没了 —— 出问题时（比如守卫为什么没触发）
    /// 没有痕迹根本查不了。这里只写日志文案，不含任何凭据内容。
    /// </summary>
    internal static void TraceLine(string text)
    {
        try
        {
            System.IO.File.AppendAllText(
                System.IO.Path.Combine(System.IO.Path.GetTempPath(), "snnu_trace.txt"),
                $"[{DateTime.Now:HH:mm:ss.fff}] {text}\n");
        }
        catch { }
    }

    // ────────────────────────── 旧版 hosts 残留 ──────────────────────────

    /// <summary>旧标记块里的记录条数，用于提示文案（启动时算一次）。</summary>
    public int LegacyHostsEntries { get; private set; }

    /// <summary>
    /// 把旧版本写进 hosts 的标记块删掉。需要管理员权限，会弹一次 UAC。
    /// 成功后清掉标记，界面据此收起提示条。
    /// </summary>
    public async Task<HostsCleanResult> CleanLegacyHostsAsync()
    {
        if (!HasLegacyHosts)
            return new(HostsCleanStatus.NothingToDo, 0, "没有需要还原的条目");

        AddLog("正在还原系统 hosts（需要管理员权限）…");

        var r = await HostsCleanup.RemoveAsync(
            onElevating: () => AddLog("等待管理员授权…")).ConfigureAwait(false);

        if (r.Status is HostsCleanStatus.Cleaned or HostsCleanStatus.NothingToDo)
        {
            HasLegacyHosts = false;
            LegacyHostsEntries = 0;
            Dispatcher.TryEnqueue(() =>
            {
                try { LegacyHostsChanged?.Invoke(); } catch { }
            });
        }

        AddLog(r.Message);
        return r;
    }

    // ────────────────────────── 窗口生命周期 ──────────────────────────

    /// <summary>显示主窗口；窗口内容树已被释放时重新构建。</summary>
    public void ShowWindow()
    {
        if (_disposed || Exiting) return;

        if (_window is null)
        {
            _window = new MainWindow(this);
        }

        _window.RestoreFromTray();
    }

    /// <summary>窗口把它自己销毁了（内容树已释放），通知运行时。</summary>
    public void OnWindowTornDown(MainWindow window)
    {
        if (ReferenceEquals(_window, window)) _window = null;
    }

    public async void Exit()
    {
        if (Exiting) return;
        _window?.PersistToStore();
        Exiting = true;
        try
        {
            await DisposeAsync();
        }
        catch (Exception ex)
        {
            AddLog($"退出清理失败：{ex.Message}");
        }
        finally
        {
            ShutdownComplete = true;
            Application.Current.Exit();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        _netWatch?.Dispose();
        _netWatch = null;

        _session?.Dispose();
        _session = null;
        _stayAwake.Set(false);
        Tray?.Dispose();
        Tray = null;

        try
        {
            await Task.WhenAll(Guard.DisposeAsync().AsTask(),
                Coordinator.DisposeAsync().AsTask(), Devices.DisposeAsync().AsTask());
        }
        finally
        {
            _stayAwake.Dispose();
            Ports.Dispose();
            Probe.Dispose();
            _portal.Dispose();
            Theme.Dispose();
        }
    }
}
