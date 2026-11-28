using System;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using SNNUAutoLogin.Services;

namespace SNNUAutoLogin;

/// <summary>
/// 开发期界面自检（--ui-test）。
/// 校验下拉框与设置项的布局一致性、命中测试是否正确，并逐状态出图。
/// </summary>
public sealed partial class MainWindow
{
    private async Task RunUiSelfTestAsync()
    {
        var sb = new StringBuilder();

        await Task.Delay(1500);
        CheckWindowIcon(sb);
        await CheckDialogAndCaptchaLifetimeAsync(sb);
        await CheckDisplayRestoreAsync(sb);
        await CheckDevicesAndCurrentOperatorAsync(sb);
        CheckRawDiagnosticLog(sb);

        // 设置项都在设置页上，先切过去量尺寸；首页的量测放到后面切回来再做。
        ShowSettingsPage(true, animate: false);
        await Task.Delay(260);
        CheckFormInputBorders();
        await CheckRulesVisibilityAsync(sb);
        await CheckGuardSummarySettingsAsync(sb);

        if (PortalLink.NavigateUri != new Uri("http://202.117.144.205/"))
            throw new InvalidOperationException("认证门户链接应不包含动态端口");
        var formStyle = (Style)Root.Resources["RowInputStyle"];
        var dialogInput = new TextBox { Style = formStyle };
        if (dialogInput.HorizontalAlignment != HorizontalAlignment.Stretch || dialogInput.MinHeight != 32
            || dialogInput.FontSize != AccountBox.FontSize)
            throw new InvalidOperationException("弹窗输入框与设置页样式不一致");
        sb.AppendLine("PASS portal hyperlink and shared dialog input style");

        sb.AppendLine($"uiReady    = {_uiReady}");
        sb.AppendLine($"theme      = dark={_theme.IsDark} accent=#{_theme.Accent.R:X2}{_theme.Accent.G:X2}{_theme.Accent.B:X2}");
        sb.AppendLine($"accountCrd = {RectOf(AccountCard)}");
        sb.AppendLine($"behavior   = {RectOf(BehaviorCard)}");
        sb.AppendLine($"accBox     = {RectOf(AccountBox)}");
        sb.AppendLine($"pwdBox     = {RectOf(PwdBox)}");

        // 高级页：滑杆、设备监测、连接规则（拆页后必须切过去才量得到）
        Navigate(NavPage.Advanced, animate: false);
        await Task.Delay(260);
        sb.AppendLine("--- advanced ---");
        sb.AppendLine($"ivSlider   = {RectOf(IntervalSlider)} val={IntervalSlider.Value}");
        sb.AppendLine($"guardCard  = {RectOf(DeviceGuardCard)}");
        sb.AppendLine($"rulesCard  = {RectOf(RulesCard)}");
        sb.AppendLine($"guardState = {RectOf(DeviceGuardStateText)}");
        sb.AppendLine($"rulesLink  = {RectOf(RulesCurrentLinkText)}");

        Navigate(NavPage.Settings, animate: false);
        await Task.Delay(260);
        sb.AppendLine("--- settings ---");
        sb.AppendLine($"accountCrd = {RectOf(AccountCard)}");
        sb.AppendLine($"behavior   = {RectOf(BehaviorCard)}");

        // 设置项命中测试：开关与行标题不能互相遮挡
        sb.AppendLine($"hit.autoSw = {HitTest(AutoStartSwitch)}");

        sb.AppendLine($"sw.auto    = {RectOf(AutoStartSwitch)} min={AutoStartSwitch.MinWidth} max={AutoStartSwitch.MaxWidth} pad={AutoStartSwitch.Padding}");
        sb.AppendLine($"sw.parts   = {DescribeSwitch(AutoStartSwitch)}");
        sb.AppendLine($"sw.conn    = {RectOf(AutoReconnectSwitch)}");
        sb.AppendLine($"accBox.right= {RightOf(AccountBox):F1}  sw.right= {RightOf(AutoStartSwitch):F1}");

        // 首页：状态卡与日志卡
        ShowSettingsPage(false, animate: false);
        await Task.Delay(260);

        sb.AppendLine("--- home ---");
        sb.AppendLine($"statusCard = {RectOf(StatusCard)}");
        sb.AppendLine($"logCard    = {RectOf(LogCard)}");
        sb.AppendLine($"deviceCard = {RectOf(DeviceCard)}");
        sb.AppendLine($"ringHost   = {RectOf(RingHost)}");
        sb.AppendLine($"hit.connBtn= {HitTest(ConnectBtn)}");

        // 三种关键状态出图
        ApplyState(ConnState.Online, null, animate: false);
        await Task.Delay(400);
        await CaptureAsync();

        ApplyState(ConnState.Offline, null, animate: false);
        await Task.Delay(400);
        await SaveElementAsync(StatusCard, "snnu_state_offline.png");

        ApplyState(ConnState.Failed, null, animate: false);
        await Task.Delay(400);
        await SaveElementAsync(StatusCard, "snnu_state_failed.png");

        // 这两张卡在设置页上，切回去才渲染得出来
        ShowSettingsPage(true, animate: false);
        await Task.Delay(260);
        await SaveElementAsync(AccountCard, "snnu_account.png");
        await SaveElementAsync(BehaviorCard, "snnu_behavior.png");

        SettingsPage.ChangeView(null, SettingsPage.ScrollableHeight, null, true);
        await Task.Delay(250);
        await SaveElementAsync(Root, "snnu_settings.png");

        var ruleDialog = ShowRuleDialogAsync(new SNNUAutoLogin.Services.ConnectionRule
        {
            Id = Guid.NewGuid().ToString("N"), Account = "test-account",
            MatchType = SNNUAutoLogin.Services.ConnectionMatchType.Wired
        });
        await Task.Delay(500);
        if (_shownDialog is not { } dialog) throw new InvalidOperationException("Rule editor did not open");
        if (dialog.ActualTheme != Root.ActualTheme)
            throw new InvalidOperationException("Rule editor theme differs from its window");
        await SaveElementAsync(dialog, "snnu_rule_dialog.png");
        dialog.Hide();
        await ruleDialog;
        sb.AppendLine("PASS rule editor opens with shared form styles");

        ShowSettingsPage(false, animate: false);
        ShowDeviceActionResult(SNNUAutoLogin.Services.DeviceActionOutcome.TimedOut, "");
        await Task.Delay(250);
        if (!OperationNotice.IsOpen || OperationNotice.Severity != InfoBarSeverity.Warning)
            throw new InvalidOperationException("Missing visible device action timeout notice");
        await SaveElementAsync(Root, "snnu_action_timeout.png");
        sb.AppendLine("PASS device timeout is visible without replacing authentication state");

        await CheckNativeDockingAsync(sb);

        // A native close must leave XAML intact until this message stack returns.
        SendMessageForIcon(WinRT.Interop.WindowNative.GetWindowHandle(this), 0x0010, IntPtr.Zero, IntPtr.Zero);
        if (!_closeQueued || !_uiBuilt || _rt.Exiting)
            throw new InvalidOperationException("Native close released the view reentrantly");
        sb.AppendLine("PASS native close defers view teardown after display changes");
        System.IO.File.WriteAllText(
            System.IO.Path.Combine(System.IO.Path.GetTempPath(), "snnu_uitest.txt"),
            sb.ToString(), Encoding.UTF8);

        // The queued close now runs AppRuntime.Exit and drains background operations.
    }

    private async Task CheckDisplayRestoreAsync(StringBuilder report)
    {
        if (_appWindow is null) throw new InvalidOperationException("Missing app window");
        var originalPosition = _appWindow.Position;
        var originalSize = _appWindow.Size;
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        double scale = Math.Max(96, GetDpiForWindow(hwnd)) / 96.0;
        try
        {
            foreach (int width in new[] { 420, 520, 420 })
            {
                _appWindow.Resize(new Windows.Graphics.SizeInt32((int)(width * scale), (int)(680 * scale)));
                await Task.Delay(180);
                UpdateSettingsInputLayout();
                TeardownUi();
                BuildUi();
                await Task.Delay(180);
                UpdateSettingsInputLayout();
                int expectedRow = Root.ActualWidth < 440 ? 1 : 0;
                if (Grid.GetRow(AccountBox) != expectedRow || Grid.GetRow(PasswordInputHost) != expectedRow
                    || Math.Abs(AppTitleBar.Margin.Right - _titleBarInset) > 0.5)
                    throw new InvalidOperationException("Rebuilt view reused the previous tree's layout cache");
            }
            var area = Microsoft.UI.Windowing.DisplayArea.GetFromWindowId(_appWindow.Id,
                Microsoft.UI.Windowing.DisplayAreaFallback.Primary).WorkArea;
            var smaller = new DockRect(area.X, area.Y, Math.Min(600, area.Width), Math.Min(450, area.Height));
            RefreshDisplayLayout(smaller);
            await Task.Delay(180);
            var p = _appWindow.Position;
            var size = _appWindow.Size;
            if (p.X < smaller.X || p.Y < smaller.Y || p.X + size.Width > smaller.Right || p.Y + size.Height > smaller.Bottom)
                throw new InvalidOperationException("Restored window does not fit the reduced display work area");

            // Deliver display/DPI notifications while a view is being released/rebuilt.
            QueueDisplayLayoutRefresh();
            TeardownUi();
            BuildUi();
            await Task.Delay(180);
            if (!_uiBuilt) throw new InvalidOperationException("Display update lost the rebuilt view");
            report.AppendLine("PASS display changes, reduced work area and rebuilt view layout");
        }
        finally
        {
            _appWindow.Resize(originalSize);
            _appWindow.Move(originalPosition);
            RefreshDisplayLayout();
        }
    }

    private async Task CheckDevicesAndCurrentOperatorAsync(StringBuilder report)
    {
        var portal = new UiDevicePortal();
        using var manager = new DeviceManager(EventDispatcher.Inline, portal, ownsClient: false);
        try
        {
            ShowSettingsPage(false, animate: false);
            await RefreshDevicesAsync(manager);
            await Task.Delay(120);
            if (!DeviceCard.IsExpanded || DeviceList.Items.Count != 2 || !DeviceRefreshBtn.IsEnabled
                || DeviceEmptyText.Visibility != Visibility.Collapsed)
                throw new InvalidOperationException("Online devices must be expanded, populated and refreshable");
            manager.Invalidate();
            RenderDeviceList(manager);
            await RefreshDevicesAsync(manager);
            if (DeviceList.Items.Count != 2 || portal.Reads != 2)
                throw new InvalidOperationException("Device list did not reload after invalidation");

            portal.Hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var pending = RefreshDevicesAsync(manager);
            manager.Invalidate();
            var followup = RefreshDevicesAsync(manager);
            var release = portal.Hold;
            portal.Hold = null;
            release.SetResult();
            await Task.WhenAll(pending, followup);
            if (DeviceList.Items.Count != 2 || portal.Reads != 4)
                throw new InvalidOperationException("Invalidation during a request lost its follow-up refresh");

            ApplyState(ConnState.Online, LoginResult.Online("demo-user@telecom", "192.0.2.10", "2026-01-01 00:00:00"), false);
            if (CurrentAccountText.Text != "demo-user" || CurrentOperatorText.Text != "中国电信")
                throw new InvalidOperationException("Current account/operator must follow the portal session");
            if (DeviceCard.TransformToVisual(Root).TransformPoint(new(0, 0)).Y
                >= QuickEditAccountBtn.TransformToVisual(Root).TransformPoint(new(0, 0)).Y)
                throw new InvalidOperationException("Device list must precede the quick settings cards");
            portal.Fail = true;
            await RefreshDevicesAsync(manager);
            if (DeviceErrorRow.Visibility != Visibility.Visible || !DeviceRefreshBtn.IsEnabled)
                throw new InvalidOperationException("Device query errors must remain visible and retryable");
            report.AppendLine("PASS expanded online devices, reload after reconnect and current operator");
        }
        finally { OnDeviceListChanged(); ApplyState(_rt.State, _rt.LastResult, false); }
    }

    private void CheckRawDiagnosticLog(StringBuilder report)
    {
        const string prefix = "RAW-REGRESSION-";
        for (int i = 0; i < 80; i++) _rt.AddDiagnostic(prefix + i);
        _rt.AddDiagnostic("RAW-REPEATED");
        _rt.AddDiagnostic("RAW-REPEATED");
        _rt.AddDiagnostic("System.IO.IOException: fixture failure\n   at Fixture.SendAsync()");
        FillRawLogs();
        var text = GetRawLogText();
        if (!text.Contains(prefix + "0") || !text.Contains(prefix + "79")
            || text.Split("RAW-REPEATED").Length != 3 || !text.Contains("\n   at Fixture.SendAsync()")
            || RawLogs.Count(entry => entry.Text.StartsWith(prefix)) != 80
            || !_rt.RawLogSnapshot[^1].Time.Contains('.'))
            throw new InvalidOperationException("Raw diagnostics were truncated, merged or lost exception details");
        report.AppendLine("PASS raw diagnostics retain more than 40 entries, duplicates, milliseconds and stack traces");
    }

    private sealed class UiDevicePortal : IOnlineDevicePortal
    {
        public int Reads;
        public bool Fail;
        public TaskCompletionSource? Hold;
        public async Task<DeviceListResult> GetOnlineDevicesAsync(int port, System.Threading.CancellationToken ct = default)
        {
            Reads++;
            if (Hold is { } hold) await hold.Task;
            return Fail ? DeviceListResult.Fail("fixture portal error") : DeviceListResult.Ok(
                new[] { new OnlineDevice { DeviceName = "演示电脑", Ip = "192.0.2.10", IsCurrent = true },
                    new OnlineDevice { DeviceName = "演示手机", Ip = "192.0.2.11", SessionId = "fixture" } }, null);
        }
        public Task<bool> DisconnectAsync(int port, System.Threading.CancellationToken ct = default) => Task.FromResult(true);
        public Task<bool> ForceOfflineAsync(string frameUrl, string sessionId, System.Threading.CancellationToken ct = default) => Task.FromResult(true);
        public void Dispose() { }
    }

    private void CheckWindowIcon(StringBuilder report)
    {
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        if (SendMessageForIcon(hwnd, 0x007F, (IntPtr)1, IntPtr.Zero) == IntPtr.Zero
            || SendMessageForIcon(hwnd, 0x007F, IntPtr.Zero, IntPtr.Zero) == IntPtr.Zero)
            throw new InvalidOperationException("Window must have explicit large and small taskbar icons");
        if (SwitchOutletItem.Text != "切换出口运营商")
            throw new InvalidOperationException("Missing explicit outlet switch menu");
        report.AppendLine("PASS explicit taskbar icons and outlet switch menu");
    }

    [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "SendMessageW")]
    private static extern IntPtr SendMessageForIcon(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);

    private async Task CheckDialogAndCaptchaLifetimeAsync(StringBuilder report)
    {
        OnSwitchOutletClicked(SwitchOutletItem, new RoutedEventArgs());
        await Task.Delay(500);
        if (_shownDialog is not { Title: "切换出口" } outletDialog)
            throw new InvalidOperationException("Outlet menu item must open its picker");
        outletDialog.Hide();
        for (int i = 0; i < 100 && _shownDialog is not null; i++) await Task.Delay(25);
        if (_shownDialog is not null) throw new InvalidOperationException("Outlet picker did not close");

        var first = new ContentDialog { XamlRoot = Root.XamlRoot, Title = "回归检查", CloseButtonText = "关闭" };
        var opened = ShowDialogAsync(first);
        await Task.Delay(500);
        var second = new ContentDialog { XamlRoot = Root.XamlRoot, Title = "重复请求", CloseButtonText = "关闭" };
        if (await ShowDialogAsync(second) != ContentDialogResult.None || !ReferenceEquals(_shownDialog, first))
            throw new InvalidOperationException("Concurrent dialogs must be ignored without replacing the active dialog");

        // Hold decoding so teardown/rebuild always happens before the async continuation.
        var release = new TaskCompletionSource<BitmapImage?>();
        var whileHidden = new TaskCompletionSource<BitmapImage?>();
        _rt.Coordinator.InjectCaptchaForTest(LoadSamplePng());
        var hiddenDecode = SyncCaptchaAsync(_ => whileHidden.Task);
        var decoding = SyncCaptchaAsync(_ => release.Task);
        TeardownUi();
        if (await opened != ContentDialogResult.None || _shownDialog is not null || _openDialogs != 0)
            throw new InvalidOperationException("Closing to tray must dismiss its dialog");
        whileHidden.SetResult(null);
        await hiddenDecode;
        BuildUi();
        CaptchaBox.Text = "new-tree-input";
        release.SetResult(null);
        await decoding;
        if (CaptchaBox.Text != "new-tree-input")
            throw new InvalidOperationException("Old captcha decode touched the rebuilt view");

        var older = new TaskCompletionSource<BitmapImage?>();
        var oldDecode = SyncCaptchaAsync(_ => older.Task);
        var newest = new BitmapImage();
        await SyncCaptchaAsync(_ => Task.FromResult<BitmapImage?>(newest));
        CaptchaBox.Text = "latest-image-input";
        older.SetResult(null);
        await oldDecode;
        if (CaptchaBox.Text != "latest-image-input" || !ReferenceEquals(CaptchaPic.Source, newest))
            throw new InvalidOperationException("An older captcha decode replaced the latest challenge");
        report.AppendLine("PASS concurrent dialogs, dialog teardown and late captcha decoding");
    }

    private async Task CheckRulesVisibilityAsync(StringBuilder report)
    {
        var original = _rt.Store.Settings.ConnectionRules.ToList();
        try
        {
            _rt.Store.Settings.ConnectionRules = new();
            RefreshRulesUi();
            CheckPanels(0);
            _rt.Store.Settings.ConnectionRules.Add(new ConnectionRule
            {
                Id = Guid.NewGuid().ToString("N"), Account = "test-wired",
                MatchType = ConnectionMatchType.Wired, Operator = Operator.Campus
            });
            _rt.Store.Settings.ConnectionRules.Add(new ConnectionRule
            {
                Id = Guid.NewGuid().ToString("N"), Account = "test-wireless",
                MatchType = ConnectionMatchType.WirelessAny, Enabled = false
            });
            if (!_rt.Store.Save()) throw new InvalidOperationException("Unable to save rule fixture");
            _rt.Store.Settings.ConnectionRules = new();
            _rt.Store.Load();
            LoadSettingsToUi();
            CheckPanels(2);
            await Task.Delay(100);
            await SaveElementAsync(RulesCard, "snnu_existing_rules.png");
            var duplicate = new ConnectionRule { Id = Guid.NewGuid().ToString("N"), Account = "duplicate", MatchType = ConnectionMatchType.Wired };
            if (ConnectionRuleSet.Validate(duplicate, _rt.Store.Settings.ConnectionRules) is null)
                throw new InvalidOperationException("Existing rule was not read for duplicate validation");
            _rt.Store.Settings.ConnectionRules.RemoveAt(0);
            RefreshRulesUi();
            CheckPanels(1);
            _rt.Store.Settings.ConnectionRules.Clear();
            RefreshRulesUi();
            CheckPanels(0);
            report.AppendLine("PASS stored rules visible on reload, including disabled rules; empty state restored after last deletion");
        }
        finally
        {
            _rt.Store.Settings.ConnectionRules = original;
            _rt.Store.Save();
            RefreshRulesUi();
        }

        void CheckPanels(int count)
        {
            bool populated = count > 0;
            if (RulesList.ItemsSource is not System.Collections.Generic.List<RuleItem> items || items.Count != count
                || RulesEmptyPanel.Visibility != (populated ? Visibility.Collapsed : Visibility.Visible)
                || RulesListPanel.Visibility != (populated ? Visibility.Visible : Visibility.Collapsed)
                || RulesFallbackHint.Visibility != (populated ? Visibility.Visible : Visibility.Collapsed))
                throw new InvalidOperationException("Rule list visibility differs from stored rules");
        }
    }

    private async Task CheckGuardSummarySettingsAsync(StringBuilder report)
    {
        int originalThreshold = _rt.Store.Settings.DeviceGuardThreshold;
        bool originallyEnabled = _rt.Store.Settings.DeviceGuardEnabled;
        DeviceGuardSwitch.IsOn = true;
        DeviceSummary.Text = "stale summary";
        DeviceGuardThresholdBox.Value = 7;
        if (_rt.Store.Settings.DeviceGuardThreshold != 7 || DeviceSummary.Text == "stale summary")
            throw new InvalidOperationException("Changing the guard threshold did not refresh the existing device summary");
        DeviceSummary.Text = "stale summary";
        DeviceGuardThresholdBox.Text = "9";
        if (_rt.Store.Settings.DeviceGuardThreshold != 9 || DeviceSummary.Text == "stale summary")
            throw new InvalidOperationException("Uncommitted threshold text was ignored");
        await Task.Delay(600);
        _rt.Store.Load();
        if (_rt.Store.Settings.DeviceGuardThreshold != 9)
            throw new InvalidOperationException("Typed guard threshold did not survive reloading saved settings");
        DeviceGuardThresholdBox.Text = "";
        if (_rt.Store.Settings.DeviceGuardThreshold != 9)
            throw new InvalidOperationException("An empty edit reset the previous threshold");
        if (DescribeDevices(DeviceListState.Ready, 2, true, 9) != "当前 2 台设备在线 · 守卫阈值 9 台"
            || DescribeDevices(DeviceListState.Ready, 2, false, 9) != "当前 2 台设备在线")
            throw new InvalidOperationException("Online count and guard threshold are ambiguous");
        DeviceGuardThresholdBox.Value = 35;
        if (_rt.Store.Settings.DeviceGuardThreshold != 35)
            throw new InvalidOperationException("UI threshold range differs from stored settings");
        DeviceGuardThresholdBox.Value = originalThreshold;
        DeviceGuardSwitch.IsOn = originallyEnabled;
        PersistToStore();
        _saveTimer.Stop();
        report.AppendLine("PASS guard value, typed text, autosave and summary update without refreshing devices");
    }

    private async Task CheckNativeDockingAsync(StringBuilder report)
    {
        if (_appWindow is null) throw new InvalidOperationException("Missing app window");
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        var original = _appWindow.Position;
        var work = Microsoft.UI.Windowing.DisplayArea.GetFromWindowId(_appWindow.Id,
            Microsoft.UI.Windowing.DisplayAreaFallback.Primary).WorkArea;
        int w = _appWindow.Size.Width, h = _appWindow.Size.Height;
        int hides = 0;
        bool paused = false;
        try
        {
            foreach (int side in new[] { 0, 1, 2 })
            {
                int x = side == 0 ? work.X : side == 1 ? work.X + work.Width - w : work.X + (work.Width - w) / 2;
                int y = side == 2 ? work.Y : work.Y + Math.Min(40, Math.Max(0, work.Height - h));
                _appWindow.Move(new Windows.Graphics.PointInt32(x, y));
                await Task.Delay(200);
                using var dock = new SNNUAutoLogin.Services.WindowEdgeDock(hwnd, DispatcherQueue, () => paused, startTimer: false);
                int outsideX = work.X + work.Width / 2, outsideY = work.Y + work.Height - 1;
                dock.Poll(outsideX, outsideY);
                await Task.Delay(900);
                dock.Poll(outsideX, outsideY);
                if (!dock.IsHidden) { report.AppendLine($"dock#{side}: taskbar or adjacent monitor excluded"); continue; }
                hides++;
                if (hides == 1 && _appWindow.Presenter is Microsoft.UI.Windowing.OverlappedPresenter presenter)
                {
                    presenter.Minimize();
                    await Task.Delay(200);
                    dock.Poll(outsideX, outsideY);
                    if (!dock.IsHidden) throw new InvalidOperationException("Minimize lost the docked restore position");
                    presenter.Restore();
                    await Task.Delay(200);
                    dock.Reveal();
                    if (Math.Abs(_appWindow.Position.X - x) > 16)
                        throw new InvalidOperationException("Minimized dock did not restore on screen");
                    report.AppendLine("PASS minimized dock restores on screen");
                    using var maximizedDock = new SNNUAutoLogin.Services.WindowEdgeDock(hwnd, DispatcherQueue, () => false, startTimer: false);
                    maximizedDock.Poll(outsideX, outsideY);
                    await Task.Delay(900);
                    maximizedDock.Poll(outsideX, outsideY);
                    if (!maximizedDock.IsHidden) throw new InvalidOperationException("Dock did not hide before maximize test");
                    presenter.Maximize();
                    await Task.Delay(200);
                    maximizedDock.Poll(outsideX, outsideY);
                    presenter.Restore();
                    await Task.Delay(200);
                    if (Math.Abs(_appWindow.Position.X - x) > 16)
                        throw new InvalidOperationException("Maximized dock lost its normal restore position");
                    report.AppendLine("PASS maximized dock preserves normal restore position");
                }
                int revealX = side == 0 ? work.X + 1 : side == 1 ? work.X + work.Width - 1 : work.X + work.Width / 2;
                int revealY = side == 2 ? work.Y + 1 : y + 30;
                dock.Poll(revealX, revealY);
                if (dock.IsHidden) throw new InvalidOperationException("Hover did not reveal the window");
                if (Math.Abs(_appWindow.Position.X - x) > 16 || Math.Abs(_appWindow.Position.Y - y) > 16)
                    throw new InvalidOperationException("Window did not return to its docked position");
                paused = true;
                dock.Poll(outsideX, outsideY);
                await Task.Delay(900);
                dock.Poll(outsideX, outsideY);
                if (dock.IsHidden) throw new InvalidOperationException("Window hid while interaction was paused");
                paused = false;
                report.AppendLine($"PASS dock#{side}: collapse, hover reveal, interaction pause");
            }
            if (hides == 0 && Microsoft.UI.Windowing.DisplayArea.FindAll().Count == 1)
                throw new InvalidOperationException("No external screen edge was reachable");
        }
        finally { _appWindow.Move(original); }
    }

    private static double RightOf(FrameworkElement el)
    {
        try
        {
            var p = el.TransformToVisual(null).TransformPoint(
                new Windows.Foundation.Point(el.ActualWidth, 0));
            return p.X;
        }
        catch { return double.NaN; }
    }

    /// <summary>列出 ToggleSwitch 模板内部关键部件的位置与尺寸，用于定位裁切来源。</summary>
    private static string DescribeSwitch(UIElement root)
    {
        var names = new[]
        {
            "SwitchAreaGrid", "OuterBorder", "SwitchKnobBounds",
            "SwitchKnob", "SwitchKnobOn", "SwitchKnobOff",
            "OnContentPresenter", "OffContentPresenter", "HeaderContentPresenter"
        };

        var found = new System.Collections.Generic.List<string>();
        Walk(root, 0);

        return found.Count == 0 ? "none" : string.Join(" | ", found);

        void Walk(UIElement node, int depth)
        {
            if (depth > 6) return;
            int n = VisualTreeHelper.GetChildrenCount(node);
            for (int i = 0; i < n; i++)
            {
                var child = VisualTreeHelper.GetChild(node, i);
                if (child is FrameworkElement fe)
                {
                    string name = fe.Name;
                    if (Array.IndexOf(names, name) >= 0)
                    {
                        var r = fe.TransformToVisual(root).TransformPoint(new Windows.Foundation.Point(0, 0));
                        found.Add($"{name}({r.X:F1},{r.Y:F1} {fe.ActualWidth:F1}x{fe.ActualHeight:F1} vis={fe.Visibility})");
                    }
                }
                if (child is UIElement ue) Walk(ue, depth + 1);
            }
        }
    }

    /// <summary>命中测试：元素中心点最上层是哪个类型，以及是否命中自身。</summary>
    private string HitTest(FrameworkElement el)
    {
        try
        {
            var c = el.TransformToVisual(null).TransformPoint(
                new Windows.Foundation.Point(el.ActualWidth / 2, el.ActualHeight / 2));

            var list = VisualTreeHelper.FindElementsInHostCoordinates(
                new Windows.Foundation.Point(c.X, c.Y), Root);

            var top = list.FirstOrDefault();
            if (top is null) return "none";
            bool owns = false;
            UIElement? cur = top;
            while (cur is not null)
            {
                if (ReferenceEquals(cur, el)) { owns = true; break; }
                cur = VisualTreeHelper.GetParent(cur) as UIElement;
            }

            return $"{top.GetType().Name} {(owns ? "-> own" : "-> OTHER")}";
        }
        catch (Exception ex)
        {
            return "err:" + ex.GetType().Name;
        }
    }

    private static async Task SaveElementAsync(FrameworkElement el, string fileName)
    {
        try
        {
            var rtb = new Microsoft.UI.Xaml.Media.Imaging.RenderTargetBitmap();
            await rtb.RenderAsync(el);

            var pixels = await rtb.GetPixelsAsync();
            var buffer = new byte[pixels.Length];
            using (var reader = Windows.Storage.Streams.DataReader.FromBuffer(pixels))
            {
                reader.ReadBytes(buffer);
            }

            using var mem = new Windows.Storage.Streams.InMemoryRandomAccessStream();
            var encoder = await Windows.Graphics.Imaging.BitmapEncoder.CreateAsync(
                Windows.Graphics.Imaging.BitmapEncoder.PngEncoderId, mem);
            encoder.SetPixelData(
                Windows.Graphics.Imaging.BitmapPixelFormat.Bgra8,
                Windows.Graphics.Imaging.BitmapAlphaMode.Premultiplied,
                (uint)rtb.PixelWidth, (uint)rtb.PixelHeight, 96, 96, buffer);
            await encoder.FlushAsync();

            var size = (uint)mem.Size;
            var png = new byte[size];
            using (var reader = new Windows.Storage.Streams.DataReader(mem.GetInputStreamAt(0)))
            {
                await reader.LoadAsync(size);
                reader.ReadBytes(png);
            }

            System.IO.File.WriteAllBytes(
                System.IO.Path.Combine(System.IO.Path.GetTempPath(), fileName), png);
        }
        catch { }
    }
}
