using System.Runtime.InteropServices;
using SNNUAutoLogin.Linux;

if (!OperatingSystem.IsLinux())
{
    Console.Error.WriteLine("该程序仅支持 Linux。");
    return 2;
}

using var shutdown = new CancellationTokenSource();
Console.CancelKeyPress += (_, args) => { args.Cancel = true; shutdown.Cancel(); };
using var term = PosixSignalRegistration.Create(PosixSignal.SIGTERM, context =>
{
    context.Cancel = true;
    shutdown.Cancel();
});
try
{
    if (args.FirstOrDefault() == "agent")
    {
        await using var agent = new LinuxAgent(args.Contains("--autostart"));
        await agent.RunAsync(shutdown.Token);
        return 0;
    }
    return await LinuxCli.RunAsync(args, shutdown.Token);
}
catch (OperationCanceledException)
{
    Console.Error.WriteLine("操作已取消或等待后台响应超时。");
    return 2;
}
catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException or ArgumentException)
{
    Console.Error.WriteLine(ex.Message);
    return 2;
}
