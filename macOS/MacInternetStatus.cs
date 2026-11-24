using System.Net;
using System.Net.Http;
using System.Text.Json;
using SNNUAutoLogin.Services;

namespace SNNUAutoLogin.Mac;

internal sealed class MacInternetStatus : IDisposable
{
    private readonly HttpClient _http = new(new SocketsHttpHandler { UseProxy = false })
    { Timeout = TimeSpan.FromSeconds(4) };
    private string _ip = "—";
    private long _checked;
    private int _network = -1;
    private Task? _query;
    public string PublicIp => _ip;
    public string? UpdateUrl { get; private set; }
    public string? UpdateTag { get; private set; }

    public void Refresh(int network, ConnState state)
    {
        if (_network != network)
        {
            _network = network;
            _checked = 0;
            _ip = "—";
        }
        if (state != ConnState.Online || _query is { IsCompleted: false }
            || Environment.TickCount64 - _checked < 300_000) return;
        _query = ReadIpAsync(network);
    }

    private async Task ReadIpAsync(int network)
    {
        try
        {
            string value = (await _http.GetStringAsync("https://ipinfo.io/ip")).Trim();
            if (_network == network && IPAddress.TryParse(value, out var address)) _ip = address.ToString();
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException)
        {
            if (_network == network) _ip = "暂不可用";
        }
        finally { if (_network == network) _checked = Environment.TickCount64; }
    }

    public async Task CheckUpdateAsync()
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get,
                "https://api.github.com/repos/mzh2077/SNNUAutoLogin-GUI/releases?per_page=20");
            request.Headers.UserAgent.ParseAdd("SNNUAutoLogin-Mac/1.6.5");
            using var reply = await _http.SendAsync(request);
            reply.EnsureSuccessStatusCode();
            using var json = JsonDocument.Parse(await reply.Content.ReadAsStringAsync());
            var installed = typeof(MacInternetStatus).Assembly.GetName().Version!;
            foreach (var release in json.RootElement.EnumerateArray())
            {
                if (release.GetProperty("draft").GetBoolean() || release.GetProperty("prerelease").GetBoolean()) continue;
                string tag = release.GetProperty("tag_name").GetString() ?? "";
                if (!Version.TryParse(tag.TrimStart('v'), out var version)
                    || new Version(version.Major, version.Minor, Math.Max(0, version.Build)) <=
                       new Version(installed.Major, installed.Minor, installed.Build)) continue;
                if (!release.GetProperty("assets").EnumerateArray().Any(a =>
                    (a.GetProperty("name").GetString() ?? "").Contains("macos-arm64", StringComparison.OrdinalIgnoreCase)))
                    continue;
                UpdateTag = tag;
                UpdateUrl = "https://github.com/mzh2077/SNNUAutoLogin-GUI/releases/tag/" + Uri.EscapeDataString(tag);
                return;
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or JsonException) { }
    }

    public void Dispose() => _http.Dispose();
}
