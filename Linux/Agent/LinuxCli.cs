using System.Text.Json;
using SNNUAutoLogin.Services;

namespace SNNUAutoLogin.Linux;

[System.Runtime.Versioning.SupportedOSPlatform("linux")]
internal static class LinuxCli
{
    public static async Task<int> RunAsync(string[] arguments, CancellationToken ct)
    {
        bool json = arguments.Contains("--json");
        var args = arguments.Where(argument => argument != "--json").ToArray();
        if (args.Length == 0 || args[0] is "help" or "--help")
        {
            Console.WriteLine("""
                snnuautologin status [--json]
                snnuautologin login | unlock | disconnect | devices | probe | guard-reconnect
                snnuautologin password [rule-id] [--stdin] [--save]
                snnuautologin captcha save FILE | refresh FILE | submit
                snnuautologin device-offline DEVICE-ID
                snnuautologin config show | set KEY VALUE
                snnuautologin rule list | add TYPE ACCOUNT OPERATOR [SSID] | remove RULE-ID
                snnuautologin service start | stop | status
                snnuautologin agent
                """);
            return 0;
        }

        string command = args[0];
        AgentReply reply;
        switch (command)
        {
            case "service":
                if (args.Length != 2 || args[1] is not ("start" or "stop" or "status"))
                    throw new ArgumentException("用法：service start|stop|status");
                string? output = await LinuxProcess.RunAsync("systemctl",
                    ["--user", args[1], "snnuautologin.service"], ct);
                if (output is null)
                {
                    Console.Error.WriteLine("无法操作用户服务。请检查服务是否已安装，或手动运行 snnuautologin agent。");
                    return 2;
                }
                if (output.Length != 0) Console.WriteLine(output);
                return 0;
            case "password":
                var ids = args.Skip(1).Where(a => a is not ("--stdin" or "--save")).ToArray();
                if (ids.Length > 1) throw new ArgumentException("用法：password [rule-id] [--stdin] [--save]");
                reply = await AgentClient.SendAsync(new("password")
                {
                    RuleId = ids.FirstOrDefault(), Password = ReadPassword(args.Contains("--stdin")),
                    Save = args.Contains("--save")
                }, ct);
                break;
            case "captcha":
                reply = await CaptchaAsync(args, ct);
                break;
            case "device-offline":
                if (args.Length != 2) throw new ArgumentException("用法：device-offline DEVICE-ID");
                reply = await AgentClient.SendAsync(new(command) { Device = args[1] }, ct);
                break;
            case "config":
            case "rule":
                reply = await SettingsAsync(args, ct);
                break;
            case "status":
            case "login":
            case "unlock":
            case "disconnect":
            case "devices":
            case "probe":
            case "guard-reconnect":
                if (args.Length != 1) throw new ArgumentException("此命令没有位置参数");
                reply = await AgentClient.SendAsync(new(command), ct);
                break;
            default:
                throw new ArgumentException("未知命令，运行 snnuautologin --help 查看用法。");
        }
        Print(reply, json);
        return reply.Success ? 0 : reply.Status?.AwaitingCaptcha == true ? 4 : 3;
    }

    private static async Task<AgentReply> CaptchaAsync(string[] args, CancellationToken ct)
    {
        if (args.Length == 2 && args[1] == "submit")
        {
            if (!Console.IsInputRedirected) Console.Error.Write("验证码：");
            return await AgentClient.SendAsync(new("captcha-submit") { Code = Console.ReadLine() }, ct);
        }

        if (args.Length != 3 || args[1] is not ("save" or "refresh"))
            throw new ArgumentException("用法：captcha save FILE | refresh FILE | submit");
        var reply = await AgentClient.SendAsync(new(args[1] == "save" ? "captcha" : "captcha-refresh"), ct);
        if (reply.Captcha is not { Length: > 0 } image) return reply;

        await using var file = new FileStream(args[2], new FileStreamOptions
        {
            Access = FileAccess.Write, Mode = FileMode.CreateNew, Share = FileShare.None,
            UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite
        });
        await file.WriteAsync(image, ct);
        return reply with { Captcha = null, Message = $"验证码已保存到 {args[2]}" };
    }

    private static async Task<AgentReply> SettingsAsync(string[] args, CancellationToken ct)
    {
        var reply = await AgentClient.SendAsync(new("settings"), ct);
        if (!reply.Success || reply.Settings is not { } settings) return reply;
        if (args.Length == 2 && (args[0], args[1]) is ("config", "show") or ("rule", "list"))
            return reply;

        if (args.Length == 4 && args[0] == "config" && args[1] == "set")
        {
            string value = args[3];
            switch (args[2])
            {
                case "account": settings.Account = value; break;
                case "operator": settings.Operator = ParseOperator(value); break;
                case "autostart": settings.AutoStart = ParseBool(value); break;
                case "reconnect": settings.AutoReconnect = ParseBool(value); break;
                case "interval": settings.CheckIntervalSeconds = ParseNumber(value); break;
                case "guard": settings.DeviceGuardEnabled = ParseBool(value); break;
                case "guard-threshold": settings.DeviceGuardThreshold = ParseNumber(value); break;
                case "probe-target": settings.DeviceGuardProbeTarget = value; break;
                case "probe-timeout": settings.DeviceGuardProbeTimeoutSeconds = ParseNumber(value); break;
                default: throw new ArgumentException("未知配置项，请查看 Linux 使用说明。");
            }
        }
        else if (args.Length is 5 or 6 && args[0] == "rule" && args[1] == "add")
        {
            var kind = args[2] switch
            {
                "wired" => ConnectionMatchType.Wired, "wifi" => ConnectionMatchType.WirelessAny,
                "ssid" => ConnectionMatchType.WirelessSsid, _ => throw new ArgumentException("规则类型应为 wired、wifi 或 ssid")
            };
            if ((kind == ConnectionMatchType.WirelessSsid) != (args.Length == 6))
                throw new ArgumentException("仅 ssid 规则需要提供 Wi-Fi 名称");
            settings.ConnectionRules.Add(new ConnectionRule
            {
                Id = Guid.NewGuid().ToString("D"), Account = args[3], Operator = ParseOperator(args[4]),
                MatchType = kind, Ssid = args.ElementAtOrDefault(5) ?? ""
            });
        }
        else if (args.Length == 3 && args[0] == "rule" && args[1] == "remove")
        {
            if (settings.ConnectionRules.RemoveAll(rule => rule.Id == args[2]) == 0)
                throw new ArgumentException("找不到连接规则");
        }
        else throw new ArgumentException("参数无效，请运行 snnuautologin --help。");

        return await AgentClient.SendAsync(new("save-settings") { Settings = settings, SettingsVersion = reply.SettingsVersion }, ct);
    }

    private static Operator ParseOperator(string value) =>
        Enum.TryParse<Operator>(value, true, out var parsed) && Enum.IsDefined(parsed)
            ? parsed : throw new ArgumentException("出口应为 campus、mobile、unicom 或 telecom");

    private static bool ParseBool(string value) =>
        bool.TryParse(value, out var parsed) ? parsed : throw new ArgumentException("开关应为 true 或 false");

    private static int ParseNumber(string value) =>
        int.TryParse(value, out var parsed) ? parsed : throw new ArgumentException("请输入整数");

    private static string ReadPassword(bool stdin)
    {
        if (stdin) return Console.ReadLine() ?? "";
        if (Console.IsInputRedirected) throw new ArgumentException("重定向输入时请指定 --stdin");
        Console.Error.Write("密码：");
        var buffer = new System.Text.StringBuilder();
        while (true)
        {
            var key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter) break;
            if (key.Key == ConsoleKey.Backspace)
            {
                if (buffer.Length > 0) buffer.Length--;
            }
            else if (!char.IsControl(key.KeyChar)) buffer.Append(key.KeyChar);
        }
        Console.Error.WriteLine();
        return buffer.ToString();
    }

    private static void Print(AgentReply reply, bool json)
    {
        if (json || reply.Settings is not null)
        {
            Console.WriteLine(JsonSerializer.Serialize(reply, AgentClient.Json));
            return;
        }
        if (reply.Message.Length != 0)
            (reply.Success ? Console.Out : Console.Error).WriteLine(reply.Message);
        if (reply.Status is { } status)
        {
            Console.WriteLine($"{status.State}  {status.Detail}  {status.EffectiveRule}  端口 {status.Port}");
            Console.WriteLine($"{status.Network.Kind} {status.Network.Ssid}  {status.CredentialStatus}");
            Console.WriteLine($"守卫：{status.Guard.LastMessage}");
            if (status.AwaitingCaptcha) Console.WriteLine("需要验证码，请运行 captcha save FILE 和 captcha submit。");
        }
        if (reply.Devices is not null)
        {
            foreach (var device in reply.Devices)
                Console.WriteLine($"{device.Id}  {device.Name}  {device.Ip}  {device.Mac}  {device.LoginTime}  {device.Status}{(device.IsCurrent ? "（本机）" : "")}");
        }
    }
}
