using System;
using System.Runtime.InteropServices;

namespace SNNUAutoLogin.Services;

/// <summary>
/// 在窗口过程层拦截 WM_CLOSE。
/// WinUI 3 的 AppWindow.Closing 在程序内调用 Close() 时不会触发，
/// 只有子类化 HWND 才能可靠地做到"关窗口 = 收进托盘"。
/// </summary>
public sealed class WindowCloseHook : IDisposable
{
    private const uint WM_CLOSE = 0x0010;

    private readonly SubclassProc _proc;
    private readonly IntPtr _hwnd;
    private readonly UIntPtr _id = (UIntPtr)0x534E4E55;
    private bool _installed;
    private bool _disposed;

    /// <summary>返回 true 表示已拦截（窗口保持存活）。</summary>
    public Func<bool>? InterceptClose { get; set; }
    public Action? DisplayChanged { get; set; }
    public bool IsInMoveSize { get; private set; }

    public WindowCloseHook(IntPtr hwnd)
    {
        _hwnd = hwnd;
        _proc = Handle;

        try
        {
            _installed = SetWindowSubclass(_hwnd, _proc, _id, IntPtr.Zero);
        }
        catch
        {
            _installed = false;
        }
    }

    private IntPtr Handle(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam, UIntPtr id, IntPtr data)
    {
        if (msg == 0x0231) IsInMoveSize = true;
        if (msg == 0x0232) IsInMoveSize = false;
        if (!_disposed && msg is 0x007E or 0x02E0 or 0x001A) // display mode, DPI, work area
        {
            try { DisplayChanged?.Invoke(); } catch { }
        }
        if (msg == WM_CLOSE && !_disposed)
        {
            try
            {
                if (InterceptClose?.Invoke() == true) return IntPtr.Zero;
            }
            catch
            {
                // 拦截失败就放行，避免窗口关不掉
            }
        }

        return DefSubclassProc(hWnd, msg, wParam, lParam);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (!_installed) return;

        try
        {
            RemoveWindowSubclass(_hwnd, _proc, _id);
        }
        catch { }
    }

    private delegate IntPtr SubclassProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam,
        UIntPtr id, IntPtr refData);

    [DllImport("comctl32.dll", SetLastError = true)]
    private static extern bool SetWindowSubclass(IntPtr hWnd, SubclassProc proc,
        UIntPtr id, IntPtr refData);

    [DllImport("comctl32.dll", SetLastError = true)]
    private static extern bool RemoveWindowSubclass(IntPtr hWnd, SubclassProc proc, UIntPtr id);

    [DllImport("comctl32.dll")]
    private static extern IntPtr DefSubclassProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
}
