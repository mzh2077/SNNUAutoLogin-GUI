using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using SNNUAutoLogin.Services;

namespace SNNUAutoLogin.Linux;

public sealed record AgentRequest(string Command)
{
    public AppSettings? Settings { get; init; }
    public Guid SettingsVersion { get; init; }
    public string? RuleId { get; init; }
    public string? Password { get; init; }
    public bool Save { get; init; }
    public string? Code { get; init; }
    public string? Device { get; init; }
}

public sealed record DeviceView(string Id, string Name, string Ip, string Mac, string LoginTime,
    bool IsCurrent, bool CanForceOffline, string Status);

public sealed record AgentStatus(ConnState State, string Detail, string EffectiveRule, NetworkAccessInfo Network,
    int NetworkVersion, int Port, DeviceGuardStatus Guard, bool AwaitingCaptcha, bool CanAuthenticate, string CredentialStatus);

public sealed record AgentReply(bool Success, string Message = "")
{
    public AgentStatus? Status { get; init; }
    public AppSettings? Settings { get; init; }
    public Guid SettingsVersion { get; init; }
    public DeviceView[]? Devices { get; init; }
    public byte[]? Captcha { get; init; }
    public ProbeResult? Probe { get; init; }
}

public static class AgentPaths
{
    public static string RuntimeDirectory => Path.Combine(
        Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR")
        ?? throw new InvalidOperationException("缺少 XDG_RUNTIME_DIR，请在正常的 Linux 用户会话运行。"),
        "snnuautologin");

    public static string SocketPath => Path.Combine(RuntimeDirectory, "agent.sock");

    public static string ConfigDirectory => Path.Combine(
        Environment.GetEnvironmentVariable("XDG_CONFIG_HOME") ??
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config"),
        "snnuautologin");
}

public static class AgentClient
{
    public static JsonSerializerOptions Json { get; } = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
        MaxDepth = 24
    };

    public static async Task<AgentReply> SendAsync(AgentRequest request, CancellationToken cancellationToken = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(80));
        using var socket = await ConnectAsync(timeout.Token);
        using var stream = new NetworkStream(socket);
        await WriteAsync(stream, request, timeout.Token);
        return await ReadAsync<AgentReply>(stream, timeout.Token);
    }

    private static async Task<Socket> ConnectAsync(CancellationToken ct)
    {
        var address = new UnixDomainSocketEndPoint(AgentPaths.SocketPath);
        for (int attempt = 0; ; attempt++)
        {
            var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            try
            {
                await socket.ConnectAsync(address, ct);
                return socket;
            }
            catch (SocketException ex)
            {
                socket.Dispose();
                if (attempt == 7)
                    throw new InvalidOperationException(
                        "后台进程未运行。请启动 snnuautologin.service 或运行 snnuautologin agent。", ex);
                await Task.Delay(TimeSpan.FromMilliseconds(200), ct);
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        }
    }

    public static async Task WriteAsync<T>(Stream stream, T message, CancellationToken ct)
    {
        var data = JsonSerializer.SerializeToUtf8Bytes(message, Json);
        if (data.Length > 1_048_576) throw new IOException("消息过大");
        var length = new byte[4];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(length, data.Length);
        await stream.WriteAsync(length, ct);
        await stream.WriteAsync(data, ct);
    }

    public static async Task<T> ReadAsync<T>(Stream stream, CancellationToken ct)
    {
        var prefix = new byte[4];
        await stream.ReadExactlyAsync(prefix, ct);
        int length = System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(prefix);
        if (length is <= 0 or > 1_048_576) throw new IOException("消息长度无效");
        var data = new byte[length];
        await stream.ReadExactlyAsync(data, ct);
        return JsonSerializer.Deserialize<T>(data, Json) ?? throw new IOException("消息为空");
    }
}
