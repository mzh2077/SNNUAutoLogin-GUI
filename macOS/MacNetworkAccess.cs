using System.Diagnostics;
using System.Net.NetworkInformation;
using SNNUAutoLogin.Services;

namespace SNNUAutoLogin.Mac;

internal sealed class MacNetworkAccess : INetworkAccessService
{
    private NetworkAccessInfo _current = new();
    private readonly object _gate = new();
    private string _interface = "";
    private string _addresses = "";
    private long _ssidChecked;
    public NetworkAccessInfo GetCurrent() { lock (_gate) return _current; }
    public void Invalidate() { lock (_gate) { _current = new(); _ssidChecked = 0; } }

    public NetworkAccessInfo GetCurrentWithSsid()
    {
        lock (_gate)
        {
            if (_current.Kind != NetworkAccessKind.Wireless || Environment.TickCount64 - _ssidChecked < 15_000)
                return _current;
            string ssid = MacNative.ReadSsid(_interface);
            _ssidChecked = Environment.TickCount64;
            return _current = _current with
            {
                Ssid = ssid, SsidReliable = ssid.Length != 0, SsidAccessDenied = ssid.Length == 0,
                At = DateTime.Now
            };
        }
    }

    public Task InitializeAsync(CancellationToken ct) => RefreshAsync(ct);
    private async Task<bool> RefreshAsync(CancellationToken ct)
    {
        var info = new ProcessStartInfo("/sbin/route") { RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string value in new[] { "-n", "get", "202.117.144.205" }) info.ArgumentList.Add(value);
        using var process = Process.Start(info) ?? throw new IOException("无法读取门户路由");
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
        budget.CancelAfter(TimeSpan.FromSeconds(2));
        string output;
        try
        {
            output = await process.StandardOutput.ReadToEndAsync(budget.Token);
            await process.WaitForExitAsync(budget.Token);
        }
        catch { if (!process.HasExited) process.Kill(); throw; }
        string device = output.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.StartsWith("interface:"))?[10..].Trim() ?? "";
        var nic = NetworkInterface.GetAllNetworkInterfaces().FirstOrDefault(n => n.Name == device);
        string addresses = nic is null ? "" : string.Join(",", nic.GetIPProperties().UnicastAddresses.Select(a => a.Address.ToString()).Order());
        bool wifi = device.Length != 0 && MacNative.IsWifi(device);
        var kind = nic is null || nic.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel
            ? NetworkAccessKind.Unknown : wifi ? NetworkAccessKind.Wireless
            : nic.NetworkInterfaceType == NetworkInterfaceType.Ethernet ? NetworkAccessKind.Wired : NetworkAccessKind.Unknown;
        lock (_gate)
        {
            bool changed = device != _interface || addresses != _addresses || kind != _current.Kind;
            if (!changed) return false;
            _interface = device; _addresses = addresses; _ssidChecked = 0;
            _current = new() { Kind = kind, InterfaceDescription = device, At = DateTime.Now };
            return true;
        }
    }

    public async Task WatchAsync(Action changed, CancellationToken ct)
    {
        int dirty = 0;
        NetworkAddressChangedEventHandler address = (_, _) => Interlocked.Exchange(ref dirty, 1);
        NetworkAvailabilityChangedEventHandler available = (_, _) => Interlocked.Exchange(ref dirty, 1);
        NetworkChange.NetworkAddressChanged += address;
        NetworkChange.NetworkAvailabilityChanged += available;
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(3));
            while (await timer.WaitForNextTickAsync(ct))
            {
                try
                {
                    bool eventRaised = Interlocked.Exchange(ref dirty, 0) != 0;
                    if (eventRaised) Invalidate();
                    if (await RefreshAsync(ct) || eventRaised) changed();
                }
                catch (IOException) { Invalidate(); changed(); }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested) { Invalidate(); changed(); }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        finally
        {
            NetworkChange.NetworkAddressChanged -= address;
            NetworkChange.NetworkAvailabilityChanged -= available;
        }
    }
}
