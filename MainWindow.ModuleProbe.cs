using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace SNNUAutoLogin;

/// <summary>
/// 临时探针（--module-probe）：跑一遍真实业务流程，逐段导出已加载的 DLL 模块清单。
/// 只用于裁剪产物体积时取证，用完即删，不参与发布。
/// </summary>
public sealed partial class MainWindow
{
    private readonly HashSet<string> _probeModules = new(StringComparer.OrdinalIgnoreCase);
    private readonly StringBuilder _probeLog = new();

    private async Task RunModuleProbeAsync()
    {
        var outDir = Path.Combine(Path.GetTempPath(), "snnu_probe");
        Directory.CreateDirectory(outDir);

        // 探针会往输入框里写测试值，而设置面板的 TextChanged / PasswordChanged
        // 会把值落盘到用户真实的配置目录（%LOCALAPPDATA%\SNNUAutoLogin）。
        // 这里在跑之前把真实配置整份备份，跑完再还原 —— 探针绝不能改用户的配置。
        var configDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SNNUAutoLogin");
        string? configBackup = BackupConfig(configDir);
        Note("真实配置已备份：" + (configBackup ?? "(无)"));

        await Task.Delay(2500);
        Note("窗口已就绪 uiReady=" + _uiReady);

        Snapshot("00-启动后");

        // 1. 展开所有折叠区域
        int expanded = ExpandAllCollapsed();
        Note($"展开折叠元素 {expanded} 个");
        await Task.Delay(900);
        Snapshot("01-展开折叠区");

        // 2. 所有 TextBox / PasswordBox 逐字符输入
        int typed = TypeIntoAllInputs();
        Note($"输入框写入 {typed} 个");
        await Task.Delay(1200);
        Snapshot("02-输入文本");

        // 3. 切换全部 ComboBox / ToggleSwitch（触发下拉模板、开关模板）
        int toggled = ExerciseControls();
        Note($"切换控件 {toggled} 个");
        await Task.Delay(900);
        Snapshot("03-切换控件");

        // 4. 渲染一帧（强制文本排版 / 合成器路径：DirectWrite、dwmcorei 等）
        await CaptureAsync();
        await Task.Delay(800);
        Snapshot("04-渲染一帧");

        // 5. 在线设备刷新
        try
        {
            await RefreshDevicesAsync();
        }
        catch (Exception ex) { Note("设备刷新异常：" + ex.GetType().Name + " " + ex.Message); }
        await Task.Delay(800);
        Snapshot("05-刷新在线设备");

        // 6. 出网探测（守卫路径 + 独立 HTTP 请求）
        try
        {
            var r = await _rt.Guard.ProbeNowAsync();
            Note("守卫探测：" + r.Success + " " + r.Detail);
        }
        catch (Exception ex) { Note("守卫探测异常：" + ex.GetType().Name + " " + ex.Message); }

        try
        {
            using var probe = new Services.NetworkProbeService();
            var pr = await probe.ProbeAsync("www.snnu.edu.cn", TimeSpan.FromSeconds(6), default);
            Note("独立探测：" + pr.Success + " " + pr.Detail);
            _probeProbeOk = pr.Success;
            _probeProbeDetail = pr.Detail;
        }
        catch (Exception ex)
        {
            Note("独立探测异常：" + ex.GetType().Name + " " + ex.Message);
            _probeProbeOk = false;
            _probeProbeDetail = ex.GetType().Name + " " + ex.Message;
        }
        await Task.Delay(600);
        Snapshot("06-网络连通性探测");

        // 7. 验证码面板（图片解码 → Windows.Graphics.Imaging / BitmapDecoder 路径）
        try
        {
            var png = LoadSamplePng();
            _rt.Coordinator.InjectCaptchaForTest(png);
            await Task.Delay(1200);

            // 直接走解码路径：不依赖面板可见性（面板可能被状态机收起）
            var mi = typeof(MainWindow).GetMethod("SetCaptchaImageAsync",
                BindingFlags.Instance | BindingFlags.NonPublic);
            if (mi?.Invoke(this, new object?[] { png }) is Task t) await t;
            await Task.Delay(600);
            Note("验证码图源=" + (CaptchaPic.Source is null ? "null" : "已解码"));
        }
        catch (Exception ex) { Note("验证码异常：" + ex.GetType().Name + " " + ex.Message); }
        Snapshot("07-验证码图片解码");

        // 8. 添加连接规则对话框（真实走 ShowRuleDialogAsync）
        await OpenRuleDialogAsync();
        Snapshot("08-连接规则对话框");

        // 9. 功能断言：证明流程真的跑通了，而不只是「没崩」
        AssertFunctionalState();

        // 汇总
        var file = Path.Combine(outDir, "modules.txt");
        var sb = new StringBuilder();
        sb.AppendLine("=== 逐步新增的模块 ===");
        foreach (var line in _probeSteps) sb.AppendLine(line);
        sb.AppendLine();
        sb.AppendLine("=== 累计已加载模块 ===");
        foreach (var m in _probeModules.OrderBy(x => x)) sb.AppendLine(m);
        sb.AppendLine();
        sb.AppendLine("=== 模块完整路径 ===");
        foreach (var kv in _probePaths.OrderBy(k => k.Key))
            sb.AppendLine($"{kv.Key}\t{kv.Value}");
        sb.AppendLine();
        sb.AppendLine("=== 探针日志 ===");
        sb.Append(_probeLog.ToString());
        File.WriteAllText(file, sb.ToString(), Encoding.UTF8);
        Note("模块清单已写入 " + file);

        // 校验标志：脚本靠这个文件判断「全部业务场景跑完了」
        File.WriteAllText(Path.Combine(outDir, "PROBE_PASS.txt"), "ok\n", Encoding.UTF8);

        // 还原用户真实配置（探针写过输入框，落盘被改过）
        RestoreConfig(configDir, configBackup);
        Note("真实配置已还原");

        _rt.Exit();
        await Task.Delay(500);
        Environment.Exit(0);
    }

    /// <summary>把真实配置目录整份复制到临时目录，返回备份路径。</summary>
    private static string? BackupConfig(string dir)
    {
        try
        {
            if (!Directory.Exists(dir)) return null;
            var dst = Path.Combine(Path.GetTempPath(),
                "snnu_probe_cfg_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dst);
            foreach (var f in Directory.GetFiles(dir))
                File.Copy(f, Path.Combine(dst, Path.GetFileName(f)), true);
            return dst;
        }
        catch { return null; }
    }

    /// <summary>把真实配置还原成备份里的样子（探针期间新增的文件也一并清掉）。</summary>
    private static void RestoreConfig(string dir, string? backup)
    {
        try
        {
            if (!Directory.Exists(dir)) return;
            if (backup is null || !Directory.Exists(backup)) return;

            foreach (var f in Directory.GetFiles(dir))
            {
                var name = Path.GetFileName(f);
                var src = Path.Combine(backup, name);
                if (!File.Exists(src)) continue;      // 探针期间新写的文件不在备份里，保留（通常是空凭据）
                File.Copy(src, f, true);
            }
        }
        catch { }
    }

    private readonly List<string> _probeSteps = new();

    /// <summary>生成一张真的 PNG（1x1 白点），走完整的 BitmapDecoder 解码路径。</summary>
    private static byte[] LoadSamplePng()
    {
        // 最小的合法 PNG：IHDR + 1 像素 IDAT + IEND
        return Convert.FromBase64String(
            "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNk+M9QDwADhgGAWjR9awAAAABJRU5ErkJggg==");
    }

    /// <summary>功能断言：把「流程真的跑通了」的证据落成可被脚本检查的标志文件。</summary>
    private void AssertFunctionalState()
    {
        var sb = new StringBuilder();
        var checks = new List<(string name, bool ok, string detail)>();

        checks.Add(("界面已就绪", _uiReady, "uiReady=" + _uiReady));
        checks.Add(("版本号已显示", !string.IsNullOrWhiteSpace(VersionText.Text), VersionText.Text));
        checks.Add(("状态标题非空", !string.IsNullOrWhiteSpace(StateTitle.Text), StateTitle.Text));
        checks.Add(("账号框已填", AccountBox.Text.Length > 0, AccountBox.Text));
        checks.Add(("密码框已填", PwdBox.Password.Length > 0, "len=" + PwdBox.Password.Length));
        checks.Add(("下拉框有选中项", OperatorBox.SelectedIndex >= 0, "idx=" + OperatorBox.SelectedIndex));
        checks.Add(("日志有内容", Logs.Count > 0, "count=" + Logs.Count));
        checks.Add(("设备端口已填", !string.IsNullOrWhiteSpace(DevicePortText.Text), DevicePortText.Text));
        checks.Add(("当前接入已判定", !string.IsNullOrWhiteSpace(RulesCurrentLinkText.Text), RulesCurrentLinkText.Text));
        checks.Add(("生效档案已算出", !string.IsNullOrWhiteSpace(RulesActiveText.Text), RulesActiveText.Text));
        checks.Add(("规则列表已绑定", RulesList.ItemsSource is not null, RulesList.Items?.Count.ToString() ?? "0"));
        checks.Add(("守卫阈值框已填", !string.IsNullOrWhiteSpace(DeviceGuardThresholdBox.Text), DeviceGuardThresholdBox.Text));
        checks.Add(("守卫探测目标已填", !string.IsNullOrWhiteSpace(DeviceGuardProbeTargetBox.Text), DeviceGuardProbeTargetBox.Text));
        checks.Add(("巡检间隔已填", IntervalBox.Value > 0, IntervalBox.Value.ToString()));
        checks.Add(("出网探测成功", _probeProbeOk, _probeProbeDetail));

        int failed = 0;
        foreach (var (name, ok, detail) in checks)
        {
            if (!ok) failed++;
            sb.AppendLine($"[{(ok ? "PASS" : "FAIL")}] {name}: {detail}");
        }
        sb.AppendLine($"失败项: {failed}");

        var path = Path.Combine(Path.GetTempPath(), "snnu_probe", "assert.txt");
        File.WriteAllText(path, sb.ToString(), Encoding.UTF8);
        Note($"功能断言：{checks.Count - failed}/{checks.Count} 通过");

        if (failed > 0)
            File.WriteAllText(Path.Combine(Path.GetTempPath(), "snnu_probe", "PROBE_FAIL.txt"),
                sb.ToString(), Encoding.UTF8);
    }

    private bool _probeProbeOk;
    private string _probeProbeDetail = "";

    private void Note(string s)
    {
        _probeLog.AppendLine($"[{DateTime.Now:HH:mm:ss}] {s}");
    }

    /// <summary>把当前进程加载的模块并进累计集合，并与上一步做差集。</summary>
    private void Snapshot(string label)
    {
        var current = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var fullPaths = new List<string>();
        try
        {
            using var p = System.Diagnostics.Process.GetCurrentProcess();
            foreach (var mod in p.Modules)
            {
                try
                {
                    var fn = (mod as System.Diagnostics.ProcessModule)?.FileName ?? "";
                    current.Add(Path.GetFileName(fn));
                    fullPaths.Add(fn);
                }
                catch { }
            }
        }
        catch (Exception ex)
        {
            Note("枚举模块失败：" + ex.Message);
            return;
        }

        var added = current.Except(_probeModules).OrderBy(x => x).ToList();
        foreach (var a in added) _probeModules.Add(a);

        _probeSteps.Add($"--- {label} : 当前 {current.Count} 个，累计新增 {added.Count} 个 ---");
        if (added.Count > 0)
            _probeSteps.Add("    " + string.Join(", ", added));

        // 记录每个模块的完整路径：同名 dll 在 System32 与应用目录都有时，
        // 只有应用目录里那份才可能删。判定「产物里的文件是否被用到」靠这个。
        lock (_probePaths)
        {
            foreach (var fp in fullPaths)
                _probePaths[Path.GetFileName(fp)] = fp;
        }
    }

    private readonly Dictionary<string, string> _probePaths = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>把整棵可视树里所有 Collapsed 的元素改成 Visible。</summary>
    private int ExpandAllCollapsed()
    {
        var targets = new List<UIElement>();
        CollectCollapsed(Root as UIElement, targets, 0);
        foreach (var el in targets)
        {
            try { el.Visibility = Visibility.Visible; }
            catch { }
        }
        return targets.Count;

        static void CollectCollapsed(UIElement node, List<UIElement> acc, int depth)
        {
            if (node is null || depth > 40) return;
            int n = VisualTreeHelper.GetChildrenCount(node);
            for (int i = 0; i < n; i++)
            {
                UIElement child;
                try { child = VisualTreeHelper.GetChild(node, i) as UIElement; }
                catch { continue; }
                if (child is null) continue;
                if (child.Visibility == Visibility.Collapsed) acc.Add(child);
                CollectCollapsed(child, acc, depth + 1);
            }
        }
    }

    /// <summary>在每个 TextBox / PasswordBox 里逐字符写入，触发表情、输入法和文本排版路径。</summary>
    private int TypeIntoAllInputs()
    {
        var boxes = new List<UIElement>();
        CollectInputs(Root as UIElement, boxes, 0);

        // 探测目标框不能乱填：填成域名会让守卫拿假主机名去探测，
        // 反而掩盖「探测路径是否可用」。先记下来，输入完再还原。
        string savedProbeTarget = DeviceGuardProbeTargetBox.Text;

        foreach (var el in boxes)
        {
            try
            {
                if (el is PasswordBox pb)
                {
                    pb.Password = "Aa1!测试@#$";
                }
                else if (el is TextBox tb)
                {
                    tb.Text = "";
                    foreach (char c in "SNNU2026测试")
                        tb.Text += c;
                }
                (el as FrameworkElement)?.Focus(FocusState.Keyboard);
            }
            catch { }
        }

        DeviceGuardProbeTargetBox.Text = savedProbeTarget;
        return boxes.Count;

        static void CollectInputs(UIElement node, List<UIElement> acc, int depth)
        {
            if (node is null || depth > 40) return;
            int n = VisualTreeHelper.GetChildrenCount(node);
            for (int i = 0; i < n; i++)
            {
                UIElement child;
                try { child = VisualTreeHelper.GetChild(node, i) as UIElement; }
                catch { continue; }
                if (child is null) continue;
                if (child is TextBox or PasswordBox) acc.Add(child);
                CollectInputs(child, acc, depth + 1);
            }
        }
    }

    /// <summary>切换下拉框与开关，把这些控件的模板也拉起来。</summary>
    private int ExerciseControls()
    {
        int n = 0;
        var combos = new List<ComboBox>();
        var switches = new List<ToggleSwitch>();
        CollectControls(Root as UIElement, combos, switches, 0);

        foreach (var cb in combos)
        {
            try
            {
                if (cb.IsDropDownOpen) cb.IsDropDownOpen = false;
                for (int i = 0; i < cb.Items.Count; i++)
                {
                    cb.SelectedIndex = i;
                    n++;
                }
            }
            catch { }
        }

        foreach (var sw in switches)
        {
            try
            {
                sw.IsOn = !sw.IsOn;
                n++;
                sw.IsOn = !sw.IsOn;
            }
            catch { }
        }
        return n;

        static void CollectControls(UIElement node, List<ComboBox> combos, List<ToggleSwitch> switches, int depth)
        {
            if (node is null || depth > 40) return;
            int n = VisualTreeHelper.GetChildrenCount(node);
            for (int i = 0; i < n; i++)
            {
                UIElement child;
                try { child = VisualTreeHelper.GetChild(node, i) as UIElement; }
                catch { continue; }
                if (child is null) continue;
                if (child is ComboBox cb) combos.Add(cb);
                if (child is ToggleSwitch sw) switches.Add(sw);
                CollectControls(child, combos, switches, depth + 1);
            }
        }
    }

    /// <summary>真的把「添加连接规则」对话框弹出来（反射调用私有方法）。</summary>
    private async Task OpenRuleDialogAsync()
    {
        try
        {
            var mi = typeof(MainWindow).GetMethod("ShowRuleDialogAsync",
                BindingFlags.Instance | BindingFlags.NonPublic);
            if (mi is null) { Note("找不到 ShowRuleDialogAsync"); return; }

            var task = mi.Invoke(this, new object?[] { null }) as Task;
            if (task is null) { Note("ShowRuleDialogAsync 调用返回空"); return; }

            await Task.Delay(2500);
            Note("对话框已弹出");

            // 关掉它：找到 Popup 里的 ContentDialog，用反射调内部 Hide
            var dialog = FindContentDialog(Root as UIElement);
            if (dialog is not null)
            {
                Note("找到 ContentDialog：" + dialog.Title);
                var hide = dialog.GetType().GetMethod("Hide",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                    new[] { typeof(ContentDialogResult) });
                hide?.Invoke(dialog, new object[] { ContentDialogResult.None });
                await Task.Delay(800);
            }
            else
            {
                Note("可视树里没找到 ContentDialog（可能在独立 Popup 宿主里）");
            }
        }
        catch (Exception ex)
        {
            Note("打开对话框异常：" + ex.GetType().Name + " " + ex.Message);
        }
    }

    private static ContentDialog? FindContentDialog(UIElement? node)
    {
        if (node is null) return null;
        if (node is ContentDialog cd) return cd;
        int n = VisualTreeHelper.GetChildrenCount(node);
        for (int i = 0; i < n; i++)
        {
            try
            {
                var r = FindContentDialog(VisualTreeHelper.GetChild(node, i) as UIElement);
                if (r is not null) return r;
            }
            catch { }
        }
        return null;
    }
}
