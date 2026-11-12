using System.Net;
using SNNUAutoLogin.Linux;
using SNNUAutoLogin.Services;

int count = 0;
void Check(bool condition, string name)
{
    if (!condition) throw new Exception(name);
    count++;
    Console.WriteLine("PASS " + name);
}

var portalOrigin = new Uri("http://202.117.144.205:8603/snnuportal/login");
Check(!PortalRequestHandler.CanSendForm(new Uri("http://example.invalid/login"), portalOrigin), "external login form blocked");
Check(!PortalRequestHandler.CanSendForm(new Uri("http://202.117.144.205:8605/login"), portalOrigin), "foreign session port blocked");
var seen = new List<string>();
using (var guarded = new HttpClient(new PortalRequestHandler(new FakeTransport(async (request, ct) =>
{
    seen.Add(request.RequestUri!.Host);
    await request.Content!.ReadAsStringAsync(ct);
    var redirect = new HttpResponseMessage(HttpStatusCode.TemporaryRedirect);
    redirect.Headers.Location = new Uri("https://example.invalid/collect");
    return redirect;
}))))
{
    try
    {
        using var body = new StringContent("password=test");
        await guarded.PostAsync(portalOrigin, body);
        throw new Exception("outside redirect was accepted");
    }
    catch (HttpRequestException)
    {
        Check(seen.SequenceEqual(["202.117.144.205"]), "307 cannot forward login body outside portal");
    }
}

var oldPort = new TaskCompletionSource<int?>(TaskCreationOptions.RunContinuationsAsynchronously);
using (var discovery = new PortalPortDiscovery(_ => oldPort.Task))
{
    var old = discovery.DiscoverAsync();
    discovery.Invalidate();
    oldPort.SetResult(8603);
    Check(await old is null && discovery.CurrentPort is null, "old-network port response discarded");
}
Check(LinuxNetworkAccess.Unescape(@"Dorm\:A\\B") == @"Dorm:A\B", "escaped Wi-Fi SSID decoded");

var guardStore = new TestCredentials();
guardStore.Settings.DeviceGuardEnabled = true;
var guardPortal = new DeviceFixture();
using (var guard = new DeviceGuardService(guardStore, guardPortal, new FailedProbe(), () => 8603, () => { }, _ => { }))
{
    var evaluation = guard.EvaluateAsync();
    await guardPortal.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
    guard.NotifyNetworkChanged();
    guard.NotifyNetworkChanged();
    guardPortal.Release.TrySetResult();
    await evaluation.WaitAsync(TimeSpan.FromSeconds(5));
    Check(guardPortal.Disconnects == 1, "coalesced guard disconnects once on current network");
}

var closingPortal = new DeviceFixture();
var manager = new DeviceManager(EventDispatcher.Inline, closingPortal);
var refresh = manager.RefreshAsync();
await closingPortal.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
var closing = manager.DisposeAsync().AsTask();
Check(!closing.IsCompleted && !closingPortal.Disposed, "shutdown waits before disposing portal");
closingPortal.Release.TrySetResult();
await Task.WhenAll(refresh, closing).WaitAsync(TimeSpan.FromSeconds(5));
Check(closingPortal.Disposed && manager.Devices.Count == 0, "shutdown discards an in-flight device response");

var mock = new PortalFixture();
using (var client = new PortalClient(mock.Transport))
{
    Check(await client.DiscoverAssignedPortAsync() == 8603, "assigned port parsed from fixture");
    Check((await client.LoginAsync(8603, "test", "password", Operator.Campus)).NeedCaptcha, "captcha requested");
    var image = await client.FetchCaptchaAsync(8603);
    Check(image?.Bytes.SequenceEqual(PortalFixture.Image) == true, "captcha image received");
    int before = mock.FormRequests;
    var login = await client.LoginAsync(8603, "test", "password", Operator.Campus, "1234");
    Check(login.Success && mock.FormRequests == before, "captcha submission uses the original form");
    await client.GetStatusAsync(8605);
    Check(mock.Ports.Contains(8603) && mock.Ports.Contains(8605), "separate portal transports per port");
}

if (OperatingSystem.IsLinux())
{
    string scratch = Directory.CreateTempSubdirectory("snnu-").FullName;
    string? runtime = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR");
    string? config = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
    string? bus = Environment.GetEnvironmentVariable("DBUS_SESSION_BUS_ADDRESS");
    Environment.SetEnvironmentVariable("XDG_RUNTIME_DIR", scratch);
    Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", scratch);
    Environment.SetEnvironmentVariable("DBUS_SESSION_BUS_ADDRESS", null);

    using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(25));
    try
    {
        var fixture = new PortalFixture();
        await using var agent = new LinuxAgent(false, new PortalClient(fixture.Transport));
        var running = agent.RunAsync(stop.Token);
        try
        {
            var configReply = await AgentClient.SendAsync(new("settings"), stop.Token);
            var settings = configReply.Settings!;
            Check(!settings.AutoStart && !settings.AutoReconnect && !settings.DeviceGuardEnabled, "automation is opt-in");
            settings.Account = "test";
            var save = new AgentRequest("save-settings") { Settings = settings, SettingsVersion = configReply.SettingsVersion };
            Check((await AgentClient.SendAsync(save, stop.Token)).Success, "save settings over Unix socket");
            Check(!(await AgentClient.SendAsync(save, stop.Token)).Success, "stale client settings rejected");
            var password = await AgentClient.SendAsync(new("password") { Password = "test-secret" }, stop.Token);
            Check(password.Success && password.Status?.CanAuthenticate == true, "in-memory password works without keyring");
            var path = Path.Combine(AgentPaths.ConfigDirectory, "settings.json");
            Check(!(await File.ReadAllTextAsync(path, stop.Token)).Contains("test-secret"), "settings contain no password");
            var mode = File.GetUnixFileMode(AgentPaths.SocketPath);
            Check(mode == (UnixFileMode.UserRead | UnixFileMode.UserWrite), "socket is user-only");
            var requested = await AgentClient.SendAsync(new("login"), stop.Token);
            Check(requested.Status?.AwaitingCaptcha == true, "agent reports captcha state");
            var challenge = await AgentClient.SendAsync(new("captcha"), stop.Token);
            Check(challenge.Captcha?.SequenceEqual(PortalFixture.Image) == true, "second client sees the same challenge");
            Check((await AgentClient.SendAsync(new("captcha-submit") { Code = "1234" }, stop.Token)).Success,
                "second client completes the original login");
            Check(fixture.LoginPosts == 1, "shared agent submits login once");
        }
        finally
        {
            await stop.CancelAsync();
            await running.WaitAsync(TimeSpan.FromSeconds(5));
        }
        Check(!File.Exists(AgentPaths.SocketPath), "shutdown removes socket");
    }
    finally
    {
        Environment.SetEnvironmentVariable("XDG_RUNTIME_DIR", runtime);
        Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", config);
        Environment.SetEnvironmentVariable("DBUS_SESSION_BUS_ADDRESS", bus);
        Directory.Delete(scratch, recursive: true);
    }
}
Console.WriteLine($"{count} checks passed.");

sealed class FakeTransport(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => send(request, ct);
}

sealed class TestCredentials : ICredentialSource
{
    public AppSettings Settings { get; } = new();
    public bool HasPassword => false;
    public string LoadPassword() => "";
    public string LoadRulePassword(string ruleId) => "";
}

sealed class FailedProbe : INetworkProbe
{
    public Task<ProbeResult> ProbeAsync(string target, TimeSpan timeout, CancellationToken ct) =>
        Task.FromResult(new ProbeResult { Detail = "offline" });
}

sealed class DeviceFixture : IOnlineDevicePortal
{
    public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public int Disconnects { get; private set; }
    public bool Disposed { get; private set; }
    public async Task<DeviceListResult> GetOnlineDevicesAsync(int port, CancellationToken ct = default)
    {
        Entered.TrySetResult();
        await Release.Task;
        return DeviceListResult.Ok([new(), new(), new()], "http://202.117.144.205/frame");
    }
    public Task<bool> ForceOfflineAsync(string frameUrl, string sessionId, CancellationToken ct = default) => Task.FromResult(true);
    public Task<bool> DisconnectAsync(int port, CancellationToken ct = default)
    {
        Disconnects++;
        return Task.FromResult(true);
    }
    public void Dispose() => Disposed = true;
}

sealed class PortalFixture
{
    public static readonly byte[] Image = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+jP3sAAAAASUVORK5CYII=");
    public HashSet<int> Ports { get; } = [];
    public int FormRequests { get; private set; }
    public int LoginPosts { get; private set; }
    public HttpMessageHandler Transport(int port)
    {
        lock (Ports) Ports.Add(port);
        return new FakeTransport(async (request, ct) =>
        {
            string path = request.RequestUri!.AbsolutePath;
            if (port == 80)
                return Text("<script>window.location='http://202.117.144.205:8603/snnuportal/login.jsp'</script>");
            if (path.Contains("userstatus", StringComparison.Ordinal)) return Text("<html>offline</html>");
            if (path.Contains("checkcode", StringComparison.Ordinal))
            {
                var image = new ByteArrayContent(Image);
                image.Headers.ContentType = new("image/png");
                return new(HttpStatusCode.OK) { Content = image };
            }
            if (request.Method == HttpMethod.Post)
            {
                LoginPosts++;
                string data = await request.Content!.ReadAsStringAsync(ct);
                return Text(data.Contains("checkcode=1234", StringComparison.Ordinal)
                    ? "当前登录账号: test" : "验证码不正确");
            }

            FormRequests++;
            return Text("""
                <form action="/snnuportal/login" method="post">
                  <input type="text" name="account">
                  <input type="password" name="password">
                  <input type="text" name="checkcode">
                  <img src="/snnuportal/checkcode.jsp">
                </form>
                """);
        });
    }

    private static HttpResponseMessage Text(string body)
    {
        System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);
        return new(HttpStatusCode.OK) { Content = new StringContent(body, System.Text.Encoding.GetEncoding("gb18030")) };
    }
}
