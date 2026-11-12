using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Text;
using System.Text.Json;
using SNNUAutoLogin.Services;

namespace SNNUAutoLogin.Linux;

internal sealed class LinuxNetworkAccess : INetworkAccessService
{
    private sealed record Route(string Interface = "", string Source = "", string Ssid = "",
        NetworkAccessKind Kind = NetworkAccessKind.Unknown, bool Reliable = false);
    private Route _route = new();

    public NetworkAccessInfo GetCurrent()
    {
        var current = Volatile.Read(ref _route);
        return new NetworkAccessInfo
        {
            InterfaceDescription = current.Interface,
            Ssid = current.Ssid,
            Kind = current.Kind,
            SsidReliable = current.Reliable,
            At = DateTime.Now
        };
    }

    public void Invalidate() => Volatile.Write(ref _route, new Route());

    public async Task InitializeAsync(CancellationToken ct) =>
        Volatile.Write(ref _route, await DetectAsync(ct));

    public async Task WatchAsync(Action changed, CancellationToken ct)
    {
        using var signal = new SemaphoreSlim(0, 1);
        void Signal()
        {
            try { signal.Release(); }
            catch (SemaphoreFullException) { }
        }
        NetworkAddressChangedEventHandler addressChanged = (_, _) => Signal();
        NetworkChange.NetworkAddressChanged += addressChanged;
        using var monitoring = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var monitor = MonitorNetworkManagerAsync(Signal, monitoring.Token);
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var current = await DetectAsync(ct);
                if (current != Interlocked.Exchange(ref _route, current)) changed();
                await signal.WaitAsync(TimeSpan.FromSeconds(3), ct);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        finally
        {
            NetworkChange.NetworkAddressChanged -= addressChanged;
            await monitoring.CancelAsync();
            await monitor;
        }
    }

    private static async Task MonitorNetworkManagerAsync(Action signal, CancellationToken ct)
    {
        try
        {
            using var process = Process.Start(LinuxProcess.StartInfo("nmcli", "monitor"));
            if (process is null) return;
            try
            {
                var errors = process.StandardError.ReadToEndAsync(ct);
                while (await process.StandardOutput.ReadLineAsync(ct) is not null) signal();
                await errors;
            }
            finally
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (System.ComponentModel.Win32Exception) { }
    }

    private static async Task<Route> DetectAsync(CancellationToken ct)
    {
        var routes = await LinuxProcess.RunAsync("ip", ["-j", "route", "get", "202.117.144.205"], ct);
        if (string.IsNullOrEmpty(routes)) return new();
        string device;
        string source;
        try
        {
            using var parsed = JsonDocument.Parse(routes);
            if (parsed.RootElement.GetArrayLength() == 0) return new();
            var entry = parsed.RootElement[0];
            device = entry.TryGetProperty("dev", out var dev) ? dev.GetString() ?? "" : "";
            source = entry.TryGetProperty("prefsrc", out var address) ? address.GetString() ?? "" : "";
        }
        catch (JsonException) { return new(); }
        if (device.Length == 0) return new();

        var state = await LinuxProcess.RunAsync("nmcli", ["-g", "GENERAL.TYPE,GENERAL.STATE", "device", "show", device], ct);
        var lines = (state ?? "").Split('\n', StringSplitOptions.TrimEntries);
        if (lines.Length < 2 || !lines[1].StartsWith("100 ", StringComparison.Ordinal))
            return new(device, source);
        if (lines[0] == "ethernet" && Directory.Exists($"/sys/class/net/{device}/device"))
            return new(device, source, Kind: NetworkAccessKind.Wired);
        if (lines[0] != "wifi") return new(device, source);

        var wifi = await LinuxProcess.RunAsync("nmcli",
            ["-t", "-e", "yes", "-f", "ACTIVE,SSID", "device", "wifi", "list", "ifname", device, "--rescan", "no"], ct);
        var active = (wifi ?? "").Split('\n').FirstOrDefault(line => line.StartsWith("yes:", StringComparison.Ordinal));
        string ssid = active is null ? "" : Unescape(active[4..].TrimEnd('\r'));
        return new(device, source, ssid, NetworkAccessKind.Wireless, ssid.Length != 0);
    }

    internal static string Unescape(string escaped)
    {
        var result = new StringBuilder(escaped.Length);
        for (int i = 0; i < escaped.Length; i++)
        {
            if (escaped[i] == '\\' && i + 1 < escaped.Length && escaped[i + 1] is '\\' or ':') i++;
            result.Append(escaped[i]);
        }
        return result.ToString();
    }
}
