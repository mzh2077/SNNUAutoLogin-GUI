using System;
using System.Runtime.InteropServices;

namespace SNNUAutoLogin.Services;

public sealed class TrayMenuState
{
    public bool AutoStart { get; set; }
    public bool AutoReconnect { get; set; }
    public bool MinimizeToTray { get; set; } = true;
    public string Tip { get; set; } = "SNNU 校园网助手";
}

/// <summary>
/// 任务栏通知区域图标（纯 Win32 Shell_NotifyIcon，不引入任何第三方托盘库）。
/// 用一个隐藏的普通窗口接收回调消息，菜单走系统原生 TrackPopupMenu。
/// </summary>
public sealed class TrayIcon : IDisposable
{
    public const int CmdShow = 1001;
    public const int CmdLogin = 1002;
    public const int CmdToggleAutoStart = 1003;
    public const int CmdToggleReconnect = 1004;
    public const int CmdToggleTray = 1005;
    public const int CmdExit = 1006;

    private const int WM_APP = 0x8000;
    private const int WM_TRAYICON = WM_APP + 1;
    private const int WM_COMMAND = 0x0111;
    private const int WM_NULL = 0x0000;

    private const int WM_LBUTTONUP = 0x0202;
    private const int WM_LBUTTONDBLCLK = 0x0203;
    private const int WM_RBUTTONUP = 0x0205;
    private const int WM_CONTEXTMENU = 0x007B;

    private const uint NIF_MESSAGE = 0x01;
    private const uint NIF_ICON = 0x02;
    private const uint NIF_TIP = 0x04;

    private const uint NIM_ADD = 0x00;
    private const uint NIM_MODIFY = 0x01;
    private const uint NIM_DELETE = 0x02;
    private const uint NIM_SETVERSION = 0x04;

    private const uint NOTIFYICON_VERSION_4 = 4;

    private const uint MF_STRING = 0x0000;
    private const uint MF_SEPARATOR = 0x0800;
    private const uint MF_CHECKED = 0x0008;
    private const uint MF_DEFAULT = 0x1000;

    private const uint TPM_RETURNCMD = 0x0100;
    private const uint TPM_RIGHTBUTTON = 0x0002;
    private const uint TPM_LEFTALIGN = 0x0000;

    /// <summary>
    /// 跨进程唤醒消息：第二次启动程序时向已经常驻的实例广播它，
    /// 由托盘隐藏窗口接住并把主窗口叫出来（而不是弹「已在运行」的对话框）。
    /// </summary>
    public static readonly uint WakeMessage = RegisterWindowMessageW("SNNUAutoLogin.Wake.v1");

    private const int HWND_BROADCAST = 0xFFFF;

    /// <summary>向所有顶层窗口广播唤醒消息，由已运行的实例响应。</summary>
    public static void BroadcastWake()
    {
        if (WakeMessage == 0) return;
        try { PostMessageW((IntPtr)HWND_BROADCAST, WakeMessage, IntPtr.Zero, IntPtr.Zero); }
        catch { }
    }

    private readonly WndProcDelegate _wndProc;
    private readonly string _className;
    private readonly uint _taskbarCreatedMsg;

    private IntPtr _hwnd;
    private IntPtr _icon;
    private TrayMenuState _state = new();
    private bool _added;
    private bool _disposed;

    public bool IsVisible => _added && !_disposed;

    public event Action? ShowRequested;
    public event Action? CommandInvoked;

    public TrayIcon()
    {
        _wndProc = WndProc;
        _className = "SNNUAutoLogin.TrayHost." + Guid.NewGuid().ToString("N");
        _taskbarCreatedMsg = RegisterWindowMessageW("TaskbarCreated");

        var hInstance = GetModuleHandleW(null);
        var wc = new WNDCLASSEX
        {
            cbSize = Marshal.SizeOf<WNDCLASSEX>(),
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProc),
            hInstance = hInstance,
            lpszClassName = _className
        };

        if (RegisterClassExW(ref wc) == 0)
            throw new InvalidOperationException("RegisterClassEx 失败");

        _hwnd = CreateWindowExW(0, _className, "SNNUAutoLogin.Tray", 0, 0, 0, 0, 0,
            IntPtr.Zero, IntPtr.Zero, hInstance, IntPtr.Zero);

        if (_hwnd == IntPtr.Zero)
            throw new InvalidOperationException("创建托盘消息窗口失败");
    }

    /// <summary>最近一次点击的菜单项（供宿主判断）。</summary>
    public int LastCommand { get; private set; } = -1;

    public bool Show(TrayMenuState state, string exePath)
    {
        _state = state;
        if (_icon == IntPtr.Zero) _icon = LoadAppIcon(exePath);

        var data = BuildData();
        data.uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP;
        data.uCallbackMessage = WM_TRAYICON;
        data.hIcon = _icon;
        data.szTip = Clip(state.Tip, 127);

        if (!_added)
        {
            _added = Shell_NotifyIconW(NIM_ADD, ref data);
            if (_added)
            {
                data.uTimeoutOrVersion = NOTIFYICON_VERSION_4;
                Shell_NotifyIconW(NIM_SETVERSION, ref data);
            }
        }
        else
        {
            _added = Shell_NotifyIconW(NIM_MODIFY, ref data);
        }

        return _added;
    }

    /// <summary>只更新提示文字（状态变化时调用）。</summary>
    public void UpdateTip(string tip)
    {
        _state.Tip = tip;
        if (!_added) return;

        var data = BuildData();
        data.uFlags = NIF_TIP;
        data.szTip = Clip(tip, 127);
        Shell_NotifyIconW(NIM_MODIFY, ref data);
    }

    public void UpdateState(TrayMenuState state)
    {
        _state = state;
        UpdateTip(state.Tip);
    }

    private NOTIFYICONDATA BuildData() => new()
    {
        cbSize = Marshal.SizeOf<NOTIFYICONDATA>(),
        hWnd = _hwnd,
        uID = 1,
        szTip = "",
        szInfo = "",
        szInfoTitle = ""
    };

    private IntPtr WndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg == _taskbarCreatedMsg && _taskbarCreatedMsg != 0)
        {
            // 资源管理器重启后图标会被清掉，重新挂一次
            _added = false;
            Show(_state, Environment.ProcessPath ?? "");
            return IntPtr.Zero;
        }

        if (msg == WakeMessage && WakeMessage != 0)
        {
            ShowRequested?.Invoke();
            return IntPtr.Zero;
        }

        if (msg == WM_TRAYICON)
        {
            int mouse = (int)(lParam.ToInt64() & 0xFFFF);

            if (mouse is WM_LBUTTONUP or WM_LBUTTONDBLCLK)
            {
                ShowRequested?.Invoke();
                return IntPtr.Zero;
            }

            if (mouse is WM_RBUTTONUP or WM_CONTEXTMENU)
            {
                ShowMenu();
                return IntPtr.Zero;
            }
        }

        if (msg == WM_COMMAND)
        {
            int id = (int)(wParam.ToInt64() & 0xFFFF);
            LastCommand = id;
            CommandInvoked?.Invoke();
            return IntPtr.Zero;
        }

        return DefWindowProcW(hwnd, msg, wParam, lParam);
    }

    private void ShowMenu()
    {
        var menu = CreatePopupMenu();
        if (menu == IntPtr.Zero) return;

        try
        {
            AppendMenu(menu, MF_STRING | MF_DEFAULT, CmdShow, "显示主窗口");
            AppendMenu(menu, MF_STRING, CmdLogin, "立即登录");
            AppendMenu(menu, MF_SEPARATOR, 0, null);

            AppendMenu(menu, MF_STRING | (_state.AutoStart ? MF_CHECKED : 0),
                CmdToggleAutoStart, "开机自动登录");
            AppendMenu(menu, MF_STRING | (_state.AutoReconnect ? MF_CHECKED : 0),
                CmdToggleReconnect, "断线自动重连");
            AppendMenu(menu, MF_STRING | (_state.MinimizeToTray ? MF_CHECKED : 0),
                CmdToggleTray, "关闭窗口时最小化到托盘");

            AppendMenu(menu, MF_SEPARATOR, 0, null);
            AppendMenu(menu, MF_STRING, CmdExit, "退出");

            GetCursorPos(out POINT pt);

            // 必须先抢前台，否则点菜单外面不会自动消失
            SetForegroundWindow(_hwnd);

            int cmd = (int)TrackPopupMenu(menu,
                TPM_RETURNCMD | TPM_RIGHTBUTTON | TPM_LEFTALIGN,
                pt.X, pt.Y, 0, _hwnd, IntPtr.Zero);

            PostMessageW(_hwnd, WM_NULL, IntPtr.Zero, IntPtr.Zero);

            if (cmd == 0) return;

            LastCommand = cmd;
            if (cmd == CmdShow) ShowRequested?.Invoke();
            else CommandInvoked?.Invoke();
        }
        finally
        {
            DestroyMenu(menu);
        }
    }

    private static string Clip(string s, int max) =>
        s.Length <= max ? s : s[..max];

    private static IntPtr LoadAppIcon(string exePath)
    {
        try
        {
            if (!string.IsNullOrEmpty(exePath) && ExtractIconExW(exePath, 0, out var large, out var small, 1) > 0)
            {
                if (small != IntPtr.Zero)
                {
                    if (large != IntPtr.Zero) DestroyIcon(large);
                    return small;
                }
                if (large != IntPtr.Zero) return large;
            }
        }
        catch { }

        return LoadIconW(IntPtr.Zero, (IntPtr)32512); // IDI_APPLICATION
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        try
        {
            if (_added)
            {
                var data = BuildData();
                Shell_NotifyIconW(NIM_DELETE, ref data);
                _added = false;
            }

            if (_icon != IntPtr.Zero)
            {
                DestroyIcon(_icon);
                _icon = IntPtr.Zero;
            }

            if (_hwnd != IntPtr.Zero)
            {
                DestroyWindow(_hwnd);
                _hwnd = IntPtr.Zero;
            }

            UnregisterClassW(_className, GetModuleHandleW(null));
        }
        catch { }
    }

    // ────────────────────────── Win32 ──────────────────────────

    private delegate IntPtr WndProcDelegate(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WNDCLASSEX
    {
        public int cbSize;
        public uint style;
        public IntPtr lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        public string? lpszMenuName;
        public string lpszClassName;
        public IntPtr hIconSm;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NOTIFYICONDATA
    {
        public int cbSize;
        public IntPtr hWnd;
        public uint uID;
        public uint uFlags;
        public uint uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szTip;
        public uint dwState;
        public uint dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szInfo;
        public uint uTimeoutOrVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string szInfoTitle;
        public uint dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ushort RegisterClassExW(ref WNDCLASSEX wc);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool UnregisterClassW(string className, IntPtr hInstance);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowExW(int exStyle, string className, string windowName,
        int style, int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr hInstance, IntPtr param);

    [DllImport("user32.dll")]
    private static extern bool DestroyWindow(IntPtr hwnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr DefWindowProcW(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern uint RegisterWindowMessageW(string name);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandleW(string? name);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern bool Shell_NotifyIconW(uint message, ref NOTIFYICONDATA data);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr CreatePopupMenu();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool AppendMenu(IntPtr menu, uint flags, int id, string? item);

    [DllImport("user32.dll")]
    private static extern bool DestroyMenu(IntPtr menu);

    [DllImport("user32.dll")]
    private static extern uint TrackPopupMenu(IntPtr menu, uint flags, int x, int y,
        int reserved, IntPtr hwnd, IntPtr rect);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT pt);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hwnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool PostMessageW(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int ExtractIconExW(string file, int index,
        out IntPtr large, out IntPtr small, int count);

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr icon);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr LoadIconW(IntPtr hInstance, IntPtr iconName);
}

