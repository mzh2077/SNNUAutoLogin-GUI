using System.Collections.Concurrent;
using System.Text.Json;
using SNNUAutoLogin.Services;

namespace SNNUAutoLogin.Linux;

[System.Runtime.Versioning.SupportedOSPlatform("linux")]
internal sealed class LinuxCredentials : ICredentialSource
{
    private const string DefaultKey = "default";
    private static string SettingsPath => Path.Combine(AgentPaths.ConfigDirectory, "settings.json");
    private readonly ConcurrentDictionary<(string Key, string Account), string> _passwords = new();
    private AppSettings _settings = new() { AutoStart = false, AutoReconnect = false };

    public AppSettings Settings => Volatile.Read(ref _settings);
    public string LoadPassword() => LoadRulePassword(DefaultKey);
    public bool HasPassword => LoadPassword().Length != 0;
    public string LoadRulePassword(string ruleId)
    {
        string? account = AccountFor(ruleId, Settings);
        return account is not null && _passwords.TryGetValue((ruleId, account), out var value) ? value : "";
    }

    private static string? AccountFor(string key, AppSettings settings) =>
        key == DefaultKey ? settings.Account : settings.ConnectionRules.FirstOrDefault(r => r.CredentialKey == key)?.Account;

    public async Task LoadAsync(CancellationToken ct)
    {
        if (File.Exists(SettingsPath))
        {
            await using var file = File.OpenRead(SettingsPath);
            var saved = await JsonSerializer.DeserializeAsync<AppSettings>(file, AgentClient.Json, ct)
                ?? throw new IOException("设置文件为空");
            Validate(saved);
            Volatile.Write(ref _settings, saved);
        }
        await LoadMissingPasswordsAsync(ct);
    }

    public async Task LoadMissingPasswordsAsync(CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("DBUS_SESSION_BUS_ADDRESS"))) return;
        var settings = Settings;
        var keys = settings.ConnectionRules.Select(r => r.CredentialKey).Prepend(DefaultKey).Distinct();
        using var lookupBudget = CancellationTokenSource.CreateLinkedTokenSource(ct);
        lookupBudget.CancelAfter(TimeSpan.FromSeconds(7));
        try
        {
            foreach (var key in keys)
            {
                var account = AccountFor(key, settings);
                if (string.IsNullOrEmpty(account) || _passwords.ContainsKey((key, account))) continue;
                var value = await LinuxProcess.RunAsync("secret-tool",
                    ["lookup", "application", "snnuautologin", "profile", key, "account", account], lookupBudget.Token);
                if (!string.IsNullOrEmpty(value)) _passwords.TryAdd((key, account), value);
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { }
    }

    public async Task SaveSettingsAsync(AppSettings settings, CancellationToken ct)
    {
        Validate(settings);
        Directory.CreateDirectory(AgentPaths.ConfigDirectory,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        string temporary = SettingsPath + "." + Guid.NewGuid().ToString("N");
        try
        {
            await using (var file = new FileStream(temporary, new FileStreamOptions
            {
                Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None,
                UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite
            }))
            {
                await JsonSerializer.SerializeAsync(file, settings, AgentClient.Json, ct);
            }
            File.Move(temporary, SettingsPath, overwrite: true);
            Volatile.Write(ref _settings, settings);
        }
        finally { File.Delete(temporary); }
    }

    public async Task<bool> SetPasswordAsync(string key, string password, bool save, CancellationToken ct)
    {
        var account = AccountFor(key, Settings);
        if (string.IsNullOrEmpty(account))
            throw new InvalidOperationException("请先保存该账号或连接规则");
        if (string.IsNullOrEmpty(password)) throw new InvalidOperationException("密码为空");
        _passwords[(key, account)] = password;
        if (!save) return true;
        return await LinuxProcess.RunAsync("secret-tool",
            ["store", "--label=SNNUAutoLogin", "application", "snnuautologin", "profile", key, "account", account],
            ct, password, 15) is not null;
    }

    private static void Validate(AppSettings settings)
    {
        settings.Account = (settings.Account ?? "").Trim();
        if (!Enum.IsDefined(settings.Operator)) throw new InvalidOperationException("运营商出口无效");
        if (settings.CheckIntervalSeconds is < 4 or > 120) throw new InvalidOperationException("巡检间隔应为 4–120 秒");
        if (settings.DeviceGuardThreshold is < 1 or > 50) throw new InvalidOperationException("设备数阈值应为 1–50");
        string target = (settings.DeviceGuardProbeTarget ?? "").Trim();
        if (!NetworkProbeService.IsValidTarget(target)
            || (!System.Net.IPAddress.TryParse(target, out _)
                && (!Uri.TryCreate(target.Contains("://", StringComparison.Ordinal) ? target : "http://" + target,
                        UriKind.Absolute, out var address)
                    || address.Scheme is not ("http" or "https") || address.UserInfo.Length != 0 || address.Host.Length == 0)))
            throw new InvalidOperationException("探测地址无效");
        settings.DeviceGuardProbeTarget = target;
        if (settings.DeviceGuardProbeTimeoutSeconds is < 1 or > 30)
            throw new InvalidOperationException("探测超时应为 1–30 秒");
        if (settings.ConnectionRules is null || settings.ConnectionRules.Count > ConnectionRuleSet.MaxRules)
            throw new InvalidOperationException("连接规则数量无效");
        var accepted = new List<ConnectionRule>();
        foreach (var rule in settings.ConnectionRules)
        {
            if (rule is null || !Guid.TryParse(rule.Id, out _)
                || accepted.Any(other => other.Id == rule.Id))
                throw new InvalidOperationException("连接规则标识缺失或重复");
            rule.PasswordRef = rule.Id;
            var error = ConnectionRuleSet.Validate(rule, accepted);
            if (error is not null) throw new InvalidOperationException(error);
            accepted.Add(rule);
        }
    }
}
