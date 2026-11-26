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
using Windows.ApplicationModel.DataTransfer;
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

    /// <summary>运行日志里最多留几条「事件」（原始日志另有上限）。</summary>
    private const int MaxEvents = 12;

    private readonly AppRuntime _rt;
    private readonly ThemeColors _theme;
    private readonly ObservableCollection<LogEntry> Logs = new();
    private readonly ObservableCollection<UiLog> Events = new();
    private readonly List<UiLog> _eventSource = new(MaxEvents + 2);
    private readonly Queue<LogEntry> _pendingLogs = new();
    private DispatcherQueueTimer? _logFlush;
    private DispatcherQueueTimer? _uiClock;

    private AppWindow? _appWindow;
    private WindowCloseHook? _closeHook;
    private ConnState _lastState = ConnState.Idle;
    private bool _uiReady;
    private bool _uiBuilt;
    private bool _hiddenToTray;
    private bool _startHidden;
    private bool _settingsOpen;
    private bool _syncing;
    private bool _guardStatusSeen;
    private string _titleBarRegion = "unset";
    private double _titleBarInset = 146;
    private string _titleBarInsetText = "unset";
    private bool _logScrollQueued;
    private bool _legacyDismissed;
    private bool _legacyCleaning;
    private bool _deviceExpanded;

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
        InitializeEdgeDock(hwnd);

        double scale = GetDpiForWindow(hwnd) / 96.0;
        if (scale <= 0) scale = 1.0;

        int targetW = (int)(520 * scale);

        // 左侧 48 紧凑栏 + 内容最宽 640：520 宽时内容占满，拉宽到 700 以上才开始留白。
        int targetH = (int)(680 * scale);

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
                presenter.PreferredMinimumWidth = (int)(420 * scale);
                presenter.PreferredMinimumHeight = (int)(560 * scale);
            }
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
            ApplyTitleBarMetrics();
        }
        catch { }

        // 右侧留白必须问系统要：caption 按钮宽度随 DPI 变化，写死 146 在 150%/200% 下会错位
        UpdateTitleBarInset();
        DispatcherQueue.TryEnqueue(() =>
        {
            ApplyTitleBarMetrics();
            UpdateTitleBarInset();
            UpdateTitleBarInteractiveRegion();
        });
    }

    private bool _tallTried;

    /// <summary>
    /// 标题栏行高、齿轮按钮尺寸都跟着系统给的真实标题栏高度走。
    ///
    /// Edge 那一类应用用的是 Tall：标题栏 48 高，系统的 caption 按钮跟着一起变高，
    /// 齿轮做成 48 就与它们同高同中线，不用再靠 ±3 像素的偏移去凑。
    /// Win10 上没有 Tall，读回的仍然是 32，行高与按钮就一起退回 32。
    /// </summary>
    private void ApplyTitleBarMetrics()
    {
        if (_appWindow is null || TitleBarRow is null) return;

        if (!_tallTried)
        {
            _tallTried = true;
            try
            {
                var tb = _appWindow.TitleBar;
                tb.PreferredHeightOption = TitleBarHeightOption.Tall;
                Trace($"标题栏高度：opt={tb.PreferredHeightOption} 物理高={tb.Height} " +
                      $"inset={tb.RightInset}");
            }
            catch (Exception ex) { Trace("Tall 设置失败：" + ex.Message); }
        }

        // ⚠ TitleBar.Height 与 RightInset 一样是【物理像素】，XAML 用逻辑像素。
        // 不除 DPI 的话 175% 下会把 72 当成 72 逻辑像素，齿轮比系统按钮低一截。
        double h;
        try
        {
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            double scale = GetDpiForWindow(hwnd) / 96.0;
            if (scale <= 0) scale = 1.0;

            double px = _appWindow.TitleBar.Height;
            h = px > 0 ? px / scale : 32;
            if (h < 20 || h > 120) h = 32;
        }
        catch { h = 32; }

        if (Math.Abs(TitleBarRow.Height.Value - h) > 0.5)
            TitleBarRow.Height = new GridLength(h);
    }

    /// <summary>
    /// 把标题栏右侧让给系统的三枚按钮。
    ///
    /// RightInset 是物理像素，除以 DPI 就是客户区里那一栏的宽度——caption 按钮
    /// 本来就贴着客户区右沿排，不用再补窗口边框（补了齿轮会压到最小化身上）。
    /// 齿轮自己的宽度也按 caption 按钮的真实宽度算，跟它们一样宽才对得上。
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
            if (inset <= 0 || inset > 400) inset = 138.0;

            if (Math.Abs(inset - _titleBarInset) < 0.5) return;

            _titleBarInset = inset;
            _titleBarInsetText =
                $"raw={insetPx} scale={scale:F2} logical={inset:F1} " +
                $"tbH={_appWindow.TitleBar.Height} opt={_appWindow.TitleBar.PreferredHeightOption}";
            AppTitleBar.Margin = new Thickness(0, 0, inset, 0);
        }
        catch (Exception ex)
        {
            _titleBarRegion = "inset-err:" + ex.GetType().Name;
        }
    }

    /// <summary>
    /// 标题栏上已经没有自定义按钮了（设置入口收到左侧导航里），
    /// 这里只需保证直通区域是空的，整条标题栏都归系统拖动。
    /// </summary>
    private void UpdateTitleBarInteractiveRegion()
    {
        if (_appWindow is null) return;

        try
        {
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            var id = Win32Interop.GetWindowIdFromWindow(hwnd);
            InputNonClientPointerSource.GetForWindowId(id)
                .SetRegionRects(NonClientRegionKind.Passthrough, Array.Empty<RectInt32>());
            _titleBarRegion = "none";
        }
        catch (Exception ex)
        {
            _titleBarRegion = "err:" + ex.GetType().Name;
        }
    }

    // ────────────────────────── 页面导航（NavigationView） ──────────────────────────

    private enum NavPage
    {
        Home,
        Settings,
        Advanced,
        About
    }

    private NavPage _page = NavPage.Home;
    private bool _navSyncing;

    /// <summary>只切可见性，不动动画。重建内容树后恢复停留页面也走这里。</summary>
    private void ApplyPageVisibility(NavPage page)
    {
        if (!_uiBuilt) return;

        HomePage.Visibility = page == NavPage.Home ? Visibility.Visible : Visibility.Collapsed;
        SettingsPage.Visibility = page == NavPage.Settings ? Visibility.Visible : Visibility.Collapsed;
        AdvancedPage.Visibility = page == NavPage.Advanced ? Visibility.Visible : Visibility.Collapsed;
        AboutPage.Visibility = page == NavPage.About ? Visibility.Visible : Visibility.Collapsed;

        _page = page;
        _settingsOpen = page != NavPage.Home;

        SyncNavSelection(page);

        // 隐藏页里的开关还没量过尺寸，切过来之后补量几拍（模板铺开有先后）
        ScheduleFitToggles();

        // 齿轮显隐变了，标题栏的直通区域要跟着重报
        DispatcherQueue.TryEnqueue(UpdateTitleBarInteractiveRegion);
    }

    private void ScheduleFitToggles()
    {
        FitToggleWidths();
        DispatcherQueue.TryEnqueue(FitToggleWidths);
        DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, FitToggleWidths);
    }

    /// <summary>代码里跳页时把左栏选中项拉回来，别让导航和内容对不上。</summary>
    private void SyncNavSelection(NavPage page)
    {
        var item = page switch
        {
            NavPage.Settings => NavSettings,
            NavPage.Advanced => NavAdvanced,
            NavPage.About => NavAbout,
            _ => NavHome
        };

        if (ReferenceEquals(Nav.SelectedItem, item)) return;

        _navSyncing = true;
        try { Nav.SelectedItem = item; } catch { }
        _navSyncing = false;
    }

    private void OnNavSelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (!_uiBuilt || _navSyncing) return;

        var tag = (args.SelectedItemContainer as NavigationViewItem)?.Tag as string;
        var page = tag switch
        {
            "settings" => NavPage.Settings,
            "advanced" => NavPage.Advanced,
            "about" => NavPage.About,
            _ => NavPage.Home
        };

        Navigate(page);
    }

    /// <summary>整页切换。内容多的页给它一段和首帧一致的入场。</summary>
    private void Navigate(NavPage page, bool animate = true)
    {
        if (!_uiBuilt) return;

        bool changed = _page != page;
        ApplyPageVisibility(page);

        if (!animate || !changed) return;

        switch (page)
        {
            case NavPage.Settings:
                SettingsPage.ChangeView(null, 0, null, true);
                Reenter(AccountCard, 0);
                Reenter(BehaviorCard, 30);
                break;

            case NavPage.Advanced:
                AdvancedPage.ChangeView(null, 0, null, true);
                Reenter(IntervalCard, 0);
                Reenter(DeviceGuardCard, 30);
                Reenter(RulesCard, 45);
                break;

            case NavPage.About:
                AboutPage.ChangeView(null, 0, null, true);
                Reenter(AboutCard, 0);
                break;

            default:
                HomePage.ChangeView(null, 0, null, true);
                Reenter(StatusCard, 0);
                Reenter(DeviceCard, 30);
                Reenter(LogCard, 45);
                break;
        }

    }

    /// <summary>自检代码仍在用这个入口：true 去设置页，false 回主页。</summary>
    private void ShowSettingsPage(bool settings, bool animate = true) =>
        Navigate(settings ? NavPage.Settings : NavPage.Home, animate);

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
        LogEvents.ItemsSource = Events;
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
        if (!_rt.IsUiTest) _ = RefreshDevicesAsync();

        ApplyThemeColors();
        ApplyState(_rt.State, _rt.LastResult, animate: false);

        // 重建窗口时验证码可能已经在了（从托盘召回的情形）
        _ = SyncCaptchaAsync();

        // 时长与倒计时每秒走一格；窗口拆掉时随 _uiBuilt 一起停
        StartUiClock();

        // XAML 重建后默认停在首页，把离开前那一页补回来
        ApplyPageVisibility(_page);

        // 开关的轨道宽度要等模板铺开才量得到，先量一次，再补一拍
        FitToggleWidths();
        DispatcherQueue.TryEnqueue(FitToggleWidths);

        // 首帧把焦点停在根上：否则框架会把焦点丢给第一个可聚焦按钮（「立即连接」），
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
        _uiBuilt = false;

        // 文本控件跟着树一起没了，重建后要重新等后台推一次守卫状态
        _guardStatusSeen = false;

        try { LogList.ItemsSource = null; } catch { }
        try { LogEvents.ItemsSource = null; } catch { }
        Logs.Clear();
        Events.Clear();
        _pendingLogs.Clear();
        _logFlush?.Stop();
        _logFlush = null;
        _uiClock?.Stop();

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

    /// <summary>按名字找模板里的子元素（自检用来量开关轨道、输入框内层）。</summary>
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

    /// <summary>把 x:Name 生成的控件字段全部置空，断开它们对已卸载树的强引用。</summary>
    private void DropElementReferences()    {
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

        // 从托盘召回时可能停在别的页，XAML 重建后默认是首页，这里补回来
        ApplyPageVisibility(_page);

        switch (_page)
        {
            case NavPage.Settings:
                Reenter(AccountCard, 0);
                Reenter(BehaviorCard, 30);
                break;

            case NavPage.Advanced:
                Reenter(IntervalCard, 0);
                Reenter(DeviceGuardCard, 30);
                Reenter(RulesCard, 45);
                break;

            case NavPage.About:
                Reenter(AboutCard, 0);
                break;

            default:
                Reenter(StatusCard, 0);
                Reenter(DeviceCard, 30);
                Reenter(LogCard, 45);
                break;
        }
    }

    /// <summary>从托盘召回：内容树在就只显示，不在就重建后再显示。</summary>
    public void RestoreFromTray()
    {
        _edgeDock?.Reveal();
        _hiddenToTray = false;

        BuildUi();

        try { _appWindow?.Show(); } catch { }
        Activate();

        try { SetForegroundWindow(WinRT.Interop.WindowNative.GetWindowHandle(this)); } catch { }

        PlayIntro();

        RunStartupHooks();
    }

    private void HideToTray()
    {
        if (_rt.Tray?.IsVisible != true) return;
        _edgeDock?.Reveal();

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

    /// <summary>
    /// 界面上显示的这一个状态：标题、一句人话、图标、颜色、是否转圈。
    /// 全程序只有这里能产出状态文案 —— 设备读取失败、日志里的告警都不参与，
    /// 否则就会出现「顶部已连接、下面说连不上」这种自相矛盾。
    /// </summary>
    private readonly record struct StateView(string Title, string Subtitle, string Glyph, Color Color, bool Busy)
    {
        public bool Online => Title == "已连接";
    }

    /// <summary>把巡检状态 + 门户可达性折算成唯一可信的界面状态。</summary>
    private StateView DescribeState(ConnState state, LoginResult? result)
    {
        bool portalDown = _rt.PortalDown;

        switch (state)
        {
            case ConnState.Online:
                string rhythm = _rt.Store.Settings.AutoReconnect
                    ? "校园网正常，正在自动保持在线"
                    : "校园网正常";
                return new StateView("已连接", rhythm, "\uE73E", _theme.Success, false);

            case ConnState.Connecting:
                return new StateView("正在认证", "正在向认证服务器提交账号信息…", "\uE895", _theme.Accent, true);

            case ConnState.Checking:
                return new StateView("正在检测", "正在确认当前网络是否已经认证", "\uE72C", _theme.Accent, true);

            case ConnState.CaptchaRequired:
                return new StateView("正在认证", "需要输入图片验证码才能继续", "\uE8D7", _theme.Caution, false);

            case ConnState.Offline when portalDown:
                return new StateView("不在校园网", "打不开认证门户，连回校园网后会自动继续", "\uE774", _theme.Neutral, false);

            case ConnState.Offline:
                return new StateView("未连接", "点下面的按钮登录校园网", "\uE7BA", _theme.Caution, false);

            case ConnState.Failed when portalDown:
                return new StateView("不在校园网", "打不开认证门户，先确认已经连上校园网", "\uE774", _theme.Neutral, false);

            case ConnState.Failed:
                string why = result?.Message is { Length: > 0 } m && m != "未认证"
                    ? m
                    : "账号或密码可能不对";
                return new StateView("认证失败", why, "\uE783", _theme.Critical, false);

            default:
                return new StateView("准备就绪", "正在读取本机状态…", "\uE7E8", _theme.Neutral, false);
        }
    }

    /// <summary>把当前状态对应的图标/圆底/进度环重新上色（复用画刷，避免每轮巡检都分配）。</summary>
    private void RepaintState(ConnState state)
    {
        var view = DescribeState(state, _rt.LastResult);
        var brush = BrushCache.Get(view.Color);

        bool glyphChanged = StateIcon.Glyph != view.Glyph;

        StateIcon.Foreground = brush;
        StateIcon.Glyph = view.Glyph;
        BusyRing.Foreground = brush;

        // 忙碌时只留系统进度环，圆底收起来 —— 一个实心淡色圆盘垫在环后面更像"压了个饼"
        HaloRing.Fill = BrushCache.Get(ThemeColors.WithAlpha(view.Color, view.Online ? 0.14 : 0.10));
        HaloRing.Visibility = view.Busy ? Visibility.Collapsed : Visibility.Visible;

        BusyRing.IsActive = view.Busy;
        BusyRing.Visibility = view.Busy ? Visibility.Visible : Visibility.Collapsed;
        StateIcon.Visibility = view.Busy ? Visibility.Collapsed : Visibility.Visible;

        // 图标切换时弹一下，比硬换字形更容易被眼睛接住
        if (glyphChanged && !view.Busy) Fx.Pop(StateIcon, 0.9f, 280);
    }

    private void OnWindowActivated(object sender, WindowActivatedEventArgs args)
    {
        _windowActive = args.WindowActivationState != WindowActivationState.Deactivated;
        if (args.WindowActivationState == WindowActivationState.Deactivated) return;
        _edgeDock?.Reveal();

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
        int operatorIndex = Array.IndexOf(new[] { Operator.Campus, Operator.Mobile, Operator.Unicom, Operator.Telecom }, s.Operator);
        if (operatorIndex < 0) operatorIndex = 1;
        DefaultOperatorBox.SelectedIndex = operatorIndex;
        QuickOperatorBox.SelectedIndex = operatorIndex;
        int hours = Math.Clamp(s.ReauthenticateIntervalHours, 0, 8760);
        ReauthenticateSwitch.IsOn = hours > 0;
        ReauthenticateUnitBox.SelectedIndex = hours > 0 && hours % 24 == 0 ? 1 : 0;
        ReauthenticateValueBox.Value = hours == 0 ? 24 : ReauthenticateUnitBox.SelectedIndex == 1 ? hours / 24 : hours;
        UpdateReauthenticateControls();
        AutoStartSwitch.IsOn = s.AutoStart && StartupManager.IsEnabled();
        QuickAutoStartSwitch.IsOn = AutoStartSwitch.IsOn;
        AutoReconnectSwitch.IsOn = s.AutoReconnect;
        QuickReconnectSwitch.IsOn = s.AutoReconnect;
        TraySwitch.IsOn = s.MinimizeToTray;
        LockKeepSwitch.IsOn = s.KeepAliveOnLock;

        // 巡检间隔：Slider 的值就是唯一真值，钳到合法范围再落盘
        IntervalSlider.Value = Math.Clamp(s.CheckIntervalSeconds, MinIntervalSeconds, MaxIntervalSeconds);
        UpdateIntervalDesc();

        DeviceGuardSwitch.IsOn = s.DeviceGuardEnabled;
        DeviceGuardThresholdBox.Value = s.DeviceGuardThreshold;
        DeviceGuardProbeTargetBox.Text = s.DeviceGuardProbeTarget;
        DeviceGuardExternalProbeTargetBox.Text = s.DeviceGuardExternalProbeTarget;
        DeviceGuardTimeoutBox.Value = s.DeviceGuardProbeTimeoutSeconds;
        UpdateGuardUiEnabledState();

        // 后台还没推过状态时，卡片底部的占位文案要跟开关一致：
        // 开关明明开着，下面却写「未启用」，看着像坏了。
        if (!_guardStatusSeen)
            DeviceGuardStateText.Text = s.DeviceGuardEnabled ? "监测已启用，等待首次检测" : "监测未启用";

        SyncLegacyHostsBar();
        _uiReady = ready;

        UpdateGuideCard();
        SyncToggleLabels();

        RefreshRulesUi();
    }

    private void UpdateIntervalDesc()
    {
        int seconds = (int)Math.Round(IntervalSlider.Value);
        IntervalValueText.Text = $"{seconds} 秒";
    }

    /// <summary>开关的「开 / 关」文字放在轨道左边，跟 Windows 11 设置应用一致。</summary>
    private void SyncToggleLabels()
    {
        if (!_uiBuilt) return;

        AutoStartState.Text = AutoStartSwitch.IsOn ? "开" : "关";
        QuickAutoStartState.Text = QuickAutoStartSwitch.IsOn ? "开" : "关";
        AutoReconnectState.Text = AutoReconnectSwitch.IsOn ? "开" : "关";
        QuickReconnectState.Text = QuickReconnectSwitch.IsOn ? "开" : "关";
        LockKeepState.Text = LockKeepSwitch.IsOn ? "开" : "关";
        TrayState.Text = TraySwitch.IsOn ? "开" : "关";
        ReauthState.Text = ReauthenticateSwitch.IsOn ? "开" : "关";
        GuardState.Text = DeviceGuardSwitch.IsOn ? "开" : "关";
    }

    /// <summary>
    /// 系统 ToggleSwitch 模板为「开 / 关」文字预留了 154 的 MinWidth，该值由模板内部决定，
    /// 页面级覆盖无效。控件宽度不改的话，轨道会贴在控件左边，右边缘空出一大截。
    /// 这里读模板里轨道（OuterBorder）的实测宽度，把控件夹到刚好等于轨道。
    /// </summary>
    private void FitToggleWidths()
    {
        if (!_uiBuilt) return;

        foreach (var toggle in new[]
                 {
                     AutoStartSwitch, QuickAutoStartSwitch,
                     AutoReconnectSwitch, QuickReconnectSwitch,
                     LockKeepSwitch, TraySwitch,
                     ReauthenticateSwitch, DeviceGuardSwitch
                 })
        {
            if (toggle is null) continue;

            var track = FindDescendant(toggle, "OuterBorder");
            if (track is { ActualWidth: > 1 })
                toggle.Width = track.ActualWidth;
        }
    }

    /// <summary>还没填学号时，主页顶部先给一张登录引导卡；填过就收起来。</summary>
    private void UpdateGuideCard()
    {
        if (!_uiBuilt) return;

        bool needSetup = string.IsNullOrWhiteSpace(AccountBox.Text);
        GuideCard.Visibility = needSetup ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>守卫关闭时整段折叠，不留一排灰控件。值本身留着，重新打开不用重填。</summary>
    private void UpdateGuardUiEnabledState()
    {
        bool on = DeviceGuardSwitch.IsOn;
        GuardRows.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
    }

    private void FillLogsFromRuntime()
    {
        Logs.Clear();
        Events.Clear();

        foreach (var e in _rt.LogSnapshot)
        {
            Logs.Add(e);
            RecordEvent(e);
        }

        while (Logs.Count > MaxLogLines) Logs.RemoveAt(0);

        UpdateLogBadge();
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
                DeviceGuardState.Disabled => "监测未启用",
                DeviceGuardState.Idle => "待命",
                DeviceGuardState.Probing => "正在检查出网",
                DeviceGuardState.Disposing => "正在断开本机连接",
                DeviceGuardState.Reauthenticating => "正在重新认证",
                DeviceGuardState.Cooldown => "稍后再检测",
                DeviceGuardState.Stopped => "已停止自动处理，请检查网络",
                _ => "待命"
            };

            DeviceGuardStateText.Text = $"{state} · 在线 {status.DeviceCount} 台（上限 {status.Threshold}）";

            DeviceGuardLastText.Text = status.LastProbeAt is null
                ? "还没检查过"
                : $"上次检查 {status.LastProbeAt:HH:mm} · {(status.LastProbeOk == true ? "正常" : "异常")} · {status.LastMessage}";

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
        var activeRule = $"当前使用：{source} · {OperatorName(profile.Operator)}";
        if (access.Kind == NetworkAccessKind.Wireless && !access.SsidReliable && ConnectionRuleSet.NeedsSsid(rules))
        {
            var reason = access.SsidAccessDenied
                ? "Windows 拒绝读取 SSID，请检查位置访问权限"
                : "未能读取 SSID";
            activeRule += $"（{reason}）";
        }

        // 端口这类技术细节挪到探测设置里，规则卡上只说人话
        if (ProbePortText is { } port) port.Text = $"统计端口 {profile.Port}";

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
        bool hasRules = items.Count > 0;
        RulesEmptyPanel.Visibility = hasRules ? Visibility.Collapsed : Visibility.Visible;
        RulesListPanel.Visibility = hasRules ? Visibility.Visible : Visibility.Collapsed;
        RulesFallbackHint.Visibility = hasRules ? Visibility.Visible : Visibility.Collapsed;
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

        if (await ShowDialogAsync(dialog) != ContentDialogResult.Primary) return;

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

        var result = await ShowDialogAsync(dialog);
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

    /// <summary>开关关掉时整行折叠 —— 灰着一排用不了的控件比藏起来更让人困惑。</summary>
    private void UpdateReauthenticateControls()
    {
        ReauthRows.Visibility = ReauthenticateSwitch.IsOn ? Visibility.Visible : Visibility.Collapsed;
        ReauthenticateValueBox.Maximum = ReauthenticateUnitBox.SelectedIndex == 1 ? 365 : 8760;
    }

    private void WireEvents()
    {
        ConnectBtn.Click += async (_, _) => await OnConnectClicked();

        AccountBox.TextChanged += (_, _) =>
        {
            ScheduleSave();
            if (_uiReady) UpdateGuideCard();
        };
        PwdBox.PasswordChanged += (_, _) => ScheduleSave();
        DefaultOperatorBox.SelectionChanged += (_, _) => ScheduleSave();
        ReauthenticateSwitch.Toggled += (_, _) =>
        {
            UpdateReauthenticateControls();
            SyncToggleLabels();
            ScheduleSave();
        };
        ReauthenticateValueBox.ValueChanged += (_, _) => ScheduleSave();
        ReauthenticateUnitBox.SelectionChanged += (_, _) => { UpdateReauthenticateControls(); ScheduleSave(); };

        AutoStartSwitch.Toggled += (_, _) =>
        {
            if (!_uiReady) return;
            MirrorToggle(AutoStartSwitch, QuickAutoStartSwitch);
            SyncToggleLabels();
            PersistToStore();
        };
        QuickAutoStartSwitch.Toggled += (_, _) =>
        {
            if (!_uiReady) return;
            MirrorToggle(QuickAutoStartSwitch, AutoStartSwitch);
            SyncToggleLabels();
            PersistToStore();
        };
        AutoReconnectSwitch.Toggled += (_, _) =>
        {
            MirrorToggle(AutoReconnectSwitch, QuickReconnectSwitch);
            SyncToggleLabels();
            ScheduleSave();
        };
        QuickReconnectSwitch.Toggled += (_, _) =>
        {
            if (!_uiReady) return;
            MirrorToggle(QuickReconnectSwitch, AutoReconnectSwitch);
            SyncToggleLabels();
            ScheduleSave();
        };
        LockKeepSwitch.Toggled += (_, _) =>
        {
            SyncToggleLabels();
            ScheduleSave();
            _rt.ApplyLockPolicy();   // 开关一动就生效，不用等下次锁屏
        };
        TraySwitch.Toggled += (_, _) =>
        {
            SyncToggleLabels();
            ScheduleSave();
            _rt.RefreshTray();
        };
        IntervalSlider.ValueChanged += (_, _) =>
        {
            UpdateIntervalDesc();
            ScheduleSave();
        };

        // 同时在线设备监测
        DeviceGuardSwitch.Toggled += (_, _) =>
        {
            UpdateGuardUiEnabledState();
            SyncToggleLabels();
            ScheduleSave();
            UpdateGuardSummarySettings();
        };
        DeviceGuardThresholdBox.ValueChanged += (_, _) => UpdateGuardSummarySettings();
        DeviceGuardThresholdBox.RegisterPropertyChangedCallback(NumberBox.TextProperty,
            (_, _) => UpdateGuardSummarySettings());
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
        Fx.PressFeedback(DeviceToggleBtn);

        // 在线设备：整行展开 / 收起，重试走卡片里的链接
        DeviceToggleBtn.Click += (_, _) => ToggleDeviceBody();
        DeviceRetryBtn.Click += async (_, _) => await OnDeviceRefreshClicked();

        // 日志：复制与清空
        LogCopyBtn.Click += (_, _) => CopyEventLog();
        LogClearBtn.Click += async (_, _) => await ClearEventLogAsync();
        RawLogCopyBtn.Click += (_, _) => CopyRawLog();

        LegacyCleanBtn.Click += (_, _) => OnLegacyCleanClicked();
        LegacyDismissBtn.Click += (_, _) =>
        {
            _legacyDismissed = true;
            Fx.Conceal(LegacyHostsBar);
        };

        // ── 主页上的快捷设置（和设置页是同一份配置，双向镜像） ──
        QuickEditAccountBtn.Click += (_, _) =>
        {
            Navigate(NavPage.Settings);
            DispatcherQueue.TryEnqueue(() =>
            {
                try { AccountBox.Focus(FocusState.Programmatic); } catch { }
            });
        };

        QuickOperatorBox.SelectionChanged += async (_, _) =>
        {
            if (!_uiReady) return;
            if (_syncing) return;

            int index = QuickOperatorBox.SelectedIndex;
            if (index < 0) return;

            _syncing = true;
            DefaultOperatorBox.SelectedIndex = index;
            _syncing = false;

            ScheduleSave();

            // 已经连着的时候换出口，旧认证其实挂在原来的出口上，问一句要不要立刻重连
            if (_rt.State != ConnState.Online) return;

            var dialog = new ContentDialog
            {
                XamlRoot = Root.XamlRoot,
                Title = "换到「" + OperatorName(OperatorAt(index)) + "」？",
                Content = "现在这条连接还挂在原来的出口上。要立刻重新登录吗？不重连的话，下一次掉线后才会用新出口。",
                PrimaryButtonText = "立刻重连",
                CloseButtonText = "以后再说",
                DefaultButton = ContentDialogButton.Primary
            };

            if (await dialog.ShowAsync() == ContentDialogResult.Primary)
                await OnConnectClicked();
        };

        // ── 首次使用：登录引导 ──
        GuideSaveBtn.Click += async (_, _) =>
        {
            string account = GuideAccountBox.Text.Trim();
            if (account.Length == 0)
            {
                ShowGuideError("先填一下学号。");
                return;
            }

            if (GuidePwdBox.Password.Length == 0)
            {
                ShowGuideError("再填一下密码。");
                return;
            }

            _uiReady = false;
            AccountBox.Text = account;
            PwdBox.Password = GuidePwdBox.Password;
            int index = GuideOperatorBox.SelectedIndex;
            if (index >= 0)
            {
                DefaultOperatorBox.SelectedIndex = index;
                QuickOperatorBox.SelectedIndex = index;
            }

            _uiReady = true;
            GuideErrorText.Visibility = Visibility.Collapsed;
            PersistToStore();
            UpdateGuideCard();

            await OnConnectClicked();
        };

        _uiReady = true;
    }

    /// <summary>主页那两个开关只是设置页开关的镜像，改动要互相带上，避免两处不一致。</summary>
    private void MirrorToggle(ToggleSwitch source, ToggleSwitch target)
    {
        if (_syncing) return;

        _syncing = true;
        try { target.IsOn = source.IsOn; } catch { }
        _syncing = false;
    }

    private void ShowGuideError(string message)
    {
        GuideErrorText.Text = message;
        GuideErrorText.Visibility = Visibility.Visible;
    }

    private static Operator OperatorAt(int index) => index switch
    {
        0 => Operator.Campus,
        2 => Operator.Unicom,
        3 => Operator.Telecom,
        _ => Operator.Mobile
    };

    // ────────────────────────── 运行日志交互 ──────────────────────────

    /// <summary>复制给开发者看的这一份：时间 + 一句人话 + 重复次数。</summary>
    private void CopyEventLog()
    {
        var sb = new System.Text.StringBuilder();
        foreach (var e in Events)
        {
            sb.Append(e.Time).Append("  ").Append(e.Text);
            if (e.Count > 1) sb.Append("（×").Append(e.Count).Append('）');
            sb.AppendLine();
        }

        if (sb.Length == 0) sb.AppendLine("（暂无事件）");
        CopyToClipboard(sb.ToString());
    }

    private void CopyRawLog()
    {
        var sb = new System.Text.StringBuilder();
        foreach (var e in Logs) sb.Append(e.Time).Append("  ").AppendLine(e.Text);
        if (sb.Length == 0) sb.AppendLine("（暂无日志）");
        CopyToClipboard(sb.ToString());
    }

    private void CopyToClipboard(string text)
    {
        try
        {
            var package = new DataPackage();
            package.SetText(text);
            Clipboard.SetContent(package);
            _rt.AddLog("日志已复制到剪贴板");
        }
        catch (Exception ex)
        {
            _rt.AddLog($"复制失败：{ex.Message}");
        }
    }

    private async Task ClearEventLogAsync()
    {
        if (Events.Count == 0) return;

        var dialog = new ContentDialog
        {
            XamlRoot = Root.XamlRoot,
            Title = "清空运行日志？",
            Content = "只清空这里显示的事件，不影响账号和设置。",
            PrimaryButtonText = "清空",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close
        };

        if (await ShowDialogAsync(dialog) != ContentDialogResult.Primary) return;

        Events.Clear();
        Logs.Clear();
        _rt.ClearLogs();
        UpdateLogBadge();
        LogEmptyText.Visibility = Visibility.Visible;
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
            RecordEvent(e);
        }

        while (Logs.Count > MaxLogLines) Logs.RemoveAt(0);
        ScrollLogToEnd();
    }

    // ────────────────────────── 日志提炼 ──────────────────────────

    /// <summary>
    /// 把一行内部日志翻译成一句人话；返回 null 表示这条只进原始日志，不往界面上放。
    /// 界面上只留「登录成功 / 掉线重连 / 失败原因 / 验证码 / 网络变化」这几类，
    /// 「守卫状态」「巡检」这种词对普通学生没有意义。
    /// </summary>
    private static string? DescribeEvent(string raw)
    {
        if (raw.StartsWith("登录成功", StringComparison.Ordinal))
            return "已连接校园网";

        if (raw.StartsWith("检测到未认证", StringComparison.Ordinal))
            return "检测到掉线，正在自动重连";

        if (raw.StartsWith("登录失败：", StringComparison.Ordinal))
            return "登录失败：" + raw["登录失败：".Length..];

        if (raw.StartsWith("验证码未通过", StringComparison.Ordinal))
            return "验证码不对，请重新输入";

        if (raw.Contains("需要输入图片验证码", StringComparison.Ordinal)
            || raw.Contains("要求输入验证码", StringComparison.Ordinal)
            || raw.Contains("验证码已过期", StringComparison.Ordinal))
            return "需要输入验证码";

        if (raw.Contains("认证门户不可达", StringComparison.Ordinal))
            return "当前不在校园网，自动登录已暂停";

        if (raw.StartsWith("定时重新认证到期", StringComparison.Ordinal)
            || raw.StartsWith("收到重新认证请求", StringComparison.Ordinal))
            return "按计划重新认证";

        if (raw.StartsWith("本机连接已断开", StringComparison.Ordinal))
            return "已断开连接，稍后会自动重连";

        if (raw.StartsWith("断开原连接失败", StringComparison.Ordinal))
            return "断开旧连接失败，这次没有继续";

        if (raw.Contains("网络状态变化", StringComparison.Ordinal)
            || raw.Contains("网络已切换", StringComparison.Ordinal)
            || raw.Contains("网络发生变化", StringComparison.Ordinal))
            return "网络发生变化，正在重新认证";

        if (raw.StartsWith("刷新失败", StringComparison.Ordinal)
            || raw.StartsWith("刷新超时", StringComparison.Ordinal))
            return "刷新状态失败，请稍后再试";

        return null;
    }

    /// <summary>收一条事件；同一句话在 10 分钟内重复出现就归并计数。</summary>
    private void RecordEvent(LogEntry raw)
    {
        if (DescribeEvent(raw.Text) is not { } text) return;

        if (Events.Count > 0 && Events[^1].Text == text
            && DateTime.Now - Events[^1].At < TimeSpan.FromMinutes(10))
        {
            var last = Events[^1];
            last.Count++;
            last.Time = raw.Time;
            UpdateLogBadge();
            return;
        }

        Events.Add(new UiLog { At = DateTime.Now, Time = raw.Time, Text = text });

        while (Events.Count > MaxEvents) Events.RemoveAt(0);
        UpdateLogBadge();
    }

    private void UpdateLogBadge()
    {
        if (!_uiBuilt) return;
        LogBadge.Text = Events.Count == 0 ? "暂无" : $"{Events.Count} 条";
        LogEmptyText.Visibility = Events.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnRuntimeSettings()
    {
        if (!_uiBuilt) return;
        LoadSettingsToUi();
        RefreshDeviceSummary();
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
            if (RawLogScroll.ScrollableHeight > 0)
                RawLogScroll.ChangeView(null, RawLogScroll.ScrollableHeight, null, true);
        });
    }

    // ────────────────────────── 状态渲染 ──────────────────────────

    private void ApplyState(ConnState state, LoginResult? result, bool animate)
    {
        if (!_uiBuilt) return;

        _lastState = state;
        var view = DescribeState(state, result);

        // 状态一变，上一次刷新留下的提示就过期了
        StatusNotice.Visibility = Visibility.Collapsed;

        RepaintState(state);

        // 文字变化走交叉淡入：状态在「正在认证 → 已连接」之间跳变时不会硬切一下
        Fx.SwapText(StateTitle, view.Title, animate);
        Fx.SwapText(StateSubtitle, view.Subtitle, animate, 4f, 200);

        string account = result?.Account ?? _rt.Store.Settings.Account;
        AccText.Text = ShortAccount(account);
        IpText.Text = state == ConnState.Online ? result?.Ip ?? "—" : "—";

        UpdateClockTexts();
        UpdateAutoPill();

        ConnectLabel.Text = state switch
        {
            ConnState.Online => "重新登录",
            ConnState.Connecting => "正在认证…",
            _ => "立即连接"
        };

        ConnectIcon.Glyph = state switch
        {
            ConnState.Online => "\uE72C",
            ConnState.Connecting => "\uE895",
            _ => "\uE7E8"
        };

        // 认证进行中再点主按钮只会重复提交，先禁掉；其余状态都可点
        ConnectBtn.IsEnabled = !view.Busy && !_rt.Exiting;
        DisconnectItem.IsEnabled = state == ConnState.Online;
        SwitchOutletItem.IsEnabled = !view.Busy;

        // 一直在线的话日志会一条都没有，看起来像坏了。连上时补一条「当前已连接」。
        if (state == ConnState.Online && Events.Count == 0)
        {
            Events.Add(new UiLog
            {
                At = DateTime.Now,
                Time = DateTime.Now.ToString("HH:mm:ss"),
                Text = "当前已连接校园网"
            });
            UpdateLogBadge();
        }
    }

    /// <summary>账号去掉 @mobile 这类出口后缀，信息格里放不下也没必要看。</summary>
    private static string ShortAccount(string? account)
    {
        if (string.IsNullOrWhiteSpace(account)) return "—";
        int at = account.IndexOf('@');
        return at > 0 ? account[..at] : account;
    }

    /// <summary>每秒刷新一次的两格：已连接时长、下次巡检倒计时。</summary>
    private void UpdateClockTexts()
    {
        if (!_uiBuilt) return;

        // 没连接的时候不该显示上一次的时长，那会让人以为现在还连着
        DurationText.Text = _lastState == ConnState.Online
            ? FormatDuration(_rt.LastResult?.LoginTime)
            : "—";

        int left = _rt.SecondsUntilNextCheck;
        CountdownText.Text = _lastState switch
        {
            ConnState.Connecting => "认证中",
            ConnState.CaptchaRequired => "已暂停",
            _ when _rt.Coordinator.LastCheckAt is null => "—",
            _ => left <= 0 ? "马上" : $"{left} 秒后"
        };
    }

    private static string FormatDuration(string? loginTime)
    {
        if (string.IsNullOrWhiteSpace(loginTime)) return "—";
        if (!DateTime.TryParse(loginTime, out var at)) return "—";

        var span = DateTime.Now - at;
        if (span < TimeSpan.Zero) return "刚刚";
        if (span.TotalMinutes < 1) return "刚刚";
        if (span.TotalHours < 1) return $"{(int)span.TotalMinutes} 分钟";
        if (span.TotalDays < 1) return $"{(int)span.TotalHours} 小时 {(int)span.Minutes} 分";
        return $"{(int)span.TotalDays} 天 {(int)span.Hours} 小时";
    }

    /// <summary>UI 秒表：只在窗口存在时跑，拆树时停掉。</summary>
    private void StartUiClock()
    {
        _uiClock ??= DispatcherQueue.CreateTimer();
        _uiClock.Interval = TimeSpan.FromSeconds(1);
        _uiClock.IsRepeating = true;
        _uiClock.Tick -= OnUiClockTick;
        _uiClock.Tick += OnUiClockTick;
        _uiClock.Start();
    }

    private void OnUiClockTick(DispatcherQueueTimer sender, object args)
    {
        if (!_uiBuilt)
        {
            sender.Stop();
            return;
        }

        UpdateClockTexts();
    }

    /// <summary>自动重连状态现在只体现在那两个开关上，这里顺手把开关文字刷一遍。</summary>
    private void UpdateAutoPill()
    {
        if (!_uiBuilt) return;

        SyncToggleLabels();
    }

    // ────────────────────────── 交互 ──────────────────────────

    // ────────────────────────── 在线设备 ──────────────────────────

    /// <summary>在线设备列表。门户在线控制页里的设备表格。</summary>
    private void OnDeviceListChanged()
    {
        if (!_uiBuilt) return;

        var dm = _rt.Devices;

        DeviceList.ItemsSource = dm.Devices;

        RefreshDeviceSummary();

        bool failed = dm.State == DeviceListState.Failed || !string.IsNullOrEmpty(dm.Error);
        DeviceErrorRow.Visibility = failed ? Visibility.Visible : Visibility.Collapsed;
        if (failed)
        {
            DeviceErrorText.Text = "连不上认证门户，设备列表稍后会自动重试。";
            DeviceErrorText.Visibility = Visibility.Visible;
        }
    }

    private void ToggleDeviceBody()
    {
        if (!_uiBuilt) return;

        _deviceExpanded = !_deviceExpanded;
        DeviceBody.Visibility = _deviceExpanded ? Visibility.Visible : Visibility.Collapsed;
        DeviceChevron.Glyph = _deviceExpanded ? "\uE70E" : "\uE70D";

        if (_deviceExpanded && _rt.Devices.State == DeviceListState.Failed)
            _ = RefreshDevicesAsync();
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

        DeviceSummary.Text = "正在读取在线设备…";

        try
        {
            int port = _rt.Profiles.Resolve().Port;
            await _rt.Devices.RefreshAsync(port);
        }
        finally
        {
            if (_uiBuilt) OnDeviceListChanged();
        }
    }

    /// <summary>下线别的设备（门户里的「强制下线」）。</summary>
    private async void OnDeviceOfflineClicked(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        if (!_uiBuilt) return;
        if ((sender as FrameworkElement)?.Tag is not OnlineDevice device) return;

        if (device.IsCurrent || !device.CanForceOffline)
        {
            _rt.AddLog("这是本机的连接，请用「更多 → 断开本机连接」");
            return;
        }

        var button = sender as Button;
        if (button is not null) button.IsEnabled = false;
        try
        {
            _rt.AddLog($"正在下线 {device.DisplayName}（{device.Ip}）…");
            var outcome = await _deviceActions.RunAsync(ct => _rt.Devices.TakeOfflineAsync(device, ct));
            ShowDeviceActionResult(outcome, $"{device.DisplayName} 已下线");
        }
        finally { if (button is not null) button.IsEnabled = true; }
    }

    /// <summary>断开本机当前这条连接：收在「更多」里，且要二次确认。</summary>
    private async void OnDisconnectMenuClicked(object sender, RoutedEventArgs e)
    {
        if (!_uiBuilt || _rt.State != ConnState.Online) return;

        var dialog = new ContentDialog
        {
            XamlRoot = Root.XamlRoot,
            Title = "断开本机连接？",
            Content = "断开后校园网会马上掉线。开了「掉线自动重连」的话，稍后会自动连回来。",
            PrimaryButtonText = "断开",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close
        };

        if (await ShowDialogAsync(dialog) != ContentDialogResult.Primary) return;

        _rt.AddLog("正在断开本机连接…");

        var outcome = await _deviceActions.RunAsync(ct => _rt.Devices.DisconnectSelfAsync(ct));
        ShowDeviceActionResult(outcome, "本机连接已断开");
        if (outcome == DeviceActionOutcome.Succeeded) _rt.Coordinator.RequestImmediateCheck();
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

        // 验证码面板长在主页的状态卡里。用户这时候如果在别的页，得先把人带回主页，
        // 否则面板在看不见的地方亮起来，登录就卡在那儿了。
        if (_page != NavPage.Home)
        {
            Navigate(NavPage.Home);
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
            if (await ShowDialogAsync(dialog) != ContentDialogResult.Primary || picker.SelectedIndex < 0) return;
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

    private async void OnRefreshMenuClicked(object sender, RoutedEventArgs e) => await OnRefreshClicked();

    private async Task OnRefreshClicked()
    {
        if (!_uiBuilt) return;

        RefreshItem.IsEnabled = false;
        StatusNotice.Visibility = Visibility.Collapsed;

        try
        {
            var message = await _rt.Coordinator.RefreshStatusAsync();
            if (_rt.Exiting || !_uiBuilt) return;
            if (message is null) return;

            StatusNotice.Text = message;
            StatusNotice.Visibility = Visibility.Visible;
        }
        catch (Exception ex)
        {
            if (_rt.Exiting || !_uiBuilt) return;
            StatusNotice.Text = $"刷新失败：{ex.Message}";
            StatusNotice.Visibility = Visibility.Visible;
        }
        finally
        {
            if (_uiBuilt) RefreshItem.IsEnabled = true;
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
        if (!_uiReady) return;
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

        // 巡检间隔：Slider 的值就是唯一真值，钳到合法范围再落盘
        double raw = IntervalSlider.Value;
        s.CheckIntervalSeconds = double.IsNaN(raw)
            ? s.CheckIntervalSeconds
            : Math.Clamp((int)Math.Round(raw), MinIntervalSeconds, MaxIntervalSeconds);

        s.DeviceGuardEnabled = DeviceGuardSwitch.IsOn;

        s.DeviceGuardThreshold = ReadGuardThreshold();

        UpdateGuardProbeSettings();

        bool settingsSaved = _rt.Store.Save();
        bool passwordSaved = _rt.Store.SavePassword(PwdBox.Password);
        if (!settingsSaved || !passwordSaved)
            _rt.AddLog("账号或密码保存失败，重启后可能丢失，请检查本地配置目录的写入权限");

        if (!_rt.IsUiTest && (AutoStartSwitch.IsOn != s.AutoStart
            || AutoStartSwitch.IsOn != StartupManager.IsEnabled()))
            _rt.SetAutoStart(AutoStartSwitch.IsOn);

        UpdateAutoPill();
        RefreshDeviceSummary();
        _rt.ApplyLockPolicy();
        _rt.RefreshTray();
    }

    private void OnClosed(object sender, WindowEventArgs args)
    {
        _edgeDock?.Dispose();
        _deviceActions.Dispose();
        PersistToStore();

        _saveTimer.Stop();
        _uiClock?.Stop();
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

/// <summary>
/// 运行日志里的一条「事件」：已经翻译成人话，重复出现只加计数。
/// 原始日志仍然完整地躺在设置页的「原始调试日志」里。
/// </summary>
public sealed class UiLog : System.ComponentModel.INotifyPropertyChanged
{
    private string _time = "";
    private int _count = 1;

    public DateTime At { get; init; }
    public string Text { get; init; } = "";

    public string Time
    {
        get => _time;
        set
        {
            if (_time == value) return;
            _time = value;
            Raise(nameof(Time));
        }
    }

    public int Count
    {
        get => _count;
        set
        {
            if (_count == value) return;
            _count = value;
            Raise(nameof(Count));
            Raise(nameof(CountText));
        }
    }

    public string CountText => _count > 1 ? $"重复 {_count} 次" : "";

    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;

    private void Raise(string name) =>
        PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(name));
}


