using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using SNNUAutoLogin.Services;

namespace SNNUAutoLogin.Mac;

public sealed record AgentRequest(string Command)
{
    public AppSettings? Settings { get; init; }
    public Guid SettingsVersion { get; init; }
    public string? RuleId { get; init; }
    public string? Password { get; init; }
    public bool Save { get; init; }
    public string? Code { get; init; }
    public string? Device { get; init; }
    public Operator? Outlet { get; init; }
}

public sealed record DeviceView(string Id, string Name, string Ip, string Mac, string LoginTime,
    bool IsCurrent, bool CanForceOffline, string Status);

public sealed record AgentStatus(ConnState State, string Detail, string EffectiveRule, NetworkAccessInfo Network,
    int NetworkVersion, int Port, DeviceGuardStatus Guard, bool AwaitingCaptcha, bool CanAuthenticate, string CredentialStatus, string LocalIp = "", string LoginTime = "");

public sealed record AgentReply(bool Success, string Message = "")
{
    public AgentStatus? Status { get; init; }
    public AppSettings? Settings { get; init; }
    public Guid SettingsVersion { get; init; }
    public DeviceView[]? Devices { get; init; }
    public byte[]? Captcha { get; init; }
    public ProbeResult? Probe { get; init; }
}

internal static class AgentClient
{
    public static MacRuntime Runtime { get; set; } = null!;
    public static JsonSerializerOptions Json { get; } = new(JsonSerializerDefaults.Web);
    public static Task<AgentReply> SendAsync(AgentRequest request, CancellationToken ct) => Runtime.SendAsync(request, ct);
}
