using System;
using System.Linq;
using System.Threading;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using SNNUAutoLogin.Services;

namespace SNNUAutoLogin;

public partial class App : Application
{
    private AppRuntime? _runtime;
    private Mutex? _singleInstance;

    public App()
    {
        InitializeComponent();

        UnhandledException += (_, e) =>
        {
            CrashLog("Xaml", e.Exception);
            e.Handled = false;
        };

        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            CrashLog("AppDomain", e.ExceptionObject as Exception);
    }

    private static void CrashLog(string source, Exception? ex)
    {
        try
        {
            var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "snnu_crash.txt");
            System.IO.File.AppendAllText(path,
                $"[{DateTime.Now:HH:mm:ss}] {source}\n{ex}\n\n");
        }
        catch { }
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        var argv = Environment.GetCommandLineArgs();
        bool Has(string s) => argv.Any(a => a.Equals(s, StringComparison.OrdinalIgnoreCase));

        if (Has("--diag"))
        {
            Diagnostics.WriteReport();
            Exit();
            return;
        }

        // 出网探测自检：按当前设置做一次独立探测（不碰门户、不改认证状态）
        if (Has("--probe"))
        {
            Diagnostics.RunProbe();
            Exit();
            return;
        }

        // 登录页解析自检（不联网）：确认验证码字段/图片/表单地址认得出来
        if (Has("--login-selftest"))
        {
            Services.LoginSelfTest.Run();
            Exit();
            return;
        }

        // 单实例：两个进程同时认证会互相把对方踢下线
        _singleInstance = new Mutex(true, @"Local\SNNUAutoLogin.SingleInstance", out bool createdNew);
        if (!createdNew)
        {
            // 已有实例在跑（它的窗口可能已经被释放）：广播唤醒，然后静默退出
            if (!Has("--tray")) TrayIcon.BroadcastWake();
            Exit();
            return;
        }

        _runtime = new AppRuntime(DispatcherQueue.GetForCurrentThread(), launchedAtStartup: Has("--tray"));
        _runtime.AttachTray();

        // --tray（开机自启路径）：完全不创建窗口，进程只跑托盘 + 巡检
        // 托盘注册失败时退回带窗口的形态，否则程序没有任何入口可以找回
        if (!Has("--tray") || _runtime.Tray?.IsVisible != true)
        {
            _runtime.ShowWindow();

            // 首帧先出：巡检线程、网络事件订阅、托盘状态写入都放到下一轮低优先级，
            // 窗口不会因为「启动时同时干活」而晚 100~200ms 才画出来。
            var dq = DispatcherQueue.GetForCurrentThread();
            if (dq is not null) dq.TryEnqueue(DispatcherQueuePriority.Low, _runtime.Start);
            else _runtime.Start();

            return;
        }

        _runtime.Start();
    }
}
