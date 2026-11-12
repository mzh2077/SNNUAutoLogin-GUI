using System.Text;
using SNNUAutoLogin.Services;

var root = Path.Combine(AppContext.BaseDirectory, "SNNU-CredentialTests-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
int checks = 0;
void Check(bool condition, string name)
{
    if (!condition) throw new Exception(name);
    checks++;
    Console.WriteLine("PASS " + name);
}
try
{
    var store = new CredentialStore(root);
    store.Load();
    store.Settings.Account = "test-account";
    Check(store.Save(), "settings saved");
    Check(store.SavePassword("test-password"), "DPAPI password saved");
    var reloaded = new CredentialStore(root);
    reloaded.Load();
    Check(reloaded.Settings.Account == "test-account", "account restored");
    Check(reloaded.LoadPassword() == "test-password", "password restored after restart");

    string secret = Path.Combine(root, "secret.bin");
    File.Delete(secret);
    Directory.CreateDirectory(secret); // deterministic write failure
    Check(!store.SavePassword("new-password"), "password write failure reported");
    Directory.Delete(secret);
    Check(store.SavePassword("new-password"), "same password retried after failure");
    reloaded.Load();
    Check(reloaded.LoadPassword() == "new-password", "retry survives restart");

    File.Delete(secret);
    Directory.CreateDirectory(secret);
    Check(!store.SavePassword(""), "password deletion failure reported");
    Directory.Delete(secret);
    Check(store.SavePassword(""), "password deletion retry succeeds");

    string settings = Path.Combine(root, "settings.json");
    File.Delete(settings);
    Directory.CreateDirectory(settings);
    store.Settings.Account = "updated";
    Check(!store.Save(), "settings failure reported");
    Directory.Delete(settings);
    Check(store.Save(), "unchanged settings retry succeeds");

    File.WriteAllText(settings, "{\"Account\":null,\"Operator\":999,\"CheckIntervalSeconds\":-1}");
    reloaded.Load();
    Check(reloaded.Settings.Account == "", "null account normalized");
    Check(reloaded.Settings.Operator == Operator.Mobile, "invalid operator normalized");
    Check(reloaded.Settings.CheckIntervalSeconds == 8, "invalid interval normalized");
    File.WriteAllText(settings, "{broken");
    reloaded.Load();
    Check(reloaded.Settings.Account == "", "corrupt settings recover defaults");

    // ── 多凭据与兼容迁移 ──
    string root2 = Path.Combine(AppContext.BaseDirectory, "SNNU-CredentialTests2-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(root2);
    try
    {
        var legacy = new CredentialStore(root2);
        legacy.Load();
        legacy.Settings.Account = "old-account";
        legacy.Save();
        legacy.SavePassword("old-password");

        string secretBin = Path.Combine(root2, "secret.bin");
        string secretsJson = Path.Combine(root2, "secrets.json");
        Check(File.Exists(secretBin), "旧版 secret.bin 已生成");
        Check(File.Exists(secretsJson), "多凭据文件已生成");

        string secretsText = File.ReadAllText(secretsJson, Encoding.UTF8);
        Check(!secretsText.Contains("old-password"), "多凭据文件里没有明文密码");
        Check(!secretsText.Contains("old-account"), "多凭据文件里没有明文账号");

        // 幂等：连跑 5 次，内容不变
        string firstContent = secretsText;
        for (int i = 0; i < 5; i++)
        {
            var again = new CredentialStore(root2);
            again.Load();
        }
        Check(File.ReadAllText(secretsJson, Encoding.UTF8) == firstContent, "重复启动不改动多凭据文件");

        var migrated = new CredentialStore(root2);
        migrated.Load();
        Check(migrated.LoadPassword() == "old-password", "迁移后旧密码仍可用");
        Check(File.Exists(secretBin), "迁移后旧文件仍然保留（降级可用）");

        // 规则密码互相独立
        Check(migrated.SaveRulePassword("rule-a", "pwd-a"), "规则 A 密码保存成功");
        Check(migrated.SaveRulePassword("rule-b", "pwd-b"), "规则 B 密码保存成功");
        Check(migrated.LoadRulePassword("rule-a") == "pwd-a", "规则 A 密码可读");
        Check(migrated.LoadRulePassword("rule-b") == "pwd-b", "规则 B 密码可读");
        Check(migrated.HasRulePassword("rule-a"), "规则 A 密码状态为已设置");
        Check(!migrated.HasRulePassword("rule-c"), "未设置的规则没有密码");

        Check(migrated.DeleteRulePassword("rule-a"), "规则 A 密码删除成功");
        Check(migrated.LoadRulePassword("rule-a").Length == 0, "规则 A 密码已删除");
        Check(migrated.LoadRulePassword("rule-b") == "pwd-b", "删除 A 不影响 B");
        Check(migrated.LoadPassword() == "old-password", "删规则密码不影响全局密码");

        // 全局密码仍然双写
        Check(migrated.SavePassword("new-global"), "全局密码保存成功");
        var afterWrite = new CredentialStore(root2);
        afterWrite.Load();
        Check(afterWrite.LoadPassword() == "new-global", "新全局密码可读");
        Check(File.Exists(secretBin), "旧文件仍在（降级后仍能用）");

        // 迁移失败回退：secrets.json 写坏时仍能读旧文件
        string goodSecrets = File.ReadAllText(secretsJson, Encoding.UTF8);
        File.WriteAllText(secretsJson, "{broken");
        var broken = new CredentialStore(root2);
        broken.Load();
        Check(broken.LoadPassword().Length > 0, "多凭据文件损坏时回退读旧文件");
        File.WriteAllText(secretsJson, goodSecrets);

        // 新字段的越界回落
        string settings2 = Path.Combine(root2, "settings.json");
        File.WriteAllText(settings2,
            "{\"Account\":\"a\",\"DeviceGuardThreshold\":999,\"DeviceGuardProbeTarget\":\"\","
            + "\"DeviceGuardProbeTimeoutSeconds\":0,\"DefaultPort\":1,"
            + "\"ConnectionRules\":[{\"Id\":\"r1\",\"MatchType\":0,\"Account\":\"ok\",\"Port\":8603},"
            + "{\"Id\":\"r2\",\"MatchType\":0,\"Account\":\"\",\"Port\":8603}]}");
        var normalized = new CredentialStore(root2);
        normalized.Load();
        Check(normalized.Settings.DeviceGuardThreshold == 2, "设备数阈值越界回落");
        Check(normalized.Settings.DeviceGuardProbeTarget == "www.snnu.edu.cn", "探测目标为空回落默认");
        Check(normalized.Settings.DeviceGuardProbeTimeoutSeconds == 3, "探测超时越界回落");
        Check(normalized.Settings.DefaultPort == 8603, "默认端口越界回落");
        Check(normalized.Settings.ConnectionRules.Count == 1, "非法规则被单独丢弃，其余保留");
        Check(normalized.Settings.ConnectionRules.Count == 1
              && normalized.Settings.ConnectionRules[0].Id == "r1", "保留下来的是合法规则");

        // 旧版默认探测目标（公网 DNS IP）要迁到新默认上，不能留在那儿
        File.WriteAllText(settings2,
            "{\"Account\":\"a\",\"DeviceGuardProbeTarget\":\"223.5.5.5\"}");
        var probeMigrated = new CredentialStore(root2);
        probeMigrated.Load();
        Check(probeMigrated.Settings.DeviceGuardProbeTarget == "www.snnu.edu.cn",
            "旧版默认探测目标迁到学校官网");

        // 用户自己改过的目标不能被动到
        File.WriteAllText(settings2,
            "{\"Account\":\"a\",\"DeviceGuardProbeTarget\":\"1.1.1.1\"}");
        var customized = new CredentialStore(root2);
        customized.Load();
        Check(customized.Settings.DeviceGuardProbeTarget == "1.1.1.1",
            "用户自定义的探测目标保持不变");

        // 两条有线规则互相冲突：后一条应被丢弃
        File.WriteAllText(settings2,
            "{\"Account\":\"a\",\"ConnectionRules\":["
            + "{\"Id\":\"r1\",\"MatchType\":0,\"Account\":\"ok\",\"Port\":8603},"
            + "{\"Id\":\"r2\",\"MatchType\":0,\"Account\":\"ok2\",\"Port\":8603}]}");
        var conflict = new CredentialStore(root2);
        conflict.Load();
        Check(conflict.Settings.ConnectionRules.Count == 1, "同一匹配方式只保留一条规则");

        // 旧版规则只有 Port、没有 Operator：那时规则不覆盖出口，一律用全局出口。
        // 迁上来必须保持这个语义，不能悄悄变成「校园网直连」。
        File.WriteAllText(settings2,
            "{\"Account\":\"a\",\"Operator\":2,\"ConnectionRules\":["
            + "{\"Id\":\"r1\",\"MatchType\":0,\"Account\":\"ok\",\"Port\":8603}]}");
        var legacyRule = new CredentialStore(root2);
        legacyRule.Load();
        Check(legacyRule.Settings.ConnectionRules.Count == 1, "旧版规则被正常读出");
        Check(legacyRule.Settings.ConnectionRules[0].Operator == (Operator)2,
            "旧版规则迁上来沿用全局出口，不被改成校园网直连");

        // 新版规则带 Operator：按自己写的来
        File.WriteAllText(settings2,
            "{\"Account\":\"a\",\"Operator\":2,\"ConnectionRules\":["
            + "{\"Id\":\"r1\",\"MatchType\":0,\"Account\":\"ok\",\"Operator\":3}]}");
        var newRule = new CredentialStore(root2);
        newRule.Load();
        Check(newRule.Settings.ConnectionRules.Count == 1
              && newRule.Settings.ConnectionRules[0].Operator == (Operator)3,
            "新版规则用自己的出口");
    }
    finally { Directory.Delete(root2, true); }

    Console.WriteLine($"{checks} credential checks passed.");
}
finally { Directory.Delete(root, true); }

namespace SNNUAutoLogin.Services
{
    public enum Operator { Campus, Mobile, Unicom, Telecom }
}
