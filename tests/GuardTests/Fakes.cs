// 守卫与规则的测试替身：不出网、不读真实网卡、不碰真实凭据。
namespace SNNUAutoLogin.Services
{
    /// <summary>门户端口表（测试只需要它来校验端口合法性）。</summary>
    public sealed class PortalClient
    {
        public static readonly int[] KnownPorts = { 8603, 8601, 8602, 8605, 8609, 8604, 8606, 8607, 8608, 8610, 8600 };

        public static bool IsKnownPort(int port) => Array.IndexOf(KnownPorts, port) >= 0;
    }

    /// <summary>运营商出口（与主程序同名同值）。</summary>
    public enum Operator
    {
        Campus = 0,
        Mobile = 1,
        Unicom = 2,
        Telecom = 3
    }

    /// <summary>可编排的接入方式。</summary>
    public sealed class FakeNetworkAccess : INetworkAccessService
    {
        public NetworkAccessKind Kind { get; set; } = NetworkAccessKind.Wired;
        public string Ssid { get; set; } = "";
        public bool SsidReliable { get; set; } = true;
        public int InvalidateCalls { get; private set; }

        public NetworkAccessInfo GetCurrent() => new()
        {
            Kind = Kind,
            Ssid = Ssid,
            SsidReliable = SsidReliable,
            InterfaceDescription = "Fake",
            At = DateTime.Now
        };

        public void Invalidate() => InvalidateCalls++;
    }

    /// <summary>可编排的探测服务。</summary>
    public sealed class FakeNetworkProbe : INetworkProbe
    {
        public bool Result { get; set; } = true;
        public string Detail { get; set; } = "假探测";
        public int Calls { get; set; }
        public string LastTarget { get; private set; } = "";
        public TimeSpan LastTimeout { get; private set; }

        public Task<ProbeResult> ProbeAsync(string target, TimeSpan timeout, CancellationToken ct)
        {
            Calls++;
            LastTarget = target;
            LastTimeout = timeout;
            return Task.FromResult(new ProbeResult
            {
                Success = Result,
                Detail = Detail,
                LatencyMs = 1,
                Transport = ProbeTransport.Icmp
            });
        }
    }

    /// <summary>可编排的门户：能返回固定设备数，并记录断开用的端口。</summary>
    public sealed class FakePortal : IOnlineDevicePortal
    {
        public int DeviceCount { get; set; } = 0;
        public bool ListSucceeds { get; set; } = true;
        public string ListMessage { get; set; } = "获取失败";
        public bool DisconnectResult { get; set; } = true;
        public int ListPort { get; private set; }
        public int DisconnectPort { get; private set; } = -1;
        public int DisconnectCalls { get; set; }
        public int ForceOfflineCalls { get; private set; }
        public Func<Task>? BeforeList { get; set; }
        public int ListCalls { get; private set; }

        public async Task<DeviceListResult> GetOnlineDevicesAsync(int port, CancellationToken ct = default)
        {
            ListCalls++;
            ListPort = port;
            if (BeforeList is not null) await BeforeList().WaitAsync(ct);
            if (!ListSucceeds) return DeviceListResult.Fail(ListMessage);

            var devices = new List<OnlineDevice>();
            for (int i = 0; i < DeviceCount; i++)
            {
                devices.Add(new OnlineDevice
                {
                    Ip = $"10.0.0.{i + 1}",
                    Mac = $"00-00-00-00-00-{i + 1:00}",
                    SessionId = $"s{i}",
                    LoginTime = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                    Location = "测试"
                });
            }

            return DeviceListResult.Ok(devices, "http://example/frame");
        }

        public Task<bool> ForceOfflineAsync(string frameUrl, string sessionId, CancellationToken ct = default)
        {
            ForceOfflineCalls++;
            return Task.FromResult(true);
        }

        public Task<bool> DisconnectAsync(int port, CancellationToken ct = default)
        {
            DisconnectCalls++;
            DisconnectPort = port;
            return Task.FromResult(DisconnectResult);
        }

        public void Dispose() { }
    }
}
