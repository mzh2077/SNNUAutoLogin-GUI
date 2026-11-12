using System.Net.Sockets;
using System.Text.Json;
using SNNUAutoLogin.Services;

namespace SNNUAutoLogin.Linux;

[System.Runtime.Versioning.SupportedOSPlatform("linux")]
internal sealed class LinuxAgent : IAsyncDisposable
{
    private readonly LinuxCredentials _credentials = new();
    private readonly LinuxNetworkAccess _network = new();
    private readonly PortalClient _portal;
    private readonly NetworkProbeService _probe = new();
    private readonly PortalPortDiscovery _ports;
    private readonly ConnectionProfileResolver _profiles;
    private readonly LoginCoordinator _login;
    private readonly DeviceManager _devices;
    private readonly DeviceGuardService _guard;
    private readonly Dictionary<string, OnlineDevice> _deviceTokens = new(StringComparer.Ordinal);
    private bool _credentialsLoaded;
    private readonly bool _autoStart;
    private Guid _settingsVersion = Guid.NewGuid();
    private int _networkVersion;

    public LinuxAgent(bool autoStart, PortalClient? portal = null)
    {
        _autoStart = autoStart;
        _portal = portal ?? new PortalClient();
        _ports = new(_portal);
        _profiles = new(_credentials, _network, _ports, allowDefaultRulePassword: false);
        _login = new(_credentials, EventDispatcher.Inline, _profiles, _ports, autoStart, _portal);
        _devices = new(EventDispatcher.Inline, _portal, ownsClient: false);
        _guard = new(_credentials, _portal, _probe, () => _profiles.Resolve().Port,
            RequestGuardReauthentication, _ => { }, CanGuardReauthenticate);
    }

    public async Task RunAsync(CancellationToken ct)
    {
        string directory = AgentPaths.RuntimeDirectory;
        Directory.CreateDirectory(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        if (File.GetAttributes(directory).HasFlag(FileAttributes.ReparsePoint)
            || (File.GetUnixFileMode(directory) & (UnixFileMode.GroupRead | UnixFileMode.GroupWrite
                | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute)) != 0)
            throw new IOException("运行目录必须由当前用户独占");

        using var instance = new FileStream(Path.Combine(directory, "agent.lock"), FileMode.OpenOrCreate,
            FileAccess.ReadWrite, FileShare.None);
        string path = AgentPaths.SocketPath;
        File.Delete(path);
        using var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        listener.Bind(new UnixDomainSocketEndPoint(path));
        try
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            listener.Listen(12);
            await _credentials.LoadAsync(ct);
            await _network.InitializeAsync(ct);
            _credentialsLoaded = true;
            _login.SetStartupLogin(_autoStart && _credentials.Settings.AutoStart);
            _login.Start();
            _guard.Start();
            using var watcherStop = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var watcher = _network.WatchAsync(NetworkChanged, watcherStop.Token);
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    using var connection = await listener.AcceptAsync(ct);
                    await ServeAsync(connection, ct);
                }
            }
            finally
            {
                await watcherStop.CancelAsync();
                await watcher;
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        finally { File.Delete(path); }
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

    private async Task ServeAsync(Socket connection, CancellationToken ct)
    {
        using var stream = new NetworkStream(connection);
        AgentReply response;
        try
        {
            using var readBudget = CancellationTokenSource.CreateLinkedTokenSource(ct);
            readBudget.CancelAfter(TimeSpan.FromSeconds(5));
            var request = await AgentClient.ReadAsync<AgentRequest>(stream, readBudget.Token);
            response = await DispatchAsync(request, ct);
        }
        catch (OperationCanceledException) { return; }
        catch (Exception ex) when (ex is IOException or JsonException or InvalidOperationException or UnauthorizedAccessException)
        {
            response = new(false, ex.Message);
        }
        try
        {
            using var writeBudget = CancellationTokenSource.CreateLinkedTokenSource(ct);
            writeBudget.CancelAfter(TimeSpan.FromSeconds(5));
            await AgentClient.WriteAsync(stream, response, writeBudget.Token);
        }
        catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException) { }
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
                await _credentials.SaveSettingsAsync(settings, ct);
                if (settings.AutoStart != previous.AutoStart
                    && await LinuxProcess.RunAsync("systemctl",
                        ["--user", settings.AutoStart ? "enable" : "disable", "snnuautologin.service"], ct) is null)
                {
                    await _credentials.SaveSettingsAsync(previous, ct);
                    return new(false, "无法更新自启动。请确认已安装 snnuautologin 用户服务。");
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
                return StatusReply(true, saved ? "" : "密钥环不可写，密码仅保留在当前后台进程。");
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
            !_credentialsLoaded ? "正在读取配置" : profile.Password.Length == 0 ? "未配置密码或密钥环不可用／尚未解锁" : "密码可用");
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
        await Task.WhenAll(_guard.DisposeAsync().AsTask(), _login.DisposeAsync().AsTask(), _devices.DisposeAsync().AsTask());
        _ports.Dispose();
        _probe.Dispose();
        _portal.Dispose();
    }
}
