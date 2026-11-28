using System.Net;
using SNNUAutoLogin.Services;

int count = 0;
void Check(bool ok, string name)
{
    if (!ok) throw new Exception(name);
    count++;
    Console.WriteLine("PASS " + name);
}

var lines = new List<string>();
using (var http = new HttpClient(new PortalRequestHandler(new Transport((_, _) =>
    Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("fixture") })), lines.Add)))
{
    using var request = new HttpRequestMessage(HttpMethod.Post,
        "http://202.117.144.205:8603/snnuportal/login?account=private-user&token=private-token");
    request.Content = new StringContent("password=private-password");
    request.Headers.Add("Cookie", "session=private-cookie");
    request.Headers.TryAddWithoutValidation("Authorization", "Bearer private-authorization");
    using var response = await http.SendAsync(request);
    Check(response.IsSuccessStatusCode, "diagnostics preserve the response");
}
string all = string.Join('\n', lines);
Check(all.Contains("HTTP -> POST http://202.117.144.205:8603/snnuportal/login")
    && all.Contains("HTTP <- 200") && all.Contains("ms content-type=text/plain"),
    "raw trace includes method, path, response status, duration and content type");
Check(!all.Contains("private-") && !all.Contains("password=") && !all.Contains("Cookie"),
    "query values, body, cookies and authorization are excluded");

lines.Clear();
using (var http = new HttpClient(new PortalRequestHandler(new Transport((_, _) =>
    throw new IOException("fixture transport failure")), lines.Add)))
{
    try { await http.GetAsync("http://202.117.144.205:8603/snnuportal/userstatus.jsp"); }
    catch (IOException) { }
}
all = string.Join('\n', lines);
Check(all.Contains("HTTP !!") && all.Contains("System.IO.IOException: fixture transport failure")
    && all.Contains(" at "), "transport failures retain exception type and stack trace");

using (var http = new HttpClient(new PortalRequestHandler(new Transport((_, _) =>
    Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK))), _ => throw new Exception("logger failure"))))
{
    using var response = await http.GetAsync("http://202.117.144.205:8603/snnuportal/login");
    Check(response.IsSuccessStatusCode, "a diagnostic observer cannot break authentication");
}

using (var client = new PortalClient(_ => new Transport((_, _) =>
    Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("未认证") }))))
{
    lines.Clear();
    client.Diagnostics += lines.Add;
    await client.GetStatusAsync(8605);
    Check(lines.Any(line => line.Contains(":8605/snnuportal/userstatus.jsp")),
        "PortalClient emits the actual selected port to diagnostics");
}
var fixture = new SessionFixture();
using (var shared = new PortalClient(fixture.CreateTransport))
{
    await using (var login = new LoginCoordinator(new Credentials(), EventDispatcher.Inline, null, null, client: shared))
    {
        await login.ManualConnectAsync();
        Check(login.State == ConnState.Online, "fixture login establishes an authenticated session: " + login.LastResult?.Message);
        using var devices = new DeviceManager(EventDispatcher.Inline, shared, ownsClient: false);
        await devices.RefreshAsync(8603);
        Check(devices.State == DeviceListState.Ready && fixture.FrameRequests == 1,
            "device query reuses the login session and resolves a relative iframe");
        Check(fixture.LastFrameQuery == "?first=1&second=2", "HTML query separators are decoded");
        fixture.FrameStatus = HttpStatusCode.Forbidden;
        await devices.RefreshAsync(8603);
        Check(devices.State == DeviceListState.Failed && devices.Error.Contains("403"),
            "a rejected frame is reported as failure rather than an empty successful list");
        fixture.StatusCode = HttpStatusCode.ServiceUnavailable;
        await devices.RefreshAsync(8603);
        Check(devices.State == DeviceListState.Failed && devices.Error.Contains("503"),
            "status-page errors retain the HTTP code");
    }
    fixture.StatusCode = HttpStatusCode.OK;
    fixture.FrameStatus = HttpStatusCode.OK;
    Check((await shared.GetOnlineDevicesAsync(8603)).Success, "disposing the coordinator does not dispose the shared client");
}
using (var separate = new PortalClient(fixture.CreateTransport))
    Check(!(await separate.GetOnlineDevicesAsync(8603)).Success,
        "regression fixture rejects a second unauthenticated client");
Console.WriteLine($"{count} diagnostics checks passed.");

sealed class Credentials : ICredentialSource
{
    public AppSettings Settings { get; } = new() { Account = "fixture-user" };
    public string LoadPassword() => "fixture-password";
    public string LoadRulePassword(string id) => "";
    public bool HasPassword => true;
}

sealed class SessionFixture
{
    public int FrameRequests;
    public string LastFrameQuery = "";
    public HttpStatusCode StatusCode = HttpStatusCode.OK;
    public HttpStatusCode FrameStatus = HttpStatusCode.OK;
    public HttpMessageHandler CreateTransport(int port)
    {
        bool authenticated = false;
        return new Transport((request, _) =>
        {
            string path = request.RequestUri!.AbsolutePath;
            HttpResponseMessage Text(string html, HttpStatusCode code) => new(code)
                { Content = new StringContent(html, System.Text.Encoding.UTF8) };
            if (request.Method == HttpMethod.Post)
            {
                authenticated = true;
                return Task.FromResult(Text("当前登录账号: fixture-user", HttpStatusCode.OK));
            }
            if (path.Contains("auth_my_state"))
            {
                FrameRequests++;
                LastFrameQuery = request.RequestUri.Query;
                return Task.FromResult(Text("<html>empty authenticated device table</html>", FrameStatus));
            }
            if (path.Contains("userstatus"))
                return Task.FromResult(Text(authenticated
                    ? "当前登录账号: fixture-user <iframe src='../zili/auth_my_state.php?first=1&amp;second=2'></iframe>"
                    : "未认证", StatusCode));
            return Task.FromResult(Text("<form action='/snnuportal/login'><input name='account'><input type='password' name='password'></form>", HttpStatusCode.OK));
        });
    }
}

sealed class Transport(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => send(request, ct);
}
