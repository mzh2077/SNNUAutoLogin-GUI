using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Dispatching;
using Windows.Storage.Streams;
using SNNUAutoLogin.Services;
using Windows.Graphics;
using Windows.UI;

namespace SNNUAutoLogin;

/// <summary>
/// 主窗口 = 纯视图。所有后台状态（凭据、巡检、托盘、日志）都在 <see cref="AppRuntime"/> 里，
/// 所以这里的内容树可以整体丢掉再重建 —— 关窗到托盘后进程只留十几 MB。
/// </summary>
public sealed partial class MainWindow : Window
{
    private const int MaxLogLines = 40;

    private readonly AppRuntime _rt;
    private readonly ThemeColors _theme;
    private readonly ObservableCollection<LogEntry> Logs = new();
    private readonly Queue<LogEntry> _pendingLogs = new();
    private DispatcherQueueTimer? _logFlush;

    private AppWindow? _appWindow;
    private WindowCloseHook? _closeHook;
    private Operator _operator = Operator.Mobile;
    private ConnState _lastState = ConnState.Idle;
    private bool _uiReady;
    private bool _uiBuilt;
    private bool _hiddenToTray;
    private bool _startHidden;
    private bool _logScrollQueued;
    private bool _legacyDismissed;
    private bool _legacyCleaning;

    private readonly DispatcherTimer _saveTimer = new() { Interval = TimeSpan.FromMilliseconds(450) };

    private static readonly int[] Intervals = { 4, 8, 15, 30 };

    public MainWindow(AppRuntime runtime)
    {
        _rt = runtime;
        _theme = runtime.Theme;

        BuildUi();

        Title = "陕师大校园网自动登录";
        ConfigureWindow();

        _saveTimer.Tick += (_, _) =>
        {
            _saveTimer.Stop();
            PersistToStore();
        };

        _theme.Changed += OnThemeChanged;
        _rt.StateChanged += OnRuntimeState;
        _rt.Coordinator.CaptchaChanged += OnCaptchaChanged;
        _rt.Logged += OnRuntimeLog;
        _rt.SettingsChanged += OnRuntimeSettings;
        _rt.LegacyHostsChanged += OnLegacyHostsChanged;
        _rt.Devices.Changed += OnDeviceListChanged;

        Activated += OnWindowActivated;
        Closed += OnClosed;
    }

    // ────────────────────────── 窗口外观 ──────────────────────────

    private void ConfigureWindow()
    {
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        var id = Win32Interop.GetWindowIdFromWindow(hwnd);
        var appWindow = AppWindow.GetFromWindowId(id);
        _appWindow = appWindow;

        try
        {
            SystemBackdrop = new MicaBackdrop();
        }
        catch
        {
            try { SystemBackdrop = new DesktopAcrylicBackdrop(); } catch { }
        }

        appWindow.Changed += OnAppWindowChanged;
        _closeHook = new WindowCloseHook(hwnd) { InterceptClose = OnCloseRequested };

        double scale = GetDpiForWindow(hwnd) / 96.0;
        if (scale <= 0) scale = 1.0;

        int targetW = (int)(1040 * scale);
        int targetH = (int)(840 * scale);

        var area = DisplayArea.GetFromWindowId(id, DisplayAreaFallback.Primary);
        var work = area.WorkArea;

        double fit = Math.Min(1.0, Math.Min(work.Width * 0.94 / targetW, work.Height * 0.94 / targetH));
        int w = (int)(targetW * fit);
        int h = (int)(targetH * fit);

        appWindow.Resize(new SizeInt32(w, h));
        appWindow.Move(new PointInt32(work.X + (work.Width - w) / 2, work.Y + (work.Height - h) / 2));

        if (appWindow.Presenter is OverlappedPresenter presenter)
        {
            try
            {
                presenter.PreferredMinimumWidth = (int)(900 * scale);
                presenter.PreferredMinimumHeight = (int)(620 * scale);            }
            catch { }
        }
    }

    /// <summary>自定义标题栏。内容树重建后必须重新挂一次（元素实例已换）。</summary>
    private void ApplyTitleBar()
    {
        try
        {
            ExtendsContentIntoTitleBar = true;
            AppTitleBar.Background = new SolidColorBrush(Colors.Transparent);
            SetTitleBar(AppTitleBar);
        }
        catch { }
    }

    /// <summary>窗口最小化：延迟一拍归还工作集，避免打断最小化过渡动画。</summary>
    private async void OnAppWindowChanged(AppWindow sender, AppWindowChangedEventArgs args)
    {
        if (!args.DidPresenterChange) return;
        if (sender.Presenter is not OverlappedPresenter presenter) return;
        if (presenter.State != OverlappedPresenterState.Minimized) return;

        await Task.Delay(1200);
        if (!_uiBuilt) return;
        if (presenter.State == OverlappedPresenterState.Minimized)
            MemoryOptimizer.Trim(force: true);
    }

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern bool PostMessageW(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    internal string RectOf(FrameworkElement el)
    {
        try
        {
            var p = el.TransformToVisual(Root).TransformPoint(new Windows.Foundation.Point(0, 0));
            return $"({p.X:F1},{p.Y:F1}) {el.ActualWidth:F1}x{el.ActualHeight:F1}";
        }
        catch
        {
            return "n/a";
        }
    }

    // ────────────────────────── 视图构建 / 拆卸 ──────────────────────────

    /// <summary>构建内容树（首次与从托盘召回时都会走这里）。</summary>
    private void BuildUi()
    {
        if (_uiBuilt) return;

        // 生成的 InitializeComponent() 带 _contentLoaded 守卫，第二次调用会直接返回。
        // 想重建整棵树必须先把这个标志复位，否则所有控件字段都会是 null。
        _contentLoaded = false;
        InitializeComponent();

        ApplyTitleBar();
        ApplyCommandLineOverrides();

        LogList.ItemsSource = Logs;
        ShowVersion();

        // 先置位再灌值：SyncOperatorUi / ApplyState 都会检查 _uiBuilt
        _uiBuilt = true;

        LoadSettingsToUi();
        FillLogsFromRuntime();
        WireEvents();
        FillDevicePorts();
        OnDeviceListChanged();

        ApplyThemeColors();
        ApplyState(_rt.State, _rt.LastResult, animate: false);

        // 重建窗口时验证码可能已经在了（从托盘召回的情形）
        _ = SyncCaptchaAsync();

        FitToggleWidths();
        DispatcherQueue.TryEnqueue(FitToggleWidths);
    }

    /// <summary>
    /// 拆掉内容树：先落盘，再把 Content 置空并清掉所有命名控件字段，
    /// 让整棵树（含 ListView 容器、文本排版缓存）真正可被回收。
    /// </summary>
    private void TeardownUi()
    {
        if (!_uiBuilt) return;

        PersistToStore();
        _uiBuilt = false;

        try { LogList.ItemsSource = null; } catch { }
        Logs.Clear();
        _pendingLogs.Clear();
        _logFlush?.Stop();
        _logFlush = null;

        Content = null;
        DropElementReferences();

        // XAML 卸载有一部分是异步的，等一拍再回收。
        // 这里只调一次 Trim：它内部就是「两轮压缩式 GC + EmptyWorkingSet」，
        // 以前外面再套一层 GC.Collect 等于每次关窗多做两次全堆回收（白卡一下）。
        DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () =>
        {
            System.Runtime.GCSettings.LargeObjectHeapCompactionMode =
                System.Runtime.GCLargeObjectHeapCompactionMode.CompactOnce;

            MemoryOptimizer.Trim(force: true);
        });
    }

    /// <summary>关于卡片里的版本号直接读程序集，别再手写一份（以前写死 1.0.0，升级后就对不上了）。</summary>
    private void ShowVersion()
    {
        try
        {
            var v = typeof(MainWindow).Assembly.GetName().Version;
            VersionText.Text = v is null ? "—" : $"{v.Major}.{v.Minor}.{v.Build}";
        }
        catch { }
    }

    /// <summary>把 x:Name 生成的控件字段全部置空，断开它们对已卸载树的强引用。</summary>
    private void DropElementReferences()
    {
        var flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        foreach (var f in GetType().GetFields(flags))
        {
            if (f.IsInitOnly || f.IsStatic) continue;
            if (!typeof(DependencyObject).IsAssignableFrom(f.FieldType)) continue;

            try { f.SetValue(this, null); } catch { }
        }
    }

    /// <summary>
    /// 首帧入场：和系统设置页一样，300ms、12px、每张卡错开 30ms。
    /// 卡片多了不排队等，4 张以内刚好在 400ms 内全部到位。
    /// </summary>
    private void PlayIntro()
    {
        if (!_uiBuilt) return;

        Fx.FallIn(StatusCard, 0, 12, Fx.SlowMs);
        Fx.FallIn(LogCard, 30, 12, Fx.SlowMs);
        Fx.FallIn(AccountCard, 30, 12, Fx.SlowMs);
        Fx.FallIn(BehaviorCard, 60, 12, Fx.SlowMs);
        Fx.FallIn(AboutCard, 90, 12, Fx.SlowMs);
    }

    /// <summary>从托盘召回：内容树在就只显示，不在就重建后再显示。</summary>
    public void RestoreFromTray()
    {
        _hiddenToTray = false;

        BuildUi();

        try { _appWindow?.Show(); } catch { }
        Activate();

        try { SetForegroundWindow(WinRT.Interop.WindowNative.GetWindowHandle(this)); } catch { }

        FitToggleWidths();
        DispatcherQueue.TryEnqueue(FitToggleWidths);
        PlayIntro();

        RunStartupHooks();
    }

    private void HideToTray()
    {
        if (_rt.Tray?.IsVisible != true) return;

        _hiddenToTray = true;
        _saveTimer.Stop();

        try { _appWindow?.Hide(); } catch { }

        TeardownUi();
        _rt.AddLog("窗口已收进托盘，界面资源已释放");
    }

    /// <summary>返回 true 表示已转成驻留托盘，窗口不关闭。</summary>
    private bool OnCloseRequested()
    {
        Trace($"close-requested exiting={_rt.Exiting} tray={(_rt.Store.Settings.MinimizeToTray ? 1 : 0)}");

        if (_rt.Exiting) return false;
        if (!_rt.Store.Settings.MinimizeToTray) return false;
        if (_rt.Tray?.IsVisible != true) return false;

        HideToTray();
        return true;
    }

    internal static void Trace(string msg)
    {
        try
        {
            System.IO.File.AppendAllText(
                System.IO.Path.Combine(System.IO.Path.GetTempPath(), "snnu_trace.txt"),
                $"[{DateTime.Now:HH:mm:ss.fff}] {msg}\n");
        }
        catch { }
    }

    internal static string Mem()
    {
        try
        {
            using var p = Process.GetCurrentProcess();
            p.Refresh();
            return $"ws={(int)(p.WorkingSet64 / 1024)}KB priv={(int)(p.PrivateMemorySize64 / 1024)}KB";
        }
        catch { return "n/a"; }
    }

    // ────────────────────────── 系统配色 ──────────────────────────

    private void OnThemeChanged()
    {
        if (!_uiBuilt) return;
        ApplyThemeColors();
        ApplyState(_lastState, _rt.LastResult, animate: false);
    }

    private void ApplyThemeColors()
    {
        if (!_uiBuilt) return;

        try
        {
            _theme.Refresh(Root.ActualTheme == ElementTheme.Dark);
            TitleIcon.Foreground = BrushCache.Get(_theme.Accent);
            RepaintState(_lastState);
        }
        catch { }
    }

    /// <summary>把当前状态对应的图标/圆底/进度环重新上色（复用画刷，避免每轮巡检都分配）。</summary>
    private void RepaintState(ConnState state)
    {
        var (glyph, busy, color) = state switch
        {
            ConnState.Online => ("\uE73E", false, _theme.Success),
            ConnState.Connecting => ("\uE895", true, _theme.Accent),
            ConnState.Offline => ("\uE7BA", false, _theme.Caution),
            ConnState.Failed => ("\uE783", false, _theme.Critical),
            ConnState.CaptchaRequired => ("\uE8D7", false, _theme.Caution),
            ConnState.Checking => ("\uE72C", true, _theme.Neutral),
            _ => ("\uE7E8", false, _theme.Neutral)
        };

        var brush = BrushCache.Get(color);
        bool glyphChanged = StateIcon.Glyph != glyph;

        StateIcon.Foreground = brush;
        StateIcon.Glyph = glyph;
        BusyRing.Foreground = brush;

        // 忙碌时只留系统进度环，圆底收起来 —— 一个实心淡色圆盘垫在环后面更像"压了个饼"
        double haloAlpha = state == ConnState.Online ? 0.14 : 0.10;
        HaloRing.Fill = BrushCache.Get(ThemeColors.WithAlpha(color, haloAlpha));
        HaloRing.Visibility = busy ? Visibility.Collapsed : Visibility.Visible;

        BusyRing.IsActive = busy;
        BusyRing.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        StateIcon.Visibility = busy ? Visibility.Collapsed : Visibility.Visible;

        // 图标切换时弹一下，比硬换字形更容易被眼睛接住
        if (glyphChanged && !busy) Fx.Pop(StateIcon, 0.9f, 280);
    }

    private void OnWindowActivated(object sender, WindowActivatedEventArgs args)
    {
        if (args.WindowActivationState == WindowActivationState.Deactivated) return;

        if (_startHidden)
        {
            _startHidden = false;
            HideToTray();
        }
    }

    // ────────────────────────── 初始化 ──────────────────────────

    /// <summary>开发期用：--op=N 强制指定出口，--dark 强制深色，配合 --snapshot 验证界面。</summary>
    private void ApplyCommandLineOverrides()
    {
        foreach (var arg in Environment.GetCommandLineArgs())
        {
            if (arg.StartsWith("--op=", StringComparison.OrdinalIgnoreCase)
                && int.TryParse(arg.AsSpan(5), out int op) && op is >= 0 and <= 3)
            {
                _operator = (Operator)op;
            }
            else if (arg.Equals("--dark", StringComparison.OrdinalIgnoreCase))
            {
                Root.RequestedTheme = ElementTheme.Dark;
            }
        }
    }

    private void LoadSettingsToUi()
    {
        bool ready = _uiReady;
        _uiReady = false;
        var s = _rt.Store.Settings;

        AccountBox.Text = s.Account;
        PwdBox.Password = _rt.Store.LoadPassword();
        AutoStartSwitch.IsOn = s.AutoStart && StartupManager.IsEnabled();
        AutoReconnectSwitch.IsOn = s.AutoReconnect;
        TraySwitch.IsOn = s.MinimizeToTray;
        LockKeepSwitch.IsOn = s.KeepAliveOnLock;

        IntervalBox.Items.Clear();
        foreach (var v in Intervals) IntervalBox.Items.Add($"{v} 秒");
        int idx = Array.IndexOf(Intervals, s.CheckIntervalSeconds);
        IntervalBox.SelectedIndex = idx >= 0 ? idx : 1;

        _operator = s.Operator;
        SyncOperatorUi();

        SyncLegacyHostsBar();
        _uiReady = ready;
    }

    private void FillLogsFromRuntime()
    {
        Logs.Clear();
        foreach (var e in _rt.LogSnapshot) Logs.Add(e);

        while (Logs.Count > MaxLogLines) Logs.RemoveAt(0);
        ScrollLogToEnd();
    }

    /// <summary>填充接入端口下拉框，并选中当前端口。</summary>
    private void FillDevicePorts()
    {
        DevicePortBox.Items.Clear();
        foreach (int p in PortalClient.KnownPorts) DevicePortBox.Items.Add(p);

        bool ready = _uiReady;
        _uiReady = false;
        DevicePortBox.SelectedItem = _rt.Devices.Port;
        _uiReady = ready;
    }

    private void WireEvents()
    {
        ConnectBtn.Click += async (_, _) => await OnConnectClicked();
        RefreshBtn.Click += async (_, _) => await OnRefreshClicked();

        OperatorBox.SelectionChanged += (_, _) =>
        {
            if (!_uiReady || OperatorBox.SelectedIndex < 0) return;
            SelectOperator((Operator)OperatorBox.SelectedIndex);
        };

        AccountBox.TextChanged += (_, _) => ScheduleSave();
        PwdBox.PasswordChanged += (_, _) => ScheduleSave();

        AutoStartSwitch.Toggled += (_, _) =>
        {
            if (!_uiReady) return;
            PersistToStore();
        };
        AutoReconnectSwitch.Toggled += (_, _) => ScheduleSave();
        LockKeepSwitch.Toggled += (_, _) =>
        {
            ScheduleSave();
            _rt.ApplyLockPolicy();   // 开关一动就生效，不用等下次锁屏
        };
        TraySwitch.Toggled += (_, _) =>
        {
            ScheduleSave();
            _rt.RefreshTray();
        };
        IntervalBox.SelectionChanged += (_, _) => ScheduleSave();

        // 验证码：点按钮或直接回车提交
        CaptchaSubmitBtn.Click += (_, _) => _ = SubmitCaptchaAsync();
        CaptchaRefreshBtn.Click += (_, _) => _ = _rt.Coordinator.RefreshCaptchaAsync();
        CaptchaBox.KeyDown += (_, e) =>
        {
            if (e.Key != Windows.System.VirtualKey.Enter) return;
            e.Handled = true;
            _ = SubmitCaptchaAsync();
        };

        // 主按钮按下时轻微回弹（合成线程 Scale 通道，不参与布局）
        Fx.PressFeedback(ConnectBtn);
        Fx.PressFeedback(CaptchaSubmitBtn);
        Fx.PressFeedback(RefreshBtn);
        Fx.PressFeedback(DeviceRefreshBtn);

        // 在线设备
        DeviceRefreshBtn.Click += async (_, _) => await OnDeviceRefreshClicked();
        DevicePortBox.SelectionChanged += async (_, _) => await OnDevicePortChanged();

        LegacyCleanBtn.Click += (_, _) => OnLegacyCleanClicked();
        LegacyDismissBtn.Click += (_, _) =>
        {
            _legacyDismissed = true;
            Fx.Conceal(LegacyHostsBar);
        };

        _uiReady = true;
    }

    // ────────────────────────── 运行时事件 ──────────────────────────

    private void OnRuntimeState(ConnState state, LoginResult? result)
    {
        if (!_uiBuilt) return;
        ApplyState(state, result, animate: true);
    }

    private void OnRuntimeLog(string text)
    {
        if (!_uiBuilt) return;

        // 日志常常成串到来（网络变化 → 巡检 → 认证）。逐条插进 ItemsControl 会让
        // 列表每次重排 + 播放过渡动画，肉眼就是「一顿一顿」。先攒在队列里，按帧合并。
        _pendingLogs.Enqueue(new LogEntry { Time = DateTime.Now.ToString("HH:mm:ss"), Text = text });

        _logFlush ??= DispatcherQueue.CreateTimer();
        if (_logFlush.IsRunning) return;

        _logFlush.Interval = TimeSpan.FromMilliseconds(90);
        _logFlush.IsRepeating = false;
        _logFlush.Tick += OnLogFlushTick;
        _logFlush.Start();
    }

    private void OnLogFlushTick(DispatcherQueueTimer sender, object args)
    {
        sender.Stop();
        _logFlush = null;

        if (!_uiBuilt || _pendingLogs.Count == 0) return;

        // 一次只补最需要的尾部若干条，避免积压时整表重排
        int skip = Math.Max(0, _pendingLogs.Count - MaxLogLines);
        while (_pendingLogs.Count > 0)
        {
            var e = _pendingLogs.Dequeue();
            if (skip-- > 0) continue;
            Logs.Add(e);
        }

        while (Logs.Count > MaxLogLines) Logs.RemoveAt(0);
        ScrollLogToEnd();
    }

    private void OnRuntimeSettings()
    {
        if (!_uiBuilt) return;
        LoadSettingsToUi();
    }

    private void ScrollLogToEnd()
    {
        if (_logScrollQueued) return;
        _logScrollQueued = true;

        DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () =>
        {
            _logScrollQueued = false;
            if (!_uiBuilt) return;
            if (LogScroll.ScrollableHeight > 0)
                LogScroll.ChangeView(null, LogScroll.ScrollableHeight, null, true);
        });
    }

    // ────────────────────────── 状态渲染 ──────────────────────────

    /// <summary>
    /// 系统 ToggleSwitch 模板为「开 / 关」文字预留了 154 的 MinWidth，该值由 StaticResource
    /// 解析，页面级覆盖无效。这里读模板里轨道（OuterBorder）的实测宽度，把控件宽度夹到刚好
    /// 等于轨道：整条轨道完整可见（MaxWidth 会裁掉右侧），同时贴住行尾对齐。
    /// </summary>
    private void FitToggleWidths()
    {
        if (!_uiBuilt) return;

        foreach (var sw in new[] { AutoStartSwitch, AutoReconnectSwitch, TraySwitch, LockKeepSwitch })
        {
            if (sw is null) continue;

            var track = FindDescendant(sw, "OuterBorder");
            if (track is { ActualWidth: > 1 })
                sw.Width = track.ActualWidth;
        }
    }

    private static FrameworkElement? FindDescendant(DependencyObject root, string name)
    {
        int n = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < n; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is FrameworkElement fe && fe.Name == name) return fe;

            var hit = FindDescendant(child, name);
            if (hit is not null) return hit;
        }

        return null;
    }

    private void ApplyState(ConnState state, LoginResult? result, bool animate)
    {
        if (!_uiBuilt) return;

        _lastState = state;

        string title;
        string subtitle;

        switch (state)
        {
            case ConnState.Online:
                title = "已连接";
                string rhythm = $"每 {_rt.Coordinator.CurrentDelaySeconds} 秒巡检";
                subtitle = result?.LoginTime is { Length: > 0 } t
                    ? $"认证时间 {t} · {rhythm}"
                    : $"当前出口已通过认证 · {rhythm}";
                AccText.Text = result?.Account ?? (string.IsNullOrWhiteSpace(AccountBox.Text) ? "—" : AccountBox.Text.Trim());
                IpText.Text = result?.Ip ?? "—";
                break;

            case ConnState.Connecting:
                title = "正在登录";
                subtitle = "正在向认证服务器提交账号信息…";
                break;

            case ConnState.Offline:
                title = "未登录";
                subtitle = result?.Message is { Length: > 0 } m && m != "未认证"
                    ? m
                    : "校园网尚未认证，点按下方按钮登录";
                AccText.Text = string.IsNullOrWhiteSpace(AccountBox.Text) ? "—" : AccountBox.Text.Trim();
                IpText.Text = "—";
                break;

            case ConnState.Failed:
                title = "登录失败";
                subtitle = result?.Message is { Length: > 0 } fm && fm != "未认证" ? fm : "请检查账号密码或网络环境";
                break;

            case ConnState.CaptchaRequired:
                title = "需要验证码";
                subtitle = result?.Message is { Length: > 0 } cm ? cm : "请在下方输入图片验证码";
                break;

            case ConnState.Checking:
                title = "正在检测";
                subtitle = "正在检测出口认证状态…";
                break;

            default:
                title = "待机";
                subtitle = "等待操作";
                break;
        }

        RepaintState(state);

        // 文字变化走交叉淡入：状态在「正在登录 → 已连接」之间跳变时不会硬切一下
        Fx.SwapText(StateTitle, title, animate);
        Fx.SwapText(StateSubtitle, subtitle, animate, 4f, 200);

        if (state == ConnState.Connecting)
        {
            ConnectLabel.Text = "登录中…";
            ConnectIcon.Glyph = "\uE895";
        }
        else if (state == ConnState.Online)
        {
            ConnectLabel.Text = "重新登录";
            ConnectIcon.Glyph = "\uE72C";
        }
        else
        {
            ConnectLabel.Text = "立即登录";
            ConnectIcon.Glyph = "\uE7E8";
        }

        TitleState.Text = title;
        TitleDot.Fill = BrushCache.Get(state switch
        {
            ConnState.Online => _theme.Success,
            ConnState.Failed => _theme.Critical,
            ConnState.Offline => _theme.Caution,
            ConnState.CaptchaRequired => _theme.Caution,
            ConnState.Connecting => _theme.Accent,
            _ => _theme.Neutral
        });

        UpdateAutoPill();
    }

    private void UpdateAutoPill()
    {
        if (!_uiBuilt) return;

        bool auto = _rt.Store.Settings.AutoStart && StartupManager.IsEnabled();
        AutoLabel.Text = auto ? "自动登录 · 已启用" : "自动登录 · 未启用";
        AutoDot.Fill = BrushCache.Get(auto ? _theme.Success : _theme.Neutral);
    }

    // ────────────────────────── 交互 ──────────────────────────

    // ────────────────────────── 在线设备 ──────────────────────────

    /// <summary>在线设备列表。门户在线控制页里的设备表格。</summary>
    private void OnDeviceListChanged()
    {
        if (!_uiBuilt) return;

        var dm = _rt.Devices;

        DeviceList.ItemsSource = dm.Devices;
        DevicePortBox.SelectedItem = dm.Port;

        bool loading = dm.State == DeviceListState.Loading;
        DeviceRefreshBtn.IsEnabled = !loading;
        DevicePortBox.IsEnabled = !loading;

        switch (dm.State)
        {
            case DeviceListState.Loading:
                DeviceSummary.Text = "正在读取在线设备…";
                DeviceEmptyText.Visibility = Visibility.Collapsed;
                break;

            case DeviceListState.Ready:
                DeviceSummary.Text = dm.Devices.Count == 0
                    ? $"端口 {dm.Port} 下没有其他在线设备"
                    : $"端口 {dm.Port} · 共 {dm.Devices.Count} 台在线设备";
                DeviceEmptyText.Visibility = dm.Devices.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
                break;

            case DeviceListState.Failed:
                DeviceSummary.Text = "在线设备读取失败";
                DeviceEmptyText.Visibility = Visibility.Collapsed;
                break;

            default:
                DeviceSummary.Text = "尚未加载";
                DeviceEmptyText.Visibility = Visibility.Collapsed;
                break;
        }

        if (!string.IsNullOrEmpty(dm.Error))
        {
            DeviceErrorText.Text = dm.Error;
            DeviceErrorText.Visibility = Visibility.Visible;
        }
        else
        {
            DeviceErrorText.Visibility = Visibility.Collapsed;
        }
    }

    private async Task OnDeviceRefreshClicked()
    {
        if (!_uiBuilt) return;

        DeviceRefreshBtn.IsEnabled = false;
        DeviceSummary.Text = "正在读取在线设备…";

        try
        {
            await _rt.Devices.RefreshAsync();
        }
        finally
        {
            if (_uiBuilt) DeviceRefreshBtn.IsEnabled = true;
        }
    }

    private async Task OnDevicePortChanged()
    {
        if (!_uiReady) return;
        if (DevicePortBox.SelectedItem is not int port) return;

        await _rt.Devices.RefreshAsync(port);
    }

    /// <summary>下线别的设备（门户里的「强制下线」）。</summary>
    private async void OnDeviceOfflineClicked(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        if (!_uiBuilt) return;
        if ((sender as FrameworkElement)?.Tag is not OnlineDevice device) return;

        if (!device.CanForceOffline)
        {
            _rt.AddLog($"{device.DisplayName} 是本机当前连接，请用卡片底部的「断开连接」");
            return;
        }

        _rt.AddLog($"正在下线 {device.DisplayName}（{device.Ip}）…");
        device.Status = "下线中";
        OnDeviceListChanged();

        var ok = await _rt.Devices.TakeOfflineAsync(device);
        _rt.AddLog(ok ? $"{device.DisplayName} 已下线" : $"{device.DisplayName} 下线失败");
    }

    /// <summary>
    /// 断开本机当前这条连接 —— 卡片底部的页面级按钮，
    /// 对应门户 userstatus.jsp 底部那个「断开连接」。
    /// </summary>
    private async void OnDeviceDisconnectSelfClicked(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        if (!_uiBuilt) return;

        DeviceDisconnectBtn.IsEnabled = false;
        DeviceDisconnectLabel.Text = "断开中…";

        _rt.AddLog("正在断开本机连接…");

        try
        {
            var ok = await _rt.Devices.DisconnectSelfAsync();
            _rt.AddLog(ok
                ? "本机连接已断开，巡检将按设置自动重新认证"
                : "断开失败");
        }
        finally
        {
            if (_uiBuilt)
            {
                DeviceDisconnectBtn.IsEnabled = true;
                DeviceDisconnectLabel.Text = "断开连接";
            }
        }
    }

    // ────────────────────────── 旧版 hosts 残留 ──────────────────────────

    private void OnLegacyHostsChanged()
    {
        if (!_uiBuilt) return;
        SyncLegacyHostsBar();
    }

    /// <summary>只在检测到旧标记块时显示提示条；用户手动关掉后本次运行不再弹。</summary>
    private void SyncLegacyHostsBar()
    {
        if (!_uiBuilt || _legacyCleaning) return;

        if (!_rt.HasLegacyHosts || _legacyDismissed)
        {
            if (LegacyHostsBar.Visibility == Visibility.Visible) Fx.Conceal(LegacyHostsBar);
            return;
        }

        LegacyHostsText.Text = _rt.LegacyHostsEntries > 0
            ? $"旧版「网络加速」留下 {_rt.LegacyHostsEntries} 条 hosts 记录，可还原为系统默认解析。"
            : "旧版「网络加速」改过系统解析，可还原为系统默认。";

        LegacyCleanBtn.Content = "还原解析";
        LegacyCleanBtn.IsEnabled = true;
        LegacyCleanBtn.Visibility = Visibility.Visible;

        if (LegacyHostsBar.Visibility != Visibility.Visible)
            Fx.Reveal(LegacyHostsBar, 6f, Fx.NormalMs);
    }

    private async void OnLegacyCleanClicked()
    {
        if (_legacyCleaning) return;

        _legacyCleaning = true;
        LegacyCleanBtn.IsEnabled = false;
        LegacyCleanBtn.Content = "还原中…";
        LegacyHostsText.Text = "正在还原系统 hosts，会请求一次管理员授权…";

        try
        {
            var r = await _rt.CleanLegacyHostsAsync();
            if (!_uiBuilt) return;

            if (r.Status is HostsCleanStatus.Cleaned or HostsCleanStatus.NothingToDo)
            {
                LegacyHostsText.Text = r.Message;
                LegacyCleanBtn.Visibility = Visibility.Collapsed;

                await Task.Delay(2200);
                if (!_uiBuilt) return;
                Fx.Conceal(LegacyHostsBar);
            }
            else
            {
                LegacyHostsText.Text = r.Message;
                LegacyCleanBtn.Content = "重试";
                LegacyCleanBtn.IsEnabled = true;
            }
        }
        catch (Exception ex)
        {
            LegacyHostsText.Text = "还原失败：" + ex.Message;
            LegacyCleanBtn.Content = "重试";
            LegacyCleanBtn.IsEnabled = true;
        }
        finally
        {
            _legacyCleaning = false;
        }
    }

    // ────────────────────────── 验证码 ──────────────────────────

    private void OnCaptchaChanged()
    {
        if (!_uiBuilt) return;
        _ = SyncCaptchaAsync();
    }

    private async Task SyncCaptchaAsync()
    {
        if (!_uiBuilt) return;

        CaptchaSubmitBtn.IsEnabled = true;
        var captcha = _rt.Coordinator.PendingCaptcha;

        if (captcha is null)
        {
            Fx.Conceal(CaptchaPanel, Fx.FastMs);
            CaptchaPic.Source = null;
            return;
        }

        Fx.Reveal(CaptchaPanel);
        CaptchaHint.Text = string.IsNullOrWhiteSpace(captcha.Hint) ? "按图输入后按回车" : captcha.Hint;

        await SetCaptchaImageAsync(captcha.Bytes);

        CaptchaBox.Text = "";
        CaptchaBox.Focus(FocusState.Programmatic);
    }

    private async Task SetCaptchaImageAsync(byte[] bytes)
    {
        try
        {
            using var stream = new InMemoryRandomAccessStream();
            using (var writer = new DataWriter(stream))
            {
                writer.WriteBytes(bytes);
                await writer.StoreAsync();
                await writer.FlushAsync();
                writer.DetachStream();
            }

            stream.Seek(0);

            var bitmap = new BitmapImage();
            await bitmap.SetSourceAsync(stream);
            CaptchaPic.Source = bitmap;
        }
        catch
        {
            CaptchaPic.Source = null;
        }
    }

    private async Task SubmitCaptchaAsync()
    {
        var code = CaptchaBox.Text.Trim();
        if (code.Length == 0)
        {
            CaptchaHint.Text = "还没填验证码";
            CaptchaBox.Focus(FocusState.Programmatic);
            return;
        }

        CaptchaSubmitBtn.IsEnabled = false;
        try
        {
            PersistToStore();
            await _rt.Coordinator.SubmitCaptchaAsync(code);
        }
        finally
        {
            CaptchaSubmitBtn.IsEnabled = true;
        }
    }

    private async Task OnConnectClicked()
    {
        PersistToStore();

        // 空字段直接给焦点，比只在日志里写一句「请先填写账号与密码」有用
        if (string.IsNullOrWhiteSpace(AccountBox.Text))
        {
            _rt.AddLog("请先填写学号");
            AccountBox.Focus(FocusState.Programmatic);
            return;
        }

        if (string.IsNullOrEmpty(PwdBox.Password))
        {
            _rt.AddLog("请先填写密码");
            PwdBox.Focus(FocusState.Programmatic);
            return;
        }

        ConnectBtn.IsEnabled = false;
        try
        {
            await _rt.Coordinator.ManualConnectAsync();
        }
        finally
        {
            if (_uiBuilt) ConnectBtn.IsEnabled = true;
        }
    }

    private async Task OnRefreshClicked()
    {
        _rt.AddLog("手动刷新状态");
        ApplyState(ConnState.Checking, null, animate: true);
        await _rt.Coordinator.RefreshAsync();
    }

    private void SelectOperator(Operator op)
    {
        if (_operator == op)
        {
            SyncOperatorUi();
            return;
        }

        _operator = op;
        SyncOperatorUi();
        _rt.AddLog($"出口切换为 {OperatorName(op)}");
        ScheduleSave();
    }

    private static string OperatorName(Operator op) => op switch
    {
        Operator.Mobile => "中国移动",
        Operator.Unicom => "中国联通",
        Operator.Telecom => "中国电信",
        _ => "校园网直连"
    };

    /// <summary>把字段值同步到下拉框（守卫再入，避免 SelectionChanged 递归）。</summary>
    private void SyncOperatorUi()
    {
        if (!_uiBuilt) return;

        int index = Math.Clamp((int)_operator, 0, 3);
        if (OperatorBox.SelectedIndex == index) return;

        bool ready = _uiReady;
        _uiReady = false;
        OperatorBox.SelectedIndex = index;
        _uiReady = ready;
    }

    // ────────────────────────── 持久化 ──────────────────────────

    private void ScheduleSave()
    {
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    /// <summary>把界面上的值写回运行时配置（关窗/退出前必须先调）。</summary>
    public void PersistToStore()
    {
        if (!_uiBuilt) return;

        var s = _rt.Store.Settings;
        s.Account = AccountBox.Text.Trim();
        s.Operator = _operator;
        s.AutoReconnect = AutoReconnectSwitch.IsOn;
        s.MinimizeToTray = TraySwitch.IsOn;
        s.KeepAliveOnLock = LockKeepSwitch.IsOn;

        int idx = IntervalBox.SelectedIndex;
        s.CheckIntervalSeconds = (idx >= 0 && idx < Intervals.Length) ? Intervals[idx] : 8;

        bool settingsSaved = _rt.Store.Save();
        bool passwordSaved = _rt.Store.SavePassword(PwdBox.Password);
        if (!settingsSaved || !passwordSaved)
            _rt.AddLog("账号或密码保存失败，重启后可能丢失，请检查本地配置目录的写入权限");

        if (AutoStartSwitch.IsOn != s.AutoStart
            || AutoStartSwitch.IsOn != StartupManager.IsEnabled())
            _rt.SetAutoStart(AutoStartSwitch.IsOn);

        UpdateAutoPill();
        _rt.ApplyLockPolicy();
        _rt.RefreshTray();
    }

    private void OnClosed(object sender, WindowEventArgs args)
    {
        PersistToStore();

        _saveTimer.Stop();
        _uiBuilt = false;

        _theme.Changed -= OnThemeChanged;
        _rt.StateChanged -= OnRuntimeState;
        _rt.Logged -= OnRuntimeLog;
        _rt.SettingsChanged -= OnRuntimeSettings;
        _rt.LegacyHostsChanged -= OnLegacyHostsChanged;
        _rt.Coordinator.CaptchaChanged -= OnCaptchaChanged;
        _rt.Devices.Changed -= OnDeviceListChanged;

        _closeHook?.Dispose();
        _closeHook = null;

        _rt.OnWindowTornDown(this);
    }
}

/// <summary>按颜色复用 SolidColorBrush，避免每次巡检/状态刷新都 new 一次。</summary>
internal static class BrushCache
{
    private static readonly System.Collections.Generic.Dictionary<uint, SolidColorBrush> Cache = new();

    public static SolidColorBrush Get(Color c)
    {
        uint key = ((uint)c.A << 24) | ((uint)c.R << 16) | ((uint)c.G << 8) | c.B;

        if (Cache.TryGetValue(key, out var b)) return b;

        if (Cache.Count > 64) Cache.Clear();

        b = new SolidColorBrush(c);
        Cache[key] = b;
        return b;
    }
}
