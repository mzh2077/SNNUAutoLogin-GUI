// No real credentials, startup entries or campus network requests in these tests.
namespace Microsoft.UI.Dispatching
{
    public sealed class DispatcherQueue : SNNUAutoLogin.Services.IEventDispatcher
    {
        public bool HasThreadAccess => true;
        public bool TryEnqueue(Action action) { action(); return true; }
    }
}

namespace SNNUAutoLogin.Services
{
    public sealed class AppSettings
    {
        public bool AutoStart { get; set; } = true;
        public bool AutoReconnect { get; set; }
        public string Account { get; set; } = "test";
        public int Operator { get; set; }
        public int CheckIntervalSeconds { get; set; } = 8;

        // 设备数守卫与连接规则相关的新字段
        public bool DeviceGuardEnabled { get; set; }
        public int DeviceGuardThreshold { get; set; } = 2;
        public string DeviceGuardProbeTarget { get; set; } = "223.5.5.5";
        public int DeviceGuardProbeTimeoutSeconds { get; set; } = 3;
        public int DefaultPort { get; set; } = 8603;
        public List<ConnectionRule> ConnectionRules { get; set; } = new();
    }

    /// <summary>规则实体（测试只需要占位，不做校验）。</summary>
    public sealed class ConnectionRule
    {
        public string Id { get; set; } = "";
        public ConnectionMatchType MatchType { get; set; }
        public string Ssid { get; set; } = "";
        public string Account { get; set; } = "";
        public int Operator { get; set; }
        public bool Enabled { get; set; } = true;
        public string PasswordRef { get; set; } = "";
    }

    public enum ConnectionMatchType
    {
        Wired = 0,
        WirelessAny = 1,
        WirelessSsid = 2
    }

    public sealed class CredentialStore : ICredentialSource
    {
        public AppSettings Settings { get; } = new();
        public string Password { get; set; } = "test-password";
        public string LoadPassword() => Password;
        public bool HasPassword => Password.Length > 0;
        public string LoadRulePassword(string ruleId) => Password;
    }

    public sealed class LoginResult
    {
        public bool Success { get; init; }
        public bool AlreadyOnline { get; init; }
        public bool NeedCaptcha { get; init; }
        public string Message { get; init; } = "";
        public string? Account { get; init; }
        public string? Ip { get; init; }
        public static LoginResult Fail(string message) => new() { Message = message };
        public static LoginResult Captcha(string message) => new() { NeedCaptcha = true, Message = message };
    }

    /// <summary>图形验证码图片；测试里只用来占位，不会真的渲染。</summary>
    public sealed record CaptchaImage(byte[] Bytes, string Hint);

    /// <summary>当前生效的登录档案（测试占位）。</summary>
    public sealed record EffectiveProfile
    {
        public string Account { get; init; } = "";
        public string Password { get; init; } = "";
        public int Port { get; init; } = 8603;
        public int Operator { get; init; }
        public ConnectionRule? Rule { get; init; }
        public bool IsDefault { get; init; } = true;
    }

    /// <summary>档案解析器（测试占位）：永远返回默认档案。</summary>
    public interface IConnectionProfileResolver
    {
        EffectiveProfile Resolve();
        void SetDiscoveredPort(int port);
    }

    /// <summary>
    /// 端口发现者（测试占位）：门户按线路分配的接入端口，测试里直接给一个固定值。
    /// </summary>
    public interface IPortalPortDiscovery
    {
        Task<int?> DiscoverAsync(CancellationToken ct = default);
        int? CurrentPort { get; }
        void Invalidate();
    }

    public sealed class PortalClient : IDisposable
    {
        public static LoginResult Status { get; set; } = LoginResult.Fail("offline");
        public static LoginResult Login { get; set; } = new() { Success = true };
        public static int LoginCalls { get; set; }
        public static int LastPort { get; set; }

        public static readonly int[] KnownPorts = { 8603, 8601, 8602, 8605, 8609, 8604, 8606, 8607, 8608, 8610, 8600 };
        public static bool IsKnownPort(int port) => Array.IndexOf(KnownPorts, port) >= 0;

        public Task<LoginResult> GetStatusAsync(int port, CancellationToken ct = default)
        {
            LastPort = port;
            return Task.FromResult(Status);
        }

        public Task<LoginResult> LoginAsync(int port, string account, string password, int op,
            CancellationToken ct = default, bool force = false)
        {
            LoginCalls++;
            LastPort = port;
            return Task.FromResult(Login);
        }

        public Task<LoginResult> LoginAsync(int port, string account, string password, int op, string captcha,
            CancellationToken ct = default, bool force = false)
        {
            LoginCalls++;
            LastPort = port;
            return Task.FromResult(Login);
        }

        public Task<CaptchaImage?> FetchCaptchaAsync(int port, CancellationToken ct = default) =>
            Task.FromResult<CaptchaImage?>(null);

        public void Dispose() { }
    }
}
