using System.Diagnostics;

namespace SNNUAutoLogin.Linux;

internal static class LinuxProcess
{
    public static ProcessStartInfo StartInfo(string command, params string[] args)
    {
        var info = new ProcessStartInfo(command)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        info.Environment["LC_ALL"] = "C.UTF-8";
        foreach (var argument in args) info.ArgumentList.Add(argument);
        return info;
    }

    public static async Task<string?> RunAsync(string command, string[] args, CancellationToken cancellation,
        string? input = null, int seconds = 5)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        timeout.CancelAfter(TimeSpan.FromSeconds(seconds));
        var info = StartInfo(command, args);
        info.RedirectStandardInput = input is not null;
        Process? process;
        try { process = Process.Start(info); }
        catch (System.ComponentModel.Win32Exception) { return null; }
        using (process)
        {
            if (process is null) return null;
            try
            {
                var output = process.StandardOutput.ReadToEndAsync(timeout.Token);
                var error = process.StandardError.ReadToEndAsync(timeout.Token);
                if (input is not null)
                {
                    await process.StandardInput.WriteAsync(input.AsMemory(), timeout.Token);
                    process.StandardInput.Close();
                }
                await process.WaitForExitAsync(timeout.Token);
                await error;
                return process.ExitCode == 0 ? (await output).TrimEnd('\r', '\n') : null;
            }
            catch (OperationCanceledException) when (!cancellation.IsCancellationRequested) { return null; }
            finally
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
            }
        }
    }
}
