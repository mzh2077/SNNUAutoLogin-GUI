using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Microsoft.UI;
using Microsoft.UI.Input;
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
    private ConnState _lastState = ConnState.Idle;
    private bool _uiReady;
    private bool _uiBuilt;
    private bool _hiddenToTray;
    private bool _startHidden;
    private bool _settingsOpen;
    private bool _aboutOpen;
    private bool _guardStatusSeen;
    private string _titleBarRegion = "unset";
    private double _titleBarInset = 146;
    private string _titleBarInsetText = "unset";
    private bool _logScrollQueued;
    private bool _legacyDismissed;
    private bool _legacyCleaning;

    private readonly DispatcherTimer _saveTimer = new() { Interval = TimeSpan.FromMilliseconds(450) };

    /// <summary>巡检时长的合法范围（秒）。存设置与落盘都用这一份，避免几处各写各的。</summary>
    private const int MinIntervalSeconds = 4;
    private const int MaxIntervalSeconds = 120;

    public MainWindow(AppRuntime runtime)
    {
        _rt = runtime;
        _theme = runtime.Theme;

        BuildUi();

        Title = "SNNU 校园网助手";
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
        _rt.UpdateAvailable += ShowUpdateLink;
        _rt.SettingsChanged += OnRuntimeSettings;
        _rt.LegacyHostsChanged += OnLegacyHostsChanged;
        _rt.Devices.Changed += OnDeviceListChanged;
        _rt.Guard.Changed += OnGuardChanged;

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

        // 设置项收进独立页面之后，首页只剩三张卡，840 会空出一大截，压到 780。
        int targetH = (int)(780 * scale);

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

        // 右侧留白必须问系统要：caption 按钮宽度随 DPI 变化，写死 146 在 150%/200% 下会错位
        UpdateTitleBarInset();
        DispatcherQueue.TryEnqueue(() =>
        {
            UpdateTitleBarInset();
            UpdateTitleBarInteractiveRegion();
        });
    }

    /// <summary>
    /// 把标题栏右侧让给系统的三枚按钮。RightInset 是物理像素，除以 DPI 才算逻辑宽度。
    /// 取不到就退回三枚 46px 按钮的 138，至少不会把齿轮盖住。
    /// </summary>
    private void UpdateTitleBarInset()
    {
        if (_appWindow is null || AppTitleBar is null) return;

        try
        {
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            double scale = GetDpiForWindow(hwnd) / 96.0;
            if (scale <= 0) scale = 1.0;

            int insetPx = _appWindow.TitleBar.RightInset;
            double inset = insetPx > 0 ? insetPx / scale : 138.0;
            if (inset < 90) inset = 138.0;

            if (Math.Abs(inset - _titleBarInset) < 0.5) return;

            _titleBarInset = inset;
            _titleBarInsetText = $"raw={insetPx} scale={scale:F2} logical={inset:F1}";
            AppTitleBar.Margin = new Thickness(0, 0, inset, 0);

            // caption 按钮宽度随 DPI 变（175% 下每枚只有 39 逻辑像素），齿轮跟着它一起缩放才不显突兀
            SettingsNavBtn.Width = Math.Max(36.0, inset / 3.0);
        }
        catch (Exception ex)
        {
            _titleBarRegion = "inset-err:" + ex.GetType().Name;
        }
    }

    /// <summary>
    /// SetTitleBar 之后整条标题栏都算拖动区，齿轮按钮会被吃掉点击。
    /// 把按钮那块显式声明成 Passthrough（属于客户端），拖动与点击各归各位。
    /// 坐标要物理像素，且窗口每次改尺寸都得重算。
    /// </summary>
    private void UpdateTitleBarInteractiveRegion()
    {
        if (_appWindow is null || SettingsNavBtn is null)
        {
            _titleBarRegion = $"skip appWindow={_appWindow is not null} btn={SettingsNavBtn is not null}";
            return;
        }

        try
        {
            double w = SettingsNavBtn.ActualWidth, h = SettingsNavBtn.ActualHeight;
            if (w <= 0 || h <= 0)
            {
                _titleBarRegion = $"zero {w:F0}x{h:F0} vis={SettingsNavBtn.Visibility}";
                return;
            }

            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            double scale = GetDpiForWindow(hwnd) / 96.0;
            if (scale <= 0) scale = 1.0;

            var origin = SettingsNavBtn.TransformToVisual(Root)
                .TransformPoint(new Windows.Foundation.Point(0, 0));

            // 四周各外扩 2px：按钮边缘那一像素的判定别留给运气
            var rect = new RectInt32(
                (int)Math.Round(origin.X * scale) - 2,
                (int)Math.Round(origin.Y * scale) - 2,
                (int)Math.Round(w * scale) + 4,
                (int)Math.Round(h * scale) + 4);

            var id = Win32Interop.GetWindowIdFromWindow(hwnd);
            InputNonClientPointerSource.GetForWindowId(id)
                .SetRegionRects(NonClientRegionKind.Passthrough, new[] { rect });

            _titleBarRegion = $"{rect.X},{rect.Y} {rect.Width}x{rect.Height}";
        }
        catch (Exception ex)
        {
            _titleBarRegion = "err:" + ex.GetType().Name + ":" + ex.Message;
        }
    }

    // ────────────────────────── 页面导航（首页 ↔ 设置） ──────────────────────────

    /// <summary>
    /// 只切可见性，不动动画。重建内容树后恢复停留页面也走这里。
    /// 齿轮按钮在设置页和关于页隐去——那两页自己有返回箭头，两个入口同时出现反而让人犹豫。
    /// </summary>
    private void ApplyPageVisibility(bool settings)
    {
        if (!_uiBuilt) return;

        bool inSettings = settings && !_aboutOpen;

        HomePage.Visibility = (inSettings || _aboutOpen) ? Visibility.Collapsed : Visibility.Visible;
        SettingsPage.Visibility = inSettings ? Visibility.Visible : Visibility.Collapsed;
        AboutPage.Visibility = _aboutOpen ? Visibility.Visible : Visibility.Collapsed;
        SettingsNavBtn.Visibility = (inSettings || _aboutOpen) ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>整页切换。设置页的卡片多，切过去时给它一段和首帧一致的入场。</summary>
    private void ShowSettingsPage(bool settings, bool animate = true)
    {
        if (!_uiBuilt) return;

        _aboutOpen = false;
        bool changed = _settingsOpen != settings;
        _settingsOpen = settings;

        ApplyPageVisibility(settings);

        if (!animate || !changed) return;

        if (settings)
        {
            SettingsScroll.ChangeView(null, 0, null, true);
            Reenter(AccountCard, 0);
            Reenter(BehaviorCard, 30);
            Reenter(RulesCard, 45);
        }
        else
        {
            Reenter(StatusCard, 0);
            Reenter(DeviceCard, 30);
            Reenter(LogCard, 45);
        }
    }

    /// <summary>关于页是设置页的下一层：从页头的信息按钮进，返回回到设置页。</summary>
    private void ShowAboutPage(bool about, bool animate = true)
    {
        if (!_uiBuilt) return;

        bool changed = _aboutOpen != about;
        _aboutOpen = about;
        ApplyPageVisibility(_settingsOpen);

        if (!animate || !changed || !about) return;

        AboutScroll.ChangeView(null, 0, null, true);
        Reenter(AboutHero, 0);
        Reenter(AboutLinks, 40);
    }

    /// <summary>
    /// FallIn 只补位移不补透明度（FadeTo 的起点取当前值，已经是 1 就没淡入），
    /// 所以换页时先把透明度清零。
    /// </summary>
    private static void Reenter(FrameworkElement el, int delayMs)
    {
        if (el is null) return;
        el.Opacity = 0;
        Fx.FallIn(el, delayMs, 12, Fx.SlowMs);
    }

    /// <summary>标题栏首次布局完成（以及每次改尺寸、换显示器改 DPI）时，按钮才量得到实际宽高。</summary>
    private void OnTitleBarSized(object sender, SizeChangedEventArgs e)
    {
        UpdateTitleBarInset();
        UpdateTitleBarInteractiveRegion();
    }

    private void OnSettingsNavClicked(object sender, RoutedEventArgs e) => ShowSettingsPage(true);
    private void OnSettingsBackClicked(object sender, RoutedEventArgs e) => ShowSettingsPage(false);
    private void OnAboutNavClicked(object sender, RoutedEventArgs e) => ShowAboutPage(true);
    private void OnAboutBackClicked(object sender, RoutedEventArgs e) => ShowAboutPage(false);

    /// <summary>探测地址、超时这些一年改不上一次，默认收起。</summary>
    private void OnGuardAdvancedToggled(object sender, RoutedEventArgs e)
    {
        if (!_uiBuilt) return;

        bool show = GuardAdvancedPanel.Visibility != Visibility.Visible;
        if (show)
        {
            Fx.Reveal(GuardAdvancedPanel, 6f, Fx.FastMs);
            GuardAdvancedToggleText.Text = "收起高级设置";
            GuardAdvancedChevron.Glyph = "\uE70E";
        }
        else
        {
            Fx.Conceal(GuardAdvancedPanel, Fx.FastMs);
            GuardAdvancedToggleText.Text = "高级探测设置";
            GuardAdvancedChevron.Glyph = "\uE70D";
        }
    }

    /// <summary>窗口最小化：延迟一拍归还工作集，避免打断最小化过渡动画。</summary>
    private async void OnAppWindowChanged(AppWindow sender, AppWindowChangedEventArgs args)
    {
        // 齿轮的点击区按客户区坐标算，窗口一改尺寸就得重报
        if (args.DidSizeChange) DispatcherQueue.TryEnqueue(UpdateTitleBarInteractiveRegion);

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
        ShowUpdateLink();

        // 先置位再灌值：ApplyState 都会检查 _uiBuilt
        _uiBuilt = true;

        LoadSettingsToUi();
        FillLogsFromRuntime();
        WireEvents();
        OnDeviceListChanged();

        // 在线设备默认就加载：进窗口就看到当前在线几台，不用再点一次刷新。
        // 放在这里而不是构造函数里，是为了让「加载中」的状态能正常显示出来。
        _ = RefreshDevicesAsync();

        ApplyThemeColors();
        ApplyState(_rt.State, _rt.LastResult, animate: false);

        // 重建窗口时验证码可能已经在了（从托盘召回的情形）
        _ = SyncCaptchaAsync();

        FitToggleWidths();
        DispatcherQueue.TryEnqueue(FitToggleWidths);

        // XAML 重建后默认停在首页，把离开前那一页补回来
        ApplyPageVisibility(_settingsOpen);

        // 首帧把焦点停在根上：否则框架会把焦点丢给第一个可聚焦按钮（「立即登录」），
        // 打开窗口就顶着一圈焦点框，像是被人点过一样。
        try { Root.Focus(FocusState.Programmatic); } catch { }
    }

    /// <summary>
    /// 拆掉内容树：先落盘，再把 Content 置空并清掉所有命名控件字段，
    /// 让整棵树（含 ListView 容器、文本排版缓存）真正可被回收。
    /// </summary>
    private void TeardownUi()
    {
        if (!_uiBuilt) return;

        PersistToStore();
        ClearPublicIp();
        _uiBuilt = false;

        // 文本控件跟着树一起没了，重建后要重新等后台推一次守卫状态
        _guardStatusSeen = false;

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
    private void ShowUpdateLink()
    {
        if (UpdateLink is null || _rt.NewReleaseTag is not { } tag) return;

        UpdateLink.Content = $"发现新版本 {tag} · 前往下载";
        UpdateLink.NavigateUri = new Uri(ReleaseUpdateCheck.ReleaseUrl(tag));
        UpdateLink.Visibility = Visibility.Visible;
    }

    private void ShowVersion()
    {
        try
        {
            var v = typeof(MainWindow).Assembly.GetName().Version;
            AboutVersionText.Text = v is null ? "版本 —" : $"版本 {v.Major}.{v.Minor}.{v.Build}";
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
    /// 只给当前那一页的卡片做——隐藏页里的元素做动画既看不见又白费一次合成。
    /// </summary>
    private void PlayIntro()
    {
        if (!_uiBuilt) return;

        // 从托盘召回时可能停在设置页，XAML 重建后默认是首页，这里补回来
        ApplyPageVisibility(_settingsOpen);

        if (_settingsOpen)
        {
            Reenter(AccountCard, 0);
            Reenter(BehaviorCard, 30);
            Reenter(RulesCard, 45);
        }
        else
        {
            Reenter(StatusCard, 0);
            Reenter(DeviceCard, 30);
            Reenter(LogCard, 45);
        }
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

        if (_rt.Exiting) return !_rt.ShutdownComplete;
        if (_rt.Store.Settings.MinimizeToTray && _rt.Tray?.IsVisible == true)
            HideToTray();
        else
            _rt.Exit();
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
            return;
        }

        // 窗口被激活说明用户可能刚换了网：重新判一次接入方式，规则卡片要跟上
        _rt.Access.Invalidate();
        RefreshRulesUi();

        // 首帧布局这时才算完，齿轮才量得到实际宽高
        UpdateTitleBarInteractiveRegion();
    }

    // ────────────────────────── 初始化 ──────────────────────────

    /// <summary>开发期用：--dark 强制深色，配合 --snapshot 验证界面。</summary>
    private void ApplyCommandLineOverrides()
    {
        foreach (var arg in Environment.GetCommandLineArgs())
        {
            if (arg.Equals("--dark", StringComparison.OrdinalIgnoreCase))
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
        DefaultOperatorBox.SelectedIndex = Array.IndexOf(new[] { Operator.Campus, Operator.Mobile, Operator.Unicom, Operator.Telecom }, s.Operator);
        int hours = Math.Clamp(s.ReauthenticateIntervalHours, 0, 8760);
        ReauthenticateSwitch.IsOn = hours > 0;
        ReauthenticateUnitBox.SelectedIndex = hours > 0 && hours % 24 == 0 ? 1 : 0;
        ReauthenticateValueBox.Value = hours == 0 ? 24 : ReauthenticateUnitBox.SelectedIndex == 1 ? hours / 24 : hours;
        UpdateReauthenticateControls();
        AutoStartSwitch.IsOn = s.AutoStart && StartupManager.IsEnabled();
        AutoReconnectSwitch.IsOn = s.AutoReconnect;
        TraySwitch.IsOn = s.MinimizeToTray;
        LockKeepSwitch.IsOn = s.KeepAliveOnLock;

        // 巡检时长：直接填数值，没有下拉索引这一层，也就不会再出现「填了 20 却被写回 8」
        IntervalBox.Value = Math.Clamp(s.CheckIntervalSeconds, MinIntervalSeconds, MaxIntervalSeconds);

        DeviceGuardSwitch.IsOn = s.DeviceGuardEnabled;
        DeviceGuardThresholdBox.Value = s.DeviceGuardThreshold;
        DeviceGuardProbeTargetBox.Text = s.DeviceGuardProbeTarget;
        DeviceGuardExternalProbeTargetBox.Text = s.DeviceGuardExternalProbeTarget;
        DeviceGuardTimeoutBox.Value = s.DeviceGuardProbeTimeoutSeconds;
        UpdateGuardUiEnabledState();

        // 后台还没推过状态时，卡片底部的占位文案要跟开关一致：
        // 开关明明开着，下面却写「守卫未启用」，看着像坏了。
        if (!_guardStatusSeen)
            DeviceGuardStateText.Text = s.DeviceGuardEnabled ? "监测已启用，等待首次检测" : "守卫未启用";

        SyncLegacyHostsBar();
        _uiReady = ready;

        RefreshRulesUi();
    }

    /// <summary>守卫关闭时把下面三项置灰 —— 但已经填好的值要留着，别让人重填一遍。</summary>
    private void UpdateGuardUiEnabledState()
    {
        bool on = DeviceGuardSwitch.IsOn;
        DeviceGuardThresholdBox.IsEnabled = on;
        DeviceGuardProbeTargetBox.IsEnabled = on;
        DeviceGuardExternalProbeTargetBox.IsEnabled = on;
        DeviceGuardTimeoutBox.IsEnabled = on;
        DeviceGuardProbeNowBtn.IsEnabled = on;
    }

    private void FillLogsFromRuntime()
    {
        Logs.Clear();
        foreach (var e in _rt.LogSnapshot) Logs.Add(e);

        while (Logs.Count > MaxLogLines) Logs.RemoveAt(0);
        ScrollLogToEnd();
    }

    // ────────────────────────── 同时在线设备监测 ──────────────────────────

    /// <summary>守卫状态变化（后台线程触发，切回 UI 线程刷新卡片）。</summary>
    private void OnGuardChanged(DeviceGuardStatus status)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            if (!_uiBuilt) return;

            _guardStatusSeen = true;

            string state = status.State switch
            {
                DeviceGuardState.Disabled => "守卫未启用",
                DeviceGuardState.Idle => "待检测",
                DeviceGuardState.Probing => "正在检测出网连通性…",
                DeviceGuardState.Disposing => "正在断开本机连接…",
                DeviceGuardState.Reauthenticating => "正在重新认证…",
                DeviceGuardState.Cooldown => "冷却中，稍后再检测",
                DeviceGuardState.Stopped => "已停止自动处置，请检查网络或手动重新连接",
                _ => "待检测"
            };

            DeviceGuardStateText.Text = $"{state} · 在线设备 {status.DeviceCount} 台 · 阈值 {status.Threshold} · 统计端口 {status.StatisticPort}";

            DeviceGuardLastText.Text = status.LastProbeAt is null
                ? "尚未检测"
                : $"上次检测 {status.LastProbeAt:HH:mm:ss} · {(status.LastProbeOk == true ? "通过" : "失败")} · {status.LastMessage}";

            RefreshRulesUi();
        });
    }

    /// <summary>「立即检测一次」：只探测，不处置。</summary>
    private void OnGuardProbeNowClicked(object sender, RoutedEventArgs e) => _ = ProbeNowAsync();

    private async Task ProbeNowAsync()
    {
        DeviceGuardProbeNowBtn.IsEnabled = false;
        DeviceGuardLastText.Text = "正在检测…";

        try
        {
            UpdateGuardProbeSettings();
            var result = await _rt.Guard.ProbeNowAsync();
            DeviceGuardLastText.Text = result.Success
                ? $"出网正常（{result.Detail}）"
                : $"出网异常（{result.Detail}）";
        }
        catch (Exception ex)
        {
            DeviceGuardLastText.Text = $"检测失败：{ex.Message}";
        }
        finally
        {
            UpdateGuardUiEnabledState();
        }
    }

    // ────────────────────────── 连接规则 ──────────────────────────

    private string? _displayedRuleId;

    /// <summary>刷新规则列表与「当前接入 / 生效规则」两行。</summary>
    private void RefreshRulesUi(bool onlyIfChanged = false)
    {
        if (!_uiBuilt) return;

        var rules = _rt.Store.Settings.ConnectionRules;
        var profile = _rt.Profiles.Resolve(out var access);
        var currentLink = access.Kind switch
        {
            NetworkAccessKind.Wired => "当前接入：有线",
            NetworkAccessKind.Wireless when access.SsidReliable => $"当前接入：无线 · SSID『{access.Ssid}』",
            NetworkAccessKind.Wireless => "当前接入：无线",
            _ => "当前接入：未知"
        };

        var source = profile.Rule is { } rule ? DescribeRule(rule) : "默认账号";
        var activeRule = $"生效规则：{source} · 账号 {profile.Account} · 出口 {OperatorName(profile.Operator)} · 端口 {profile.Port}";
        if (access.Kind == NetworkAccessKind.Wireless && !access.SsidReliable && ConnectionRuleSet.NeedsSsid(rules))
        {
            var reason = access.SsidAccessDenied
                ? "Windows 拒绝读取 SSID，请检查位置访问权限"
                : "未能读取 SSID";
            activeRule += $"（{reason}）";
        }

        var ruleId = profile.Rule?.Id;
        if (onlyIfChanged && _displayedRuleId == ruleId
            && RulesCurrentLinkText.Text == currentLink && RulesActiveText.Text == activeRule)
            return;

        _displayedRuleId = ruleId;
        RulesCurrentLinkText.Text = currentLink;
        RulesActiveText.Text = activeRule;

        var items = new List<RuleItem>();
        foreach (var r in ConnectionRuleSet.Sorted(rules))
        {
            bool active = string.Equals(ruleId, r.Id, StringComparison.Ordinal);
            items.Add(new RuleItem(r, active, _rt.Store.HasRulePassword(r.CredentialKey)));
        }

        RulesList.ItemsSource = items;
        RulesEmptyText.Visibility = items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>添加一条规则。</summary>
    private void OnRuleAddClicked(object sender, RoutedEventArgs e) => _ = AddRuleAsync();

    private async Task AddRuleAsync()
    {
        var rule = await ShowRuleDialogAsync(null);
        if (rule is null) return;

        var rules = _rt.Store.Settings.ConnectionRules;
        rules.Add(rule);
        if (!SaveRules()) return;

        _rt.AddLog($"已添加连接规则：{DescribeRule(rule)}");
        RefreshRulesUi();
    }

    /// <summary>编辑一条规则。</summary>
    private void OnRuleEditClicked(object sender, RoutedEventArgs e) => _ = EditRuleAsync(sender);

    private async Task EditRuleAsync(object sender)
    {
        if (sender is not FrameworkElement fe || fe.Tag is not ConnectionRule target) return;

        var edited = await ShowRuleDialogAsync(target);
        if (edited is null) return;

        var rules = _rt.Store.Settings.ConnectionRules;
        int index = rules.FindIndex(r => string.Equals(r.Id, target.Id, StringComparison.Ordinal));
        if (index < 0) return;

        rules[index] = edited;
        if (!SaveRules()) return;

        _rt.AddLog($"已修改连接规则：{DescribeRule(edited)}");
        RefreshRulesUi();
    }

    /// <summary>删除一条规则（连带删掉它的密码）。</summary>
    private void OnRuleDeleteClicked(object sender, RoutedEventArgs e) => _ = DeleteRuleAsync(sender);

    private async Task DeleteRuleAsync(object sender)
    {
        if (sender is not FrameworkElement fe || fe.Tag is not ConnectionRule target) return;

        var dialog = new ContentDialog
        {
            XamlRoot = Root.XamlRoot,
            Title = "删除这条连接规则？",
            Content = $"「{DescribeRule(target)}」会被删除，这条规则保存的密码也会一并清除。",
            PrimaryButtonText = "删除",
            SecondaryButtonText = "取消",
            DefaultButton = ContentDialogButton.Secondary
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

        var rules = _rt.Store.Settings.ConnectionRules;
        rules.RemoveAll(r => string.Equals(r.Id, target.Id, StringComparison.Ordinal));

        // 凭据的删除失败只记日志，不阻断删除
        if (!_rt.Store.DeleteRulePassword(target.CredentialKey))
            _rt.AddLog("该规则的密码清理失败，请稍后重试");

        if (!SaveRules()) return;

        _rt.AddLog($"已删除连接规则：{DescribeRule(target)}");
        RefreshRulesUi();
    }

    /// <summary>规则增删改之后统一落盘。</summary>
    private bool SaveRules()
    {
        var s = _rt.Store.Settings;
        var valid = ConnectionRuleSet.ValidateSet(s.ConnectionRules, out var dropped);

        foreach (var d in dropped) _rt.AddLog($"有一条连接规则未生效：{d}");

        s.ConnectionRules = valid;
        if (!_rt.Store.Save())
        {
            _rt.AddLog("连接规则保存失败，请检查本地配置目录的写入权限");
            return false;
        }

        // 规则变了，生效档案也要跟着重算
        _rt.Access.Invalidate();
        return true;
    }

    private static string DescribeRule(ConnectionRule rule) => rule.MatchType switch
    {
        ConnectionMatchType.Wired => "有线",
        ConnectionMatchType.WirelessAny => "无线（任意 SSID）",
        _ => $"无线（指定 SSID『{rule.Ssid}』）"
    };

    /// <summary>规则编辑对话框。返回 null 表示用户取消。</summary>
    private async Task<ConnectionRule?> ShowRuleDialogAsync(ConnectionRule? existing)
    {
        bool editing = existing is not null;

        ConnectionRule rule;
        string initialPassword = "";
        if (existing is null)
        {
            // 只在添加规则时读取 SSID，档案解析复用这次查询结果。
            var access = _rt.Access.GetCurrentWithSsid();
            var profile = _rt.Profiles.Resolve(access);
            rule = new ConnectionRule
            {
                Id = Guid.NewGuid().ToString("N"),
                MatchType = access.Kind == NetworkAccessKind.Wireless
                    ? ConnectionMatchType.WirelessSsid
                    : ConnectionMatchType.Wired,
                Ssid = access.SsidReliable ? access.Ssid : "",
                Account = profile.Account,
                Operator = profile.Operator,
                Enabled = true
            };
            initialPassword = profile.Password;
        }
        else
        {
            rule = new ConnectionRule
            {
                Id = existing.Id,
                MatchType = existing.MatchType,
                Ssid = existing.Ssid,
                Account = existing.Account,
                Operator = existing.Operator,
                Enabled = existing.Enabled,
                PasswordRef = existing.PasswordRef
            };
        }

        var inputStyle = (Style)Root.Resources["FormInputStyle"];
        var matchBox = new ComboBox { Style = inputStyle, HorizontalAlignment = HorizontalAlignment.Stretch };
        matchBox.Items.Add("有线");
        matchBox.Items.Add("无线（任意 SSID）");
        matchBox.Items.Add("无线（指定 SSID）");
        matchBox.SelectedIndex = (int)rule.MatchType;

        var ssidBox = new TextBox { Style = inputStyle, MaxLength = 32, PlaceholderText = "例如：校园网", Text = rule.Ssid };
        var accountBox = new TextBox { Style = inputStyle, PlaceholderText = "学号", Text = rule.Account };

        var pwdBox = new PasswordBox
        {
            Style = inputStyle,
            Password = initialPassword,
            PasswordRevealMode = PasswordRevealMode.Peek,
            PlaceholderText = editing ? "留空表示不修改" : "校园网密码"
        };

        var operators = Enum.GetValues<Operator>();
        var operatorBox = new ComboBox
        {
            Style = inputStyle,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            ItemsSource = operators.Select(OperatorName).ToArray(),
            SelectedIndex = Array.IndexOf(operators, rule.Operator)
        };

        var errorText = new TextBlock
        {
            Style = (Style)Root.Resources["ErrorTextStyle"],
            Visibility = Visibility.Collapsed
        };

        void UpdateSsidEnabled()
        {
            ssidBox.IsEnabled = matchBox.SelectedIndex == (int)ConnectionMatchType.WirelessSsid;
        }

        matchBox.SelectionChanged += (_, _) => UpdateSsidEnabled();
        UpdateSsidEnabled();

        var panel = new StackPanel { Spacing = 10, Width = 320 };

        var titleStyle = (Style)Root.Resources["RowTitleStyle"];
        var hintStyle = (Style)Root.Resources["HintStyle"];

        void AddRow(string label, string description, Control control)
        {
            panel.Children.Add(new StackPanel
            {
                Spacing = 4,
                Children =
                {
                    new TextBlock { Text = label, Style = titleStyle },
                    new TextBlock { Text = description, Style = hintStyle },
                    control
                }
            });
        }

        AddRow("匹配方式", "按当前接入方式决定这条规则何时生效", matchBox);
        AddRow("SSID", "无线网络的名称，只在「无线（指定 SSID）」时生效", ssidBox);
        AddRow("账号", "登录校园网用的学号，留空则沿用默认账号", accountBox);
        AddRow("密码", "该学号对应的密码，编辑时留空表示不修改", pwdBox);
        AddRow("出口", "运营商出口，决定这段流量从哪条链路出去", operatorBox);
        panel.Children.Add(errorText);

        var dialog = new ContentDialog
        {
            XamlRoot = Root.XamlRoot,
            Title = editing ? "编辑连接规则" : "添加连接规则",
            Content = panel,
            PrimaryButtonText = "保存",
            SecondaryButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary
        };

        dialog.PrimaryButtonClick += (_, args) =>
        {
            rule.MatchType = (ConnectionMatchType)Math.Max(0, matchBox.SelectedIndex);
            rule.Ssid = rule.MatchType == ConnectionMatchType.WirelessSsid ? ssidBox.Text.Trim() : "";
            rule.Account = accountBox.Text.Trim();
            rule.Operator = operatorBox.SelectedIndex >= 0
                ? operators[operatorBox.SelectedIndex] : Operator.Campus;

            // 唯一性校验：把别的规则都算上（自己除外）
            var others = new List<ConnectionRule>();
            foreach (var r in _rt.Store.Settings.ConnectionRules)
            {
                if (!string.Equals(r.Id, rule.Id, StringComparison.Ordinal)) others.Add(r);
            }

            var error = ConnectionRuleSet.Validate(rule, others);
            if (error is not null)
            {
                errorText.Text = error;
                errorText.Visibility = Visibility.Visible;
                args.Cancel = true;      // 别关掉对话框，让用户改
                return;
            }

            string password = pwdBox.Password;
            if (!string.IsNullOrEmpty(password))
            {
                if (!_rt.Store.SaveRulePassword(rule.CredentialKey, password))
                {
                    errorText.Text = "密码保存失败，请检查本地配置目录的写入权限";
                    errorText.Visibility = Visibility.Visible;
                    args.Cancel = true;
                    return;
                }
            }
            else if (!editing)
            {
                // 新建时密码必填：编辑留空是「不改」，新建留空是「没填」
                errorText.Text = "请填写密码";
                errorText.Visibility = Visibility.Visible;
                args.Cancel = true;
            }
        };

        var result = await dialog.ShowAsync();
        return result == ContentDialogResult.Primary ? rule : null;
    }

    /// <summary>规则列表的一行（供界面绑定）。</summary>
    private sealed class RuleItem
    {
        public RuleItem(ConnectionRule rule, bool isActive, bool hasPassword)
        {
            Rule = rule;
            IsActive = isActive;

            MatchText = rule.MatchType switch
            {
                ConnectionMatchType.Wired => "有线",
                ConnectionMatchType.WirelessAny => "无线（任意 SSID）",
                _ => $"无线（指定 SSID）· SSID：{rule.Ssid}"
            };

            DetailText = $"账号 {rule.Account} · 出口 {DescribeOperator(rule.Operator)} · 密码{(hasPassword ? "已设置" : "未设置")}";
        }

        public ConnectionRule Rule { get; }
        public bool IsActive { get; }
        public string MatchText { get; }
        public string DetailText { get; }
    }

    /// <summary>运营商出口的中文名。</summary>
    private static string DescribeOperator(Operator op) => op switch
    {
        Operator.Campus => "校园网直连",
        Operator.Mobile => "中国移动",
        Operator.Unicom => "中国联通",
        Operator.Telecom => "中国电信",
        _ => op.ToString()
    };

    /// <summary>巡检时长的快捷值按钮。</summary>
    private void OnIntervalQuickClicked(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement fe) return;
        if (fe.Tag is not string tag || !int.TryParse(tag, out int seconds)) return;

        IntervalBox.Value = Math.Clamp(seconds, MinIntervalSeconds, MaxIntervalSeconds);
        ScheduleSave();
    }

    private void UpdateReauthenticateControls()
    {
        ReauthenticateValueBox.IsEnabled = ReauthenticateSwitch.IsOn;
        ReauthenticateUnitBox.IsEnabled = ReauthenticateSwitch.IsOn;
        ReauthenticateValueBox.Maximum = ReauthenticateUnitBox.SelectedIndex == 1 ? 365 : 8760;
    }

    private void WireEvents()
    {
        ConnectBtn.Click += async (_, _) => await OnConnectClicked();
        RefreshBtn.Click += async (_, _) => await OnRefreshClicked();

        AccountBox.TextChanged += (_, _) => ScheduleSave();
        PwdBox.PasswordChanged += (_, _) => ScheduleSave();
        DefaultOperatorBox.SelectionChanged += (_, _) => ScheduleSave();
        ReauthenticateSwitch.Toggled += (_, _) => { UpdateReauthenticateControls(); ScheduleSave(); };
        ReauthenticateValueBox.ValueChanged += (_, _) => ScheduleSave();
        ReauthenticateUnitBox.SelectionChanged += (_, _) => { UpdateReauthenticateControls(); ScheduleSave(); };

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
        IntervalBox.ValueChanged += (_, _) => ScheduleSave();

        // 同时在线设备监测
        DeviceGuardSwitch.Toggled += (_, _) =>
        {
            UpdateGuardUiEnabledState();
            ScheduleSave();
        };
        DeviceGuardThresholdBox.ValueChanged += (_, _) => ScheduleSave();
        DeviceGuardProbeTargetBox.TextChanged += (_, _) => ScheduleSave();
        DeviceGuardExternalProbeTargetBox.TextChanged += (_, _) => ScheduleSave();
        DeviceGuardTimeoutBox.ValueChanged += (_, args) =>
        {
            if (!_uiReady) return;
            if (!double.IsNaN(args.NewValue))
                _rt.Store.Settings.DeviceGuardProbeTimeoutSeconds = Math.Clamp((int)Math.Round(args.NewValue), 1, 30);
            ScheduleSave();
        };
        // 「立即检测一次」与「添加规则」的点击在 XAML 里直接绑到处理器，这里不再重复接线

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
        RefreshRulesUi(onlyIfChanged: true);
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

    private static readonly System.Net.Http.HttpClient PublicIpClient = new(
        new System.Net.Http.SocketsHttpHandler { UseProxy = false })
    {
        Timeout = TimeSpan.FromSeconds(4)
    };

    private System.Threading.CancellationTokenSource? _publicIpRequest;
    private (string? Account, string? Ip, string? LoginTime)? _publicIpSession;
    private long _publicIpCheckedAt;

    private void ClearPublicIp()
    {
        _publicIpRequest?.Cancel();
        _publicIpRequest = null;
        _publicIpSession = null;
        if (PublicIpText is { } text) text.Text = "—";
    }

    private async Task UpdatePublicIpAsync(LoginResult? result, bool force = false)
    {
        var session = (result?.Account, result?.Ip, result?.LoginTime);
        if (_publicIpSession == session && (_publicIpRequest is not null
            || (!force && Environment.TickCount64 - _publicIpCheckedAt < 300_000))) return;

        ClearPublicIp();
        _publicIpSession = session;
        PublicIpText.Text = "查询中…";
        using var request = new System.Threading.CancellationTokenSource();
        _publicIpRequest = request;
        string value = "暂不可用";
        try
        {
            string body = await PublicIpClient.GetStringAsync("https://ipinfo.io/ip", request.Token);
            if (System.Net.IPAddress.TryParse(body.Trim(), out var address))
                value = address.ToString();
        }
        catch (System.Net.Http.HttpRequestException) { }
        catch (OperationCanceledException) { }
        finally
        {
            if (ReferenceEquals(_publicIpRequest, request))
            {
                _publicIpRequest = null;
                _publicIpCheckedAt = Environment.TickCount64;
                if (_uiBuilt) PublicIpText.Text = value;
            }
        }
    }

    private void ApplyState(ConnState state, LoginResult? result, bool animate)
    {
        if (!_uiBuilt) return;

        _lastState = state;
        if (state != ConnState.Online) ClearPublicIp();

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
                _ = UpdatePublicIpAsync(result);
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
        DevicePortText.Text = $"端口 {dm.Port}";

        bool loading = dm.State == DeviceListState.Loading;
        DeviceRefreshBtn.IsEnabled = !loading;

        switch (dm.State)
        {
            case DeviceListState.Loading:
                DeviceSummary.Text = "正在读取在线设备…";
                break;

            case DeviceListState.Ready:
                DeviceSummary.Text = dm.Devices.Count == 0
                    ? "暂无在线设备"
                    : $"共 {dm.Devices.Count} 台在线设备";
                break;

            case DeviceListState.Failed:
                DeviceSummary.Text = "在线设备读取失败";
                break;

            default:
                DeviceSummary.Text = "尚未加载";
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
        await RefreshDevicesAsync();
    }

    /// <summary>
    /// 拉一次在线设备列表。
    ///
    /// 端口取当前生效档案里的那个 —— 门户按线路分配接入端口，
    /// 所以这里不能写死，得跟着门户分配的结果走。
    /// </summary>
    private async Task RefreshDevicesAsync()
    {
        if (!_uiBuilt) return;

        DeviceRefreshBtn.IsEnabled = false;
        DeviceSummary.Text = "正在读取在线设备…";

        try
        {
            int port = _rt.Profiles.Resolve().Port;
            await _rt.Devices.RefreshAsync(port);
        }
        finally
        {
            if (_uiBuilt) DeviceRefreshBtn.IsEnabled = true;
        }
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

        // 验证码长在设置页的「默认账号」卡里。用户这时候多半停在首页，必须把人带过去，
        // 否则面板在看不见的地方亮起来，登录就卡在那儿了。
        if (!_settingsOpen || _aboutOpen)
        {
            ShowSettingsPage(true);
            DispatcherQueue.TryEnqueue(() =>
            {
                try { CaptchaPanel.StartBringIntoView(); } catch { }
            });
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

    private async void OnSwitchOutletClicked(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button) return;
        button.IsEnabled = false;
        try
        {
            PersistToStore();
            var options = Enum.GetValues<Operator>();
            var picker = new ComboBox
            {
                Style = (Style)Root.Resources["FormInputStyle"],
                HorizontalAlignment = HorizontalAlignment.Stretch,
                ItemsSource = options.Select(OperatorName).ToArray(),
                SelectedIndex = Array.IndexOf(options, _rt.Coordinator.ActiveOperator)
            };
            var dialog = new ContentDialog
            {
                XamlRoot = Root.XamlRoot,
                Title = "切换出口",
                Content = new StackPanel { Spacing = 12, Children =
                    { new TextBlock { Text = "确定后将断开本机连接，并使用所选出口重新登录。",
                        TextWrapping = TextWrapping.Wrap }, picker } },
                PrimaryButtonText = "断开并重新登录",
                CloseButtonText = "取消"
            };
            if (await dialog.ShowAsync() != ContentDialogResult.Primary || picker.SelectedIndex < 0) return;
            var selected = options[picker.SelectedIndex];
            await _rt.Coordinator.ManualConnectAsync(selected);
            _rt.Devices.Invalidate();
        }
        catch (Exception ex) { _rt.AddLog($"切换出口失败：{ex.Message}"); }
        finally { if (_uiBuilt) button.IsEnabled = true; }
    }

    private async Task OnConnectClicked()
    {
        bool reconnect = _rt.State == ConnState.Online;
        PersistToStore();

        var profile = _rt.Profiles.Resolve();
        if (string.IsNullOrWhiteSpace(profile.Account))
        {
            _rt.AddLog("请先填写学号");
            AccountBox.Focus(FocusState.Programmatic);
            return;
        }

        if (string.IsNullOrEmpty(profile.Password))
        {
            _rt.AddLog("请先填写密码");
            PwdBox.Focus(FocusState.Programmatic);
            return;
        }

        ConnectBtn.IsEnabled = false;
        try
        {
            await _rt.Coordinator.ManualConnectAsync(reconnect: reconnect);
            if (reconnect) _rt.Devices.Invalidate();
        }
        finally
        {
            if (_uiBuilt) ConnectBtn.IsEnabled = true;
        }
    }

    private async Task OnRefreshClicked()
    {
        RefreshBtn.IsEnabled = false;
        StatusRefreshIcon.Visibility = Visibility.Collapsed;
        StatusRefreshProgress.Visibility = Visibility.Visible;
        StatusRefreshProgress.IsActive = true;
        StatusRefreshMessage.Visibility = Visibility.Collapsed;

        try
        {
            var message = await _rt.Coordinator.RefreshStatusAsync();
            if (_rt.Exiting) return;
            if (message is null)
            {
                if (_rt.State == ConnState.Online)
                    _ = UpdatePublicIpAsync(_rt.LastResult, force: true);
                return;
            }
            StatusRefreshMessage.Text = message;
            StatusRefreshMessage.Visibility = Visibility.Visible;
        }
        catch (Exception ex)
        {
            if (_rt.Exiting) return;
            StatusRefreshMessage.Text = $"刷新失败：{ex.Message}";
            StatusRefreshMessage.Visibility = Visibility.Visible;
        }
        finally
        {
            StatusRefreshProgress.IsActive = false;
            StatusRefreshProgress.Visibility = Visibility.Collapsed;
            StatusRefreshIcon.Visibility = Visibility.Visible;
            RefreshBtn.IsEnabled = true;
        }
    }

    private static string OperatorName(Operator op) => op switch
    {
        Operator.Mobile => "中国移动",
        Operator.Unicom => "中国联通",
        Operator.Telecom => "中国电信",
        _ => "校园网直连"
    };

    // ────────────────────────── 持久化 ──────────────────────────

    private void ScheduleSave()
    {
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    private int ReadGuardProbeTimeout()
    {
        // 文本在 NumberBox 提交 Value 前也可能已被编辑。
        return int.TryParse(DeviceGuardTimeoutBox.Text, out int seconds)
            ? Math.Clamp(seconds, 1, 30)
            : _rt.Store.Settings.DeviceGuardProbeTimeoutSeconds;
    }

    private void UpdateGuardProbeSettings()
    {
        var s = _rt.Store.Settings;
        s.DeviceGuardProbeTarget = NetworkProbeService.NormalizeTarget(DeviceGuardProbeTargetBox.Text);
        s.DeviceGuardExternalProbeTarget = NetworkProbeService.IsValidTarget(DeviceGuardExternalProbeTargetBox.Text)
            ? DeviceGuardExternalProbeTargetBox.Text.Trim() : NetworkProbeService.DefaultExternalTarget;

        s.DeviceGuardProbeTimeoutSeconds = ReadGuardProbeTimeout();
    }

    /// <summary>把界面上的值写回运行时配置（关窗/退出前必须先调）。</summary>
    public void PersistToStore()
    {
        if (_rt.Exiting) return;
        if (!_uiBuilt) return;

        var s = _rt.Store.Settings;
        s.Account = AccountBox.Text.Trim();
        var operators = new[] { Operator.Campus, Operator.Mobile, Operator.Unicom, Operator.Telecom };
        if (DefaultOperatorBox.SelectedIndex >= 0) s.Operator = operators[DefaultOperatorBox.SelectedIndex];
        double interval = ReauthenticateValueBox.Value;
        int multiplier = ReauthenticateUnitBox.SelectedIndex == 1 ? 24 : 1;
        s.ReauthenticateIntervalHours = ReauthenticateSwitch.IsOn
            ? (int)Math.Clamp(double.IsFinite(interval) ? Math.Round(interval) * multiplier : 24, 1, 8760)
            : 0;
        s.AutoReconnect = AutoReconnectSwitch.IsOn;
        s.MinimizeToTray = TraySwitch.IsOn;
        s.KeepAliveOnLock = LockKeepSwitch.IsOn;

        // 巡检时长：NumberBox 的值就是唯一真值，钳到合法范围再落盘
        double raw = IntervalBox.Value;
        s.CheckIntervalSeconds = double.IsNaN(raw)
            ? s.CheckIntervalSeconds
            : Math.Clamp((int)Math.Round(raw), MinIntervalSeconds, MaxIntervalSeconds);

        s.DeviceGuardEnabled = DeviceGuardSwitch.IsOn;

        double threshold = DeviceGuardThresholdBox.Value;
        s.DeviceGuardThreshold = double.IsNaN(threshold) ? 2 : Math.Clamp((int)Math.Round(threshold), 1, 50);

        UpdateGuardProbeSettings();

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
        ClearPublicIp();
        _uiBuilt = false;

        _theme.Changed -= OnThemeChanged;
        _rt.StateChanged -= OnRuntimeState;
        _rt.Logged -= OnRuntimeLog;
        _rt.UpdateAvailable -= ShowUpdateLink;
        _rt.SettingsChanged -= OnRuntimeSettings;
        _rt.LegacyHostsChanged -= OnLegacyHostsChanged;
        _rt.Coordinator.CaptchaChanged -= OnCaptchaChanged;
        _rt.Devices.Changed -= OnDeviceListChanged;
        _rt.Guard.Changed -= OnGuardChanged;

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

