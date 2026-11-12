using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using SNNUAutoLogin.Services;

namespace SNNUAutoLogin.Linux.Desktop;

internal sealed class MainWindow : Window
{
    private readonly CancellationTokenSource _closed = new();
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(3) };
    private readonly TextBlock _status = new() { Text = "正在连接后台…", FontSize = 17, TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _notice = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _guardStatus = new() { TextWrapping = TextWrapping.Wrap };
    private readonly StackPanel _captchaPanel = new() { IsVisible = false, Spacing = 8 };
    private readonly Image _captcha = new() { Height = 55, HorizontalAlignment = HorizontalAlignment.Left };
    private readonly TextBox _code = new() { PlaceholderText = "验证码", Width = 120 };
    private readonly TextBox _account = new() { PlaceholderText = "学号" };
    private readonly TextBox _password = new() { PlaceholderText = "密码", PasswordChar = '●' };
    private readonly CheckBox _savePassword = new() { Content = "保存到系统密钥环" };
    private readonly ComboBox _operator = Choices<Operator>();
    private readonly CheckBox _autoStart = new() { Content = "用户登录后自动启动" };
    private readonly CheckBox _reconnect = new() { Content = "断线自动重连" };
    private readonly TextBox _interval = new() { PlaceholderText = "巡检间隔（秒）" };
    private readonly CheckBox _enableGuard = new() { Content = "启用设备数守卫" };
    private readonly TextBox _threshold = new() { PlaceholderText = "在线设备数阈值" };
    private readonly TextBox _target = new() { PlaceholderText = "探测地址" };
    private readonly TextBox _timeout = new() { PlaceholderText = "探测超时（秒）" };
    private readonly ListBox _rules = new() { MinHeight = 120, MaxHeight = 180 };
    private readonly ComboBox _ruleType = Choices<ConnectionMatchType>();
    private readonly TextBox _ruleSsid = new() { PlaceholderText = "指定 Wi-Fi 名称" };
    private readonly TextBox _ruleAccount = new() { PlaceholderText = "规则账号" };
    private readonly TextBox _rulePassword = new() { PlaceholderText = "规则密码", PasswordChar = '●' };
    private readonly ComboBox _ruleOperator = Choices<Operator>();
    private readonly CheckBox _ruleEnabled = new() { Content = "启用规则", IsChecked = true };
    private readonly ListBox _devices = new() { MinHeight = 130, MaxHeight = 280 };
    private DeviceView[] _deviceRows = [];
    private AppSettings? _settings;
    private Guid _settingsVersion;
    private int _networkVersion;
    private bool _busy;
    private bool _polling;

    public MainWindow()
    {
        Title = "陕师大校园网自动登录";
        Width = 780;
        Height = 660;
        MinWidth = 540;
        MinHeight = 460;

        _captchaPanel.Children.Add(_captcha);
        _captchaPanel.Children.Add(Row(_code, Button("提交验证码", SubmitCaptchaAsync),
            Button("换一张", () => LoadCaptchaAsync(refresh: true))));

        var tabs = new TabControl
        {
            ItemsSource = new[]
            {
                Tab("登录", Form(
                    Label("默认账号", _account), Label("运营商出口", _operator), _password, _savePassword,
                    Row(Button("保存设置", SaveDefaultAsync), Button("立即登录", LoginAsync),
                        Button("重新读取密钥环", () => CommandAsync("unlock"))), _captchaPanel)),
                Tab("连接规则", Form(
                    _rules, Row(Button("新规则", () => { _rules.SelectedIndex = -1; EditRule(); return Task.CompletedTask; }),
                        Button("保存规则", SaveRuleAsync), Button("删除规则", DeleteRuleAsync)),
                    Label("匹配方式", _ruleType), _ruleSsid, _ruleAccount,
                    Label("运营商出口", _ruleOperator), _ruleEnabled, _rulePassword,
                    new TextBlock { Text = "规则密码随「保存到系统密钥环」设置处理。" })),
                Tab("设备与守卫", Form(
                    _devices, Row(Button("刷新列表", () => DeviceCommandAsync("devices")),
                        Button("下线所选设备", OfflineAsync), Button("断开本机", () => DeviceCommandAsync("disconnect"))),
                    _guardStatus, _enableGuard, Label("设备数阈值", _threshold), Label("探测地址", _target),
                    Label("探测超时（秒）", _timeout), Row(Button("保存守卫设置", SaveGuardAsync),
                        Button("立即检测一次", () => CommandAsync("probe"))))),
                Tab("自动化", Form(
                    _autoStart, _reconnect, Label("巡检间隔（秒）", _interval),
                    Row(Button("保存自动化设置", SaveAutomationAsync), Button("重新加载设置", LoadSettingsAsync))))
            }
        };
        var page = new Grid { Margin = new Thickness(20), RowDefinitions = new RowDefinitions("Auto,*,Auto") };
        page.Children.Add(_status);
        Grid.SetRow(tabs, 1);
        page.Children.Add(tabs);
        Grid.SetRow(_notice, 2);
        page.Children.Add(_notice);
        Content = page;

        _rules.SelectionChanged += (_, _) => EditRule();
        _timer.Tick += async (_, _) => await PollAsync();
        Opened += async (_, _) =>
        {
            await RunUiAsync(ConnectAsync);
            _timer.Start();
        };
        Closed += (_, _) =>
        {
            _timer.Stop();
            _closed.Cancel();
            SetCaptchaImage(null);
        };
    }

    private async Task ConnectAsync()
    {
        try
        {
            await LoadSettingsAsync();
        }
        catch (InvalidOperationException)
        {
            var info = new ProcessStartInfo("systemctl") { UseShellExecute = false };
            foreach (string arg in new[] { "--user", "start", "snnuautologin.service" }) info.ArgumentList.Add(arg);
            try
            {
                using var service = Process.Start(info);
                using var limit = CancellationTokenSource.CreateLinkedTokenSource(_closed.Token);
                limit.CancelAfter(TimeSpan.FromSeconds(10));
                if (service is not null) await service.WaitForExitAsync(limit.Token);
            }
            catch (System.ComponentModel.Win32Exception) { }
            await LoadSettingsAsync();
        }
    }

    private async Task PollAsync()
    {
        if (_busy || _polling) return;
        _polling = true;
        try
        {
            var reply = await SendAsync(new("status"));
            ApplyStatus(reply);
            if (reply.Status?.AwaitingCaptcha == true && _captcha.Source is null) await LoadCaptchaAsync();
            if (_settings is null) await LoadSettingsAsync();
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException) { _status.Text = ex.Message; }
        catch (OperationCanceledException) { }
        finally { _polling = false; }
    }

    private async Task LoadSettingsAsync()
    {
        var reply = await SendAsync(new("settings"));
        if (reply.Settings is not { } settings) throw new InvalidOperationException(reply.Message);
        _settings = settings;
        _settingsVersion = reply.SettingsVersion;
        _account.Text = settings.Account;
        _operator.SelectedItem = settings.Operator;
        _autoStart.IsChecked = settings.AutoStart;
        _reconnect.IsChecked = settings.AutoReconnect;
        _interval.Text = settings.CheckIntervalSeconds.ToString();
        _enableGuard.IsChecked = settings.DeviceGuardEnabled;
        _threshold.Text = settings.DeviceGuardThreshold.ToString();
        _target.Text = settings.DeviceGuardProbeTarget;
        _timeout.Text = settings.DeviceGuardProbeTimeoutSeconds.ToString();
        _rules.ItemsSource = settings.ConnectionRules.Select(r => $"{r.MatchType}  {r.Ssid}  {r.Account}").ToArray();
        EditRule();
        ApplyStatus(reply);
    }

    private AppSettings SettingsCopy() => _settings is { } settings
        ? System.Text.Json.JsonSerializer.Deserialize<AppSettings>(
            System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(settings, AgentClient.Json), AgentClient.Json)!
        : throw new InvalidOperationException("请先连接后台并加载设置。");

    private async Task SaveAsync(AppSettings settings)
    {
        var reply = await SendAsync(new("save-settings") { Settings = settings, SettingsVersion = _settingsVersion });
        if (!reply.Success) throw new InvalidOperationException(reply.Message);
        await LoadSettingsAsync();
        _notice.Text = "设置已保存";
    }

    private async Task SaveDefaultAsync()
    {
        var settings = SettingsCopy();
        var secret = _password.Text;
        settings.Account = _account.Text ?? "";
        settings.Operator = _operator.SelectedItem is Operator selected ? selected : Operator.Campus;
        await SaveAsync(settings);
        if (!string.IsNullOrEmpty(secret)) await SavePasswordAsync(null, secret);
        _password.Text = "";
    }

    private async Task LoginAsync()
    {
        if (!string.IsNullOrEmpty(_password.Text) || _account.Text != _settings?.Account || !Equals(_operator.SelectedItem, _settings?.Operator))
            await SaveDefaultAsync();
        await CommandAsync("login");
        if (_captchaPanel.IsVisible) await LoadCaptchaAsync();
    }

    private Task SaveAutomationAsync()
    {
        var settings = SettingsCopy();
        settings.AutoStart = _autoStart.IsChecked == true;
        settings.AutoReconnect = _reconnect.IsChecked == true;
        settings.CheckIntervalSeconds = Number(_interval);
        return SaveAsync(settings);
    }

    private Task SaveGuardAsync()
    {
        var settings = SettingsCopy();
        settings.DeviceGuardEnabled = _enableGuard.IsChecked == true;
        settings.DeviceGuardThreshold = Number(_threshold);
        settings.DeviceGuardProbeTarget = _target.Text ?? "";
        settings.DeviceGuardProbeTimeoutSeconds = Number(_timeout);
        return SaveAsync(settings);
    }

    private void EditRule()
    {
        int index = _rules.SelectedIndex;
        var rule = index >= 0 && _settings?.ConnectionRules.Count > index ? _settings.ConnectionRules[index] : null;
        _ruleType.SelectedItem = rule?.MatchType ?? ConnectionMatchType.Wired;
        _ruleSsid.Text = rule?.Ssid ?? "";
        _ruleAccount.Text = rule?.Account ?? "";
        _ruleOperator.SelectedItem = rule?.Operator ?? Operator.Campus;
        _ruleEnabled.IsChecked = rule?.Enabled ?? true;
        _rulePassword.Text = "";
    }

    private async Task SaveRuleAsync()
    {
        var settings = SettingsCopy();
        int index = _rules.SelectedIndex;
        var secret = _rulePassword.Text;
        var rule = index >= 0 ? settings.ConnectionRules[index] : new ConnectionRule { Id = Guid.NewGuid().ToString("D") };
        rule.MatchType = (ConnectionMatchType)(_ruleType.SelectedItem ?? ConnectionMatchType.Wired);
        rule.Ssid = rule.MatchType == ConnectionMatchType.WirelessSsid ? _ruleSsid.Text ?? "" : "";
        rule.Account = _ruleAccount.Text ?? "";
        rule.Operator = (Operator)(_ruleOperator.SelectedItem ?? Operator.Campus);
        rule.Enabled = _ruleEnabled.IsChecked == true;
        if (index < 0) settings.ConnectionRules.Add(rule);
        await SaveAsync(settings);
        if (!string.IsNullOrEmpty(secret)) await SavePasswordAsync(rule.Id, secret);
    }

    private Task DeleteRuleAsync()
    {
        var settings = SettingsCopy();
        if (_rules.SelectedIndex < 0) throw new InvalidOperationException("请先选择规则");
        settings.ConnectionRules.RemoveAt(_rules.SelectedIndex);
        return SaveAsync(settings);
    }

    private async Task SavePasswordAsync(string? key, string password)
    {
        var reply = await SendAsync(new("password") { RuleId = key, Password = password, Save = _savePassword.IsChecked == true });
        ApplyStatus(reply);
        if (!reply.Success) throw new InvalidOperationException(reply.Message);
        if (reply.Message.Length != 0) _notice.Text = reply.Message;
    }

    private async Task LoadCaptchaAsync(bool refresh = false)
    {
        var reply = await SendAsync(new(refresh ? "captcha-refresh" : "captcha"));
        ApplyStatus(reply);
        SetCaptchaImage(reply.Captcha);
        if (!reply.Success) _notice.Text = reply.Message;
    }

    private async Task SubmitCaptchaAsync()
    {
        var reply = await SendAsync(new("captcha-submit") { Code = _code.Text });
        _code.Text = "";
        ApplyStatus(reply);
        _notice.Text = reply.Message;
        if (_captchaPanel.IsVisible) await LoadCaptchaAsync();
    }

    private void SetCaptchaImage(byte[]? bytes)
    {
        if (_captcha.Source is Bitmap old) old.Dispose();
        using var data = bytes is { Length: > 0 } ? new MemoryStream(bytes) : null;
        _captcha.Source = data is null ? null : new Bitmap(data);
    }

    private async Task OfflineAsync()
    {
        int index = _devices.SelectedIndex;
        if (index < 0 || !_deviceRows[index].CanForceOffline || _deviceRows[index].IsCurrent)
            throw new InvalidOperationException("请先选择可下线的其他设备。");
        await DeviceCommandAsync("device-offline", _deviceRows[index].Id);
    }

    private async Task DeviceCommandAsync(string command, string? id = null)
    {
        var reply = await SendAsync(new(command) { Device = id });
        ApplyStatus(reply);
        _notice.Text = reply.Message;
        _deviceRows = reply.Devices ?? [];
        _devices.ItemsSource = _deviceRows.Select(d =>
            $"{d.Name}  {d.Ip}  {d.Mac}  {d.LoginTime}  {(d.IsCurrent ? "本机" : d.Status)}").ToArray();
    }

    private async Task CommandAsync(string command)
    {
        var reply = await SendAsync(new(command));
        ApplyStatus(reply);
        _notice.Text = reply.Message;
    }

    private void ApplyStatus(AgentReply reply)
    {
        if (reply.Status is not { } status) return;
        if (_networkVersion != status.NetworkVersion)
        {
            _networkVersion = status.NetworkVersion;
            _deviceRows = [];
            _devices.ItemsSource = Array.Empty<string>();
            SetCaptchaImage(null);
        }
        _status.Text = $"{DisplayState(status.State)}  {status.Detail}\n{status.Network.Ssid}  {status.EffectiveRule}  ·  {status.CredentialStatus}";
        _guardStatus.Text = status.Guard.LastMessage;
        _captchaPanel.IsVisible = status.AwaitingCaptcha;
        if (!status.AwaitingCaptcha) SetCaptchaImage(null);
    }

    private static string DisplayState(ConnState state) => state switch
    {
        ConnState.Online => "已连接", ConnState.Offline => "未连接", ConnState.Checking => "正在检查",
        ConnState.Connecting => "正在登录", ConnState.Failed => "登录失败", ConnState.CaptchaRequired => "等待验证码",
        _ => "待命"
    };

    private Task<AgentReply> SendAsync(AgentRequest request) => AgentClient.SendAsync(request, _closed.Token);

    private async Task RunUiAsync(Func<Task> action)
    {
        if (_busy) return;
        _busy = true;
        _notice.Text = "";
        try { await action(); }
        catch (OperationCanceledException) { if (!_closed.IsCancellationRequested) _notice.Text = "操作超时"; }
        catch (Exception ex) { _notice.Text = ex.Message; }
        finally { _busy = false; }
    }

    private Button Button(string label, Func<Task> action)
    {
        var button = new Button { Content = label };
        button.Click += async (_, _) => await RunUiAsync(action);
        return button;
    }

    private static Control Row(params Control[] children) => new WrapPanel().AddAll(children);
    private static Control Form(params Control[] children) => new StackPanel { Spacing = 10, Margin = new Thickness(0, 14) }.AddAll(children);
    private static Control Label(string text, Control field) => Form(new TextBlock { Text = text }, field);
    private static TabItem Tab(string header, Control contents) => new() { Header = header, Content = new ScrollViewer { Content = contents } };
    private static ComboBox Choices<T>() where T : struct, Enum => new() { ItemsSource = Enum.GetValues<T>(), SelectedIndex = 0 };
    private static int Number(TextBox text) => int.TryParse(text.Text, out int n) ? n : throw new InvalidOperationException("请填写有效整数。");
}

internal static class PanelChildren
{
    public static T AddAll<T>(this T panel, IEnumerable<Control> children) where T : Panel
    {
        foreach (var child in children) panel.Children.Add(child);
        return panel;
    }
}
