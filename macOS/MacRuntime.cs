using System.Text.Json;
using SNNUAutoLogin.Services;

namespace SNNUAutoLogin.Mac;

internal sealed class MacRuntime : IAsyncDisposable
{
    private readonly MacCredentials _credentials = new();
    private readonly MacNetworkAccess _network = new();
    private readonly PortalClient _portal;
    private readonly NetworkProbeService _probe = new();
    private readonly PortalPortDiscovery _ports;
    private readonly ConnectionProfileResolver _profiles;
    private readonly LoginCoordinator _login;
    private readonly DeviceManager _devices;
    private readonly DeviceGuardService _guard;
    private readonly Dictionary<string, OnlineDevice> _deviceTokens = new(StringComparer.Ordinal);
    private bool _credentialsLoaded;
    private Guid _settingsVersion = Guid.NewGuid();
    private int _networkVersion;

    public MacRuntime(PortalClient? portal = null)
    {
        _portal = portal ?? new PortalClient();
        _ports = new(_portal);
        _profiles = new(_credentials, _network, _ports, allowDefaultRulePassword: false);
        _login = new(_credentials, EventDispatcher.Inline, _profiles, _ports, false, _portal);
        _devices = new(EventDispatcher.Inline, _portal, ownsClient: false);
        _guard = new(_credentials, _portal, _probe, () => _profiles.Resolve().Port,
            RequestGuardReauthentication, _ => { }, CanGuardReauthenticate);
    }

    private readonly CancellationTokenSource _stop = new();
    private readonly SemaphoreSlim _requests = new(1, 1);
    private Task _watcher = Task.CompletedTask;
    public Task InitializeAsync() => Task.Run(async () =>
    {
        await _credentials.LoadAsync(_stop.Token);
        await _network.InitializeAsync(_stop.Token);
        _credentialsLoaded = true;
        _login.SetStartupLogin(_credentials.Settings.AutoReconnect && _credentials.HasPassword);
        _login.Start();
        _guard.Start();
        _watcher = _network.WatchAsync(NetworkChanged, _stop.Token);
    });

    public NetworkAccessInfo ReadNetworkForRule()
    {
        MacNative.RequestLocation();
        return _network.GetCurrentWithSsid();
    }

    public async Task<AgentReply> SendAsync(AgentRequest request, CancellationToken ct)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _stop.Token);
        await _requests.WaitAsync(linked.Token);
        try
        {
            var reply = await Task.Run(() => DispatchAsync(request, linked.Token), linked.Token);
            // Keep editor copies separate from the active runtime configuration.
            return JsonSerializer.Deserialize<AgentReply>(JsonSerializer.Serialize(reply, AgentClient.Json), AgentClient.Json)!;
        }
        finally { _requests.Release(); }
    }

    private void NetworkChanged()
    {
        Interlocked.Increment(ref _networkVersion);
        _ports.Invalidate();
        _profiles.SetDiscoveredPort(0);
        _devices.Invalidate();
        _login.InvalidateNetwork();
        _guard.NotifyNetworkChanged();
    }

    private bool CanGuardReauthenticate()
    {
        var profile = _profiles.Resolve();
        return !_login.HasPendingCaptcha && profile.Account.Length != 0 && profile.Password.Length != 0;
    }

    private void RequestGuardReauthentication()
    {
        _devices.Invalidate();
        Interlocked.Increment(ref _networkVersion);
        _login.RequestReauthenticate();
    }

    private async Task<AgentReply> DispatchAsync(AgentRequest request, CancellationToken ct)
    {
        switch (request.Command)
        {
            case "status":
                return StatusReply();
            case "settings":
                return new(true) { Settings = _credentials.Settings, SettingsVersion = _settingsVersion, Status = Status() };
            case "save-settings":
                if (request.SettingsVersion != _settingsVersion)
                    return new(false, "设置已在另一入口修改，请重新加载。");
                var settings = request.Settings ?? throw new InvalidOperationException("设置为空");
                var previous = _credentials.Settings;
                if (settings.AutoStart != previous.AutoStart)
                {
                    int loginItemResult = await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(
                        () => MacNative.SetLoginItem(settings.AutoStart));
                    if (loginItemResult != 0) return new(false, "无法设置登录项，请将应用复制到 Applications 后重试，并检查系统设置中的登录项。");
                }
                try { await _credentials.SaveSettingsAsync(settings, ct); }
                catch
                {
                    if (settings.AutoStart != previous.AutoStart)
                        await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() => MacNative.SetLoginItem(previous.AutoStart));
                    throw;
                }
                _settingsVersion = Guid.NewGuid();
                await _credentials.LoadMissingPasswordsAsync(ct);
                _login.InvalidateNetwork();
                return StatusReply();
            case "unlock":
                await _credentials.LoadMissingPasswordsAsync(ct);
                _login.RequestImmediateCheck();
                return StatusReply();
            case "password":
                bool saved = await _credentials.SetPasswordAsync(request.RuleId ?? "default", request.Password ?? "", request.Save, ct);
                return StatusReply(true, saved ? "" : _credentials.Notice);
            case "refresh":
                return StatusReply(true, await _login.RefreshStatusAsync() ?? "");
            case "reconnect":
                _devices.Invalidate();
                await _login.ManualConnectAsync(reconnect: true);
                return StatusReply(_login.LastResult?.Success == true);
            case "switch-outlet":
                if (request.Outlet is null || !Enum.IsDefined(request.Outlet.Value)) return new(false, "出口无效");
                _devices.Invalidate();
                await _login.ManualConnectAsync(request.Outlet, reconnect: true);
                return StatusReply(_login.LastResult?.Success == true);
            case "login":
                await _login.ManualConnectAsync();
                return StatusReply(_login.LastResult?.Success == true);
            case "captcha":
                var image = _login.HasPendingCaptcha ? _login.PendingCaptcha : null;
                return new(image is not null, image is null ? "当前没有验证码" : "")
                { Captcha = image?.Bytes, Status = Status() };
            case "captcha-refresh":
                await _login.RefreshCaptchaAsync();
                var refreshed = _login.HasPendingCaptcha ? _login.PendingCaptcha : null;
                return new(refreshed is not null, refreshed is null ? "无法刷新验证码" : "")
                { Captcha = refreshed?.Bytes, Status = Status() };
            case "captcha-submit":
                var result = await _login.SubmitCaptchaAsync(request.Code ?? "");
                return StatusReply(result.Success, result.Message);
            case "devices":
                int? port = await _ports.DiscoverAsync(ct);
                await _devices.RefreshAsync(port ?? _profiles.Resolve().Port, ct);
                return DevicesReply();
            case "device-offline":
                if (!_deviceTokens.TryGetValue(request.Device ?? "", out var device) || !device.CanForceOffline || device.IsCurrent)
                    return new(false, "设备列表已过期或该设备为本机，请先刷新列表。");
                bool removed = await _devices.TakeOfflineAsync(device, ct);
                return DevicesReply(removed);
            case "disconnect":
                if (_devices.State != DeviceListState.Ready)
                {
                    int? disconnectPort = await _ports.DiscoverAsync(ct);
                    await _devices.RefreshAsync(disconnectPort ?? _profiles.Resolve().Port, ct);
                }
                bool disconnected = await _devices.DisconnectSelfAsync(ct);
                return DevicesReply(disconnected);
            case "probe":
                var probe = await _guard.ProbeNowAsync();
                return new(probe.Success, probe.Detail) { Probe = probe, Status = Status() };
            case "guard-reconnect":
                if (!CanGuardReauthenticate())
                    return new(false, "请先设置当前账号密码，并完成待处理的验证码。");
                bool reconnected = await _guard.DisposeNowAsync();
                return StatusReply(reconnected);
            default:
                return new(false, "未知命令");
        }
    }

    private AgentStatus Status()
    {
        var profile = _profiles.Resolve();
        return new(_login.State, _login.LastResult?.Message ?? "", profile.Rule?.Ssid is { Length: > 0 } ssid
            ? ssid : profile.Rule?.MatchType.ToString() ?? "默认账号", _network.GetCurrent(), Volatile.Read(ref _networkVersion),
            profile.Port, _guard.Status, _login.HasPendingCaptcha, profile.Password.Length != 0,
            !_credentialsLoaded ? "正在读取配置" : _credentials.Notice.Length > 0 ? _credentials.Notice : profile.Password.Length == 0 ? "请填写密码" : "密码可用", _login.LastResult?.Ip ?? "", _login.LastResult?.LoginTime ?? "");
    }

    private AgentReply StatusReply(bool success = true, string message = "") => new(success, message) { Status = Status() };

    private AgentReply DevicesReply(bool operationSucceeded = true)
    {
        _deviceTokens.Clear();
        var devices = _devices.Devices.Select(device =>
        {
            string token = Guid.NewGuid().ToString("N");
            _deviceTokens.Add(token, device);
            return new DeviceView(token, device.DisplayName, device.Ip, device.Mac, device.LoginTime,
                device.IsCurrent, device.CanForceOffline, device.Status);
        }).ToArray();
        bool ok = _devices.State == DeviceListState.Ready && operationSucceeded;
        string error = ok ? "" : string.IsNullOrEmpty(_devices.Error) ? "操作失败，请刷新设备列表。" : _devices.Error;
        return new(ok, error) { Devices = devices, Status = Status() };
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        await _watcher;
        var servicesStopping = Task.WhenAll(_guard.DisposeAsync().AsTask(),
            _login.DisposeAsync().AsTask(), _devices.DisposeAsync().AsTask());
        await _requests.WaitAsync();
        try
        {
            await servicesStopping;
            _ports.Dispose();
            _probe.Dispose();
            _portal.Dispose();
        }
        finally { _requests.Release(); _requests.Dispose(); _stop.Dispose(); }
    }
}
