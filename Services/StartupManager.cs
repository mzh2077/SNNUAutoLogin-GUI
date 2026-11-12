using System;
using Microsoft.Win32;

namespace SNNUAutoLogin.Services;

/// <summary>开机自启动（HKCU Run，无需管理员权限）。</summary>
public static class StartupManager
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "SNNUAutoLogin";

    private static string ExePath
    {
        get
        {
            var p = Environment.ProcessPath;
            return string.IsNullOrEmpty(p) ? "" : p;
        }
    }

    public static bool IsEnabled() => IsEnabled(ExePath, RunKey);

    internal static bool IsEnabled(string executablePath, string runKey)
        => IsEnabled(executablePath, () => ReadEntry(runKey));

    internal static bool IsEnabled(string executablePath, Func<string?> readEntry)
    {
        try
        {
            // 程序被移动过则视为失效，允许覆盖
            return !string.IsNullOrEmpty(executablePath)
                && string.Equals(readEntry(), Command(executablePath), StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static string Command(string executablePath) => $"\"{executablePath}\" --tray";

    public static bool SetEnabled(bool enabled) => SetEnabled(enabled, ExePath, RunKey);

    internal static bool SetEnabled(bool enabled, string executablePath, string runKey)
        => SetEnabled(enabled, executablePath, () => ReadEntry(runKey), value => WriteEntry(runKey, value));

    internal static bool SetEnabled(bool enabled, string executablePath, Func<string?> readEntry, Action<string?> writeEntry)
    {
        try
        {
            if (enabled && string.IsNullOrEmpty(executablePath)) return false;
            string? expected = enabled ? Command(executablePath) : null;
            writeEntry(expected);
            return string.Equals(readEntry(), expected, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static string? ReadEntry(string runKey)
    {
        using var key = Registry.CurrentUser.OpenSubKey(runKey, false);
        return key?.GetValue(ValueName) as string;
    }

    private static void WriteEntry(string runKey, string? value)
    {
        if (value is null)
        {
            using var key = Registry.CurrentUser.OpenSubKey(runKey, true);
            key?.DeleteValue(ValueName, false);
            return;
        }

        using var writableKey = Registry.CurrentUser.CreateSubKey(runKey, true)
            ?? throw new InvalidOperationException("无法打开开机自启注册表项");
        writableKey.SetValue(ValueName, value, RegistryValueKind.String);
    }
}
