// 测试替身：不出网、不碰凭据。
// DeviceManager 依赖 IOnlineDevicePortal，这里给它一个可编程的假实现。
namespace Microsoft.UI.Dispatching
{
    public sealed class DispatcherQueue
    {
        public bool HasThreadAccess => true;
        public bool TryEnqueue(Action action) { action(); return true; }
    }
}

namespace SNNUAutoLogin.Services
{
    /// <summary>记录调用并可预设返回值的门户替身。</summary>
    public sealed class FakePortal : IOnlineDevicePortal
    {
        /// <summary>GetOnlineDevicesAsync 要返回的结果。</summary>
        public DeviceListResult DevicesResult { get; set; } = DeviceListResult.Ok(Array.Empty<OnlineDevice>(), null);

        /// <summary>DisconnectAsync 的返回值。</summary>
        public bool DisconnectResult { get; set; } = true;

        /// <summary>ForceOfflineAsync 的返回值。</summary>
        public bool ForceOfflineResult { get; set; } = true;

        /// <summary>调用次数。</summary>
        public int GetCalls { get; private set; }
        public int DisconnectCalls { get; private set; }
        public int ForceOfflineCalls { get; private set; }

        /// <summary>最近一次调用用到的端口。</summary>
        public int LastPort { get; private set; }

        /// <summary>最近一次强制下线用到的会话标识。</summary>
        public string? LastSessionId { get; private set; }

        public Task<DeviceListResult> GetOnlineDevicesAsync(int port, CancellationToken ct = default)
        {
            GetCalls++;
            LastPort = port;
            return Task.FromResult(DevicesResult);
        }

        public Task<bool> ForceOfflineAsync(string frameUrl, string sessionId, CancellationToken ct = default)
        {
            ForceOfflineCalls++;
            LastSessionId = sessionId;
            return Task.FromResult(ForceOfflineResult);
        }

        public Task<bool> DisconnectAsync(int port, CancellationToken ct = default)
        {
            DisconnectCalls++;
            LastPort = port;
            return Task.FromResult(DisconnectResult);
        }

        public void Dispose() { }
    }
}
