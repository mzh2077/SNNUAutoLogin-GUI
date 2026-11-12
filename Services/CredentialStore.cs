using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SNNUAutoLogin.Services;

/// <summary>凭据文件（secrets.json）的内容。全部是 DPAPI 密文的 base64，磁盘上没有明文。</summary>
internal sealed class SecretsFile
{
    /// <summary>结构版本。遇到未知版本只读不改，避免把别人写的数据搞坏。</summary>
    public int SchemaVersion { get; set; } = 1;

    /// <summary>全局默认密码的密文（base64）。</summary>
    public string Global { get; set; } = "";

    /// <summary>规则密码的密文（base64），键是规则 Id。</summary>
    public Dictionary<string, string> Rules { get; set; } = new(StringComparer.Ordinal);
}

/// <summary>
/// 设置与凭据持久化。密码通过 DPAPI（当前用户作用域）加密落盘，不存明文。
///
/// 凭据分两处：
///   - <c>secret.bin</c>：旧版的单份密码，**保留不删**，新版本继续双写它，保证降级回旧版还能登录；
///   - <c>secrets.json</c>：多份密码（全局一份 + 每条规则一份），新版本真正读的是它。
/// </summary>
public sealed class CredentialStore : ICredentialSource
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("SNNU.AutoLogin.v1");

    private readonly string Dir;

    private string SettingsPath => Path.Combine(Dir, "settings.json");
    private string SecretPath => Path.Combine(Dir, "secret.bin");
    private string SecretsPath => Path.Combine(Dir, "secrets.json");

    public CredentialStore() : this(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SNNUAutoLogin")) { }

    internal CredentialStore(string directory) => Dir = directory;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    // 巡检每几秒就要读一次密码，DPAPI 解密 + 读盘没必要重复做，解密结果缓存在内存里
    private string? _pwdCache;
    private bool _pwdLoaded;
    private bool _passwordDirty;
    private string _lastJson = "";

    // 规则密码按 key 缓存：增删改单条只失效那一条
    private readonly Dictionary<string, string> _rulePwdCache = new(StringComparer.Ordinal);
    private readonly object _ruleGate = new();

    /// <summary>加载期间的提示（迁移失败等），由调用方读取后写进日志。</summary>
    public string LastNotice { get; private set; } = "";

    public AppSettings Settings { get; private set; } = new();

    public void Load()
    {
        LastNotice = "";

        try
        {
            if (Directory.Exists(Dir) is false) Directory.CreateDirectory(Dir);
            if (File.Exists(SettingsPath))
            {
                var json = File.ReadAllText(SettingsPath, Encoding.UTF8);
                var s = ParseSettings(json);
                if (s != null) Settings = s;
                NormalizeSettings();
                _lastJson = json;
            }
        }
        catch
        {
            Settings = new AppSettings();
        }

        _pwdCache = null;
        _pwdLoaded = false;
        _passwordDirty = false;
        lock (_ruleGate) _rulePwdCache.Clear();

        // 迁移：secrets.json 不存在且 secret.bin 能解时，把它搬进新文件
        if (MigrateSecrets()) LastNotice = "已把旧版密码迁移到多凭据文件";
    }

    /// <summary>
    /// 解析设置。
    ///
    /// 分两段做：先把 ConnectionRules 这个属性从 JSON 里剥掉，反序列化剩下的设置；
    /// 再逐条解析规则。这样一条规则写坏了（比如端口填了字符串）不会连带把
    /// 账号、密码、开关这些设置一起拖下水。
    /// </summary>
    private AppSettings? ParseSettings(string json)
    {
        AppSettings? settings = null;
        var rules = new List<ConnectionRule>();

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (root.ValueKind == JsonValueKind.Object)
            {
                var rest = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
                foreach (var p in root.EnumerateObject())
                {
                    if (p.NameEquals("ConnectionRules")) continue;
                    rest[p.Name] = p.Value;
                }

                settings = JsonSerializer.Deserialize<AppSettings>(
                    JsonSerializer.Serialize(rest, JsonOpts), JsonOpts);

                if (root.TryGetProperty("ConnectionRules", out var rulesEl)
                    && rulesEl.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in rulesEl.EnumerateArray())
                    {
                        ConnectionRule? rule = null;
                        try
                        {
                            rule = JsonSerializer.Deserialize<ConnectionRule>(item.GetRawText(), JsonOpts);
                        }
                        catch
                        {
                            rule = null;
                        }

                        if (rule is null) continue;

                        // 旧版规则只有 Port、没有 Operator —— 那时规则不覆盖出口，
                        // 一律用全局出口。老配置迁上来必须保持这个语义，
                        // 否则用户升级后出口会被悄悄改成「校园网直连」。
                        if (!item.TryGetProperty("Operator", out _))
                            rule.Operator = settings.Operator;

                        var error = ConnectionRuleSet.Validate(rule, rules);
                        if (error is not null)
                        {
                            LastNotice = $"有一条连接规则未生效：{error}";
                            continue;
                        }

                        rules.Add(rule);
                    }
                }
            }
        }
        catch
        {
            settings = null;
        }

        if (settings is null) return null;

        settings.ConnectionRules = rules;
        return settings;
    }

    /// <summary>把越界的设置值重置回默认值（沿用既有风格：不是钳制，是回落）。</summary>
    private void NormalizeSettings()
    {
        Settings.Account ??= "";
        if (!Enum.IsDefined(Settings.Operator)) Settings.Operator = Operator.Mobile;
        if (Settings.CheckIntervalSeconds is < 4 or > 120) Settings.CheckIntervalSeconds = 8;

        if (Settings.DeviceGuardThreshold is < 1 or > 50) Settings.DeviceGuardThreshold = 2;

        if (!NetworkProbeService.IsValidTarget(Settings.DeviceGuardProbeTarget))
            Settings.DeviceGuardProbeTarget = NetworkProbeService.DefaultTarget;
        else
            Settings.DeviceGuardProbeTarget = Settings.DeviceGuardProbeTarget.Trim();

        // 探测目标换过默认值：老配置里存的还是那个公网 DNS IP 的，
        // 直接当成「没自定义过」迁到新默认上，免得用户以为改设置没生效。
        if (Settings.DeviceGuardProbeTarget == NetworkProbeService.LegacyDefaultTarget)
            Settings.DeviceGuardProbeTarget = NetworkProbeService.DefaultTarget;

        if (Settings.DeviceGuardProbeTimeoutSeconds is < 1 or > 30)
            Settings.DeviceGuardProbeTimeoutSeconds = NetworkProbeService.DefaultTimeoutSeconds;

        if (!PortalClient.IsKnownPort(Settings.DefaultPort)) Settings.DefaultPort = 8603;

        Settings.ConnectionRules ??= new List<ConnectionRule>();
        Settings.ConnectionRules = ConnectionRuleSet.ValidateSet(
            Settings.ConnectionRules, out var dropped);

        foreach (var d in dropped) LastNotice = $"有一条连接规则未生效：{d}";
    }

    public bool Save()
    {
        try
        {
            if (!Directory.Exists(Dir)) Directory.CreateDirectory(Dir);

            var json = JsonSerializer.Serialize(Settings, JsonOpts);
            if (json == _lastJson) return true;   // 内容没变就不落盘

            WriteAtomically(SettingsPath, Encoding.UTF8.GetBytes(json));
            _lastJson = json;
            return true;
        }
        catch { return false; }
    }

    public bool SavePassword(string? password)
    {
        password ??= "";

        // 与缓存一致说明没改过，跳过 DPAPI 加密与写盘（点「登录」时不再重复落盘）
        if (_pwdLoaded && !_passwordDirty && string.Equals(_pwdCache, password, StringComparison.Ordinal)) return true;

        _pwdCache = password;
        _pwdLoaded = true;
        _passwordDirty = true;

        try
        {
            if (!Directory.Exists(Dir)) Directory.CreateDirectory(Dir);

            if (password.Length == 0)
            {
                File.Delete(SecretPath);

                // 多凭据文件里的全局条目也要一起清掉
                var secrets = ReadSecrets();
                if (secrets is not null && !string.IsNullOrEmpty(secrets.Global))
                {
                    secrets.Global = "";
                    WriteSecrets(secrets);
                }

                _passwordDirty = false;
                return true;
            }

            var data = Encoding.UTF8.GetBytes(password);
            try
            {
                var cipher = ProtectedData.Protect(data, Entropy, DataProtectionScope.CurrentUser);

                // 双写：旧文件留给旧版读，新文件给新版读 —— 降级回旧版也能用当前密码
                WriteAtomically(SecretPath, cipher);

                var secrets = ReadSecrets() ?? new SecretsFile();
                secrets.Global = Convert.ToBase64String(cipher);
                WriteSecrets(secrets);

                _passwordDirty = false;
                return true;
            }
            finally { CryptographicOperations.ZeroMemory(data); }
        }
        catch { return false; }
    }

    /// <summary>读取密码（首次解密后缓存，后续调用为纯内存读取）。</summary>
    public string LoadPassword()
    {
        if (_pwdLoaded) return _pwdCache ?? "";

        _pwdLoaded = true;
        _pwdCache = "";

        // 先看多凭据文件
        var secrets = ReadSecrets();
        if (secrets is not null && !string.IsNullOrEmpty(secrets.Global))
        {
            try
            {
                var cipher = Convert.FromBase64String(secrets.Global);
                var data = ProtectedData.Unprotect(cipher, Entropy, DataProtectionScope.CurrentUser);
                _pwdCache = Encoding.UTF8.GetString(data);
                CryptographicOperations.ZeroMemory(data);
                return _pwdCache;
            }
            catch
            {
                _pwdCache = "";
            }
        }

        // 回退：读旧版单份密码
        try
        {
            if (!File.Exists(SecretPath)) return _pwdCache;

            var cipher = File.ReadAllBytes(SecretPath);
            if (cipher.Length == 0) return _pwdCache;

            var data = ProtectedData.Unprotect(cipher, Entropy, DataProtectionScope.CurrentUser);
            _pwdCache = Encoding.UTF8.GetString(data);
            CryptographicOperations.ZeroMemory(data);
        }
        catch
        {
            _pwdCache = "";
        }

        return _pwdCache;
    }

    public bool HasPassword => LoadPassword().Length > 0;

    // ────────────────────────── 规则密码 ──────────────────────────

    /// <summary>读取某条规则的密码。未设置返回空串。</summary>
    public string LoadRulePassword(string ruleId)
    {
        if (string.IsNullOrWhiteSpace(ruleId)) return "";

        lock (_ruleGate)
        {
            if (_rulePwdCache.TryGetValue(ruleId, out var cached)) return cached;
        }

        string result = "";

        var secrets = ReadSecrets();
        if (secrets is not null
            && secrets.Rules.TryGetValue(ruleId, out var b64)
            && !string.IsNullOrEmpty(b64))
        {
            try
            {
                var cipher = Convert.FromBase64String(b64);
                var data = ProtectedData.Unprotect(cipher, Entropy, DataProtectionScope.CurrentUser);
                result = Encoding.UTF8.GetString(data);
                CryptographicOperations.ZeroMemory(data);
            }
            catch
            {
                result = "";
            }
        }

        lock (_ruleGate) _rulePwdCache[ruleId] = result;
        return result;
    }

    /// <summary>写入某条规则的密码。传空串表示删除该条凭据。</summary>
    public bool SaveRulePassword(string ruleId, string? password)
    {
        if (string.IsNullOrWhiteSpace(ruleId)) return false;
        password ??= "";

        try
        {
            if (!Directory.Exists(Dir)) Directory.CreateDirectory(Dir);

            var secrets = ReadSecrets() ?? new SecretsFile();

            if (password.Length == 0)
            {
                secrets.Rules.Remove(ruleId);
            }
            else
            {
                var data = Encoding.UTF8.GetBytes(password);
                try
                {
                    var cipher = ProtectedData.Protect(data, Entropy, DataProtectionScope.CurrentUser);
                    secrets.Rules[ruleId] = Convert.ToBase64String(cipher);
                }
                finally { CryptographicOperations.ZeroMemory(data); }
            }

            if (!WriteSecrets(secrets)) return false;

            lock (_ruleGate) _rulePwdCache.Remove(ruleId);
            return true;
        }
        catch { return false; }
    }

    /// <summary>删除某条规则的凭据条目。失败只记日志，不抛。</summary>
    public bool DeleteRulePassword(string ruleId)
    {
        if (string.IsNullOrWhiteSpace(ruleId)) return false;

        try
        {
            var secrets = ReadSecrets();
            if (secrets is null) return true;
            if (!secrets.Rules.Remove(ruleId)) return true;

            if (!WriteSecrets(secrets)) return false;

            lock (_ruleGate) _rulePwdCache.Remove(ruleId);
            return true;
        }
        catch { return false; }
    }

    /// <summary>该规则是否已设置密码。</summary>
    public bool HasRulePassword(string ruleId) => LoadRulePassword(ruleId).Length > 0;

    // ────────────────────────── 凭据文件 ──────────────────────────

    /// <summary>
    /// 把旧版 secret.bin 里的密码迁移到多凭据文件。
    ///
    /// 幂等：secrets.json 已经存在就直接跳过，重复启动不会重复写、也不会覆盖既有条目。
    /// 失败不阻断启动 —— 这时继续读旧的 secret.bin，全局密码照常可用。
    /// </summary>
    internal bool MigrateSecrets()
    {
        try
        {
            if (File.Exists(SecretsPath)) return false;    // 已迁移过
            if (!File.Exists(SecretPath)) return false;

            var cipher = File.ReadAllBytes(SecretPath);
            if (cipher.Length == 0) return false;

            // 先确认能解开：解不开就别写新文件，保留旧路径回退
            var probe = ProtectedData.Unprotect(cipher, Entropy, DataProtectionScope.CurrentUser);
            CryptographicOperations.ZeroMemory(probe);

            var secrets = new SecretsFile
            {
                SchemaVersion = 1,
                Global = Convert.ToBase64String(cipher)
            };

            return WriteSecrets(secrets);
        }
        catch
        {
            // 迁移失败：回退读 secret.bin，程序照常启动
            LastNotice = "旧版密码迁移失败，已回退读取原文件";
            return false;
        }
    }

    private SecretsFile? ReadSecrets()
    {
        try
        {
            if (!File.Exists(SecretsPath)) return null;

            var json = File.ReadAllText(SecretsPath, Encoding.UTF8);
            var secrets = JsonSerializer.Deserialize<SecretsFile>(json, JsonOpts);
            if (secrets is null) return null;

            // 未知版本只读不改
            if (secrets.SchemaVersion < 1) return null;

            secrets.Rules ??= new Dictionary<string, string>(StringComparer.Ordinal);
            return secrets;
        }
        catch
        {
            return null;
        }
    }

    private bool WriteSecrets(SecretsFile secrets)
    {
        try
        {
            if (!Directory.Exists(Dir)) Directory.CreateDirectory(Dir);
            WriteAtomically(SecretsPath, Encoding.UTF8.GetBytes(JsonSerializer.Serialize(secrets, JsonOpts)));
            return true;
        }
        catch { return false; }
    }

    private static void WriteAtomically(string path, byte[] data)
    {
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllBytes(temporary, data);
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }
}
