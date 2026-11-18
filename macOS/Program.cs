using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;

namespace SNNUAutoLogin.Mac;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Contains("--core-selftest")) return CoreSelfTest.RunAsync().GetAwaiter().GetResult();
        if (!OperatingSystem.IsMacOSVersionAtLeast(14))
        {
            Console.Error.WriteLine("需要 macOS 14 或更新版本");
            return 1;
        }
        Directory.CreateDirectory(MacPaths.ConfigDirectory,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        FileStream instance;
        try
        {
            instance = new FileStream(Path.Combine(MacPaths.ConfigDirectory, "instance.lock"),
                FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException)
        {
            File.WriteAllText(Path.Combine(MacPaths.ConfigDirectory, "show-window"), "");
            return 0;
        }
        using (instance)
            return AppBuilder.Configure<DesktopApp>().UsePlatformDetect().StartWithClassicDesktopLifetime(args);
    }
}

public sealed class DesktopApp : Application
{
    private IClassicDesktopStyleApplicationLifetime _desktop = null!;
    private MacRuntime _runtime = null!;
    private TrayIcon? _tray;
    private readonly DispatcherTimer _showRequests = new() { Interval = TimeSpan.FromSeconds(1) };
    private bool _quitting;
    internal static bool Quitting { get; private set; }

    public override void Initialize() => Styles.Add(new FluentTheme());

    public override async void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop) return;
        _desktop = desktop;
        desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        desktop.ShutdownRequested += (_, e) =>
        {
            if (Quitting) return;
            e.Cancel = true;
            _ = QuitAsync();
        };
        bool smoke = desktop.Args?.Contains("--smoke-test") == true;
        _runtime = new MacRuntime();
        AgentClient.Runtime = _runtime;
        try
        {
            await _runtime.InitializeAsync();
            var window = new MainWindow();
            desktop.MainWindow = window;
            var show = new NativeMenuItem("显示窗口");
            show.Click += (_, _) => ShowWindow();
            var reconnect = new NativeMenuItem("重新登录");
            reconnect.Click += async (_, _) =>
            {
                try { await _runtime.SendAsync(new("reconnect"), CancellationToken.None); }
                catch (Exception) { ShowWindow(); }
            };
            var quit = new NativeMenuItem("退出");
            quit.Click += async (_, _) => await QuitAsync();
            using var icon = typeof(DesktopApp).Assembly.GetManifestResourceStream("AppIcon.png")!;
            _tray = new TrayIcon
            {
                Icon = new WindowIcon(icon), ToolTipText = "陕师大校园网自动登录",
                Menu = new NativeMenu { Items = { show, reconnect, new NativeMenuItemSeparator(), quit } }
            };
            TrayIcon.SetIcons(this, new TrayIcons { _tray });
            _showRequests.Tick += (_, _) =>
            {
                string path = Path.Combine(MacPaths.ConfigDirectory, "show-window");
                if (!File.Exists(path)) return;
                File.Delete(path);
                ShowWindow();
            };
            _showRequests.Start();
            ShowWindow();
            if (smoke)
            {
                await Task.Delay(1500);
                window.Hide();
                await Task.Delay(1000);
                var status = await _runtime.SendAsync(new("status"), CancellationToken.None);
                if (!status.Success || window.IsVisible) throw new InvalidOperationException("后台未运行");
                ShowWindow();
                await Task.Delay(500);
                File.WriteAllText(Path.Combine(MacPaths.ConfigDirectory, "smoke-ok"), "window, background, restore, exit");
                await QuitAsync();
            }
        }
        catch (Exception ex)
        {
            File.WriteAllText(Path.Combine(MacPaths.ConfigDirectory, "startup-error.txt"), ex.ToString());
            if (smoke) { desktop.Shutdown(1); return; }
            var error = new Window
            {
                Title = "启动失败", Width = 560, Height = 220,
                Content = new TextBlock { Margin = new Thickness(20), TextWrapping = Avalonia.Media.TextWrapping.Wrap,
                    Text = "无法启动，请检查配置目录中的 startup-error.txt。\n" + ex.Message }
            };
            desktop.MainWindow = error;
            error.Closed += (_, _) => desktop.Shutdown(1);
            error.Show();
        }
        base.OnFrameworkInitializationCompleted();
    }

    private void ShowWindow()
    {
        if (_desktop.MainWindow is not { } window) return;
        window.Show();
        window.WindowState = WindowState.Normal;
        window.Activate();
    }

    private async Task QuitAsync()
    {
        if (_quitting) return;
        _quitting = true;
        _showRequests.Stop();
        try { await _runtime.DisposeAsync(); }
        finally
        {
            Quitting = true;
            _tray?.Dispose();
            _desktop.Shutdown();
        }
    }
}
