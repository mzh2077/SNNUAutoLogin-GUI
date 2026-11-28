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
Console.WriteLine($"{count} diagnostics checks passed.");

sealed class Transport(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => send(request, ct);
}
