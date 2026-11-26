using System;
using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;

namespace SNNUAutoLogin.Services;

/// <summary>Only the small reveal strip stays above other windows while tucked away.</summary>
internal sealed class WindowEdgeDock : IDisposable
{
    private readonly IntPtr _hwnd;
    private readonly Func<bool> _paused;
    private readonly DispatcherQueueTimer _timer;
    private DockRect _restore;
    private DockRect _work;
    private IntPtr _monitor;
    private Placement _placement;
    private bool _placementValid;
    private bool _hidden;
    private bool _wasTopmost;
    private bool _disposed;
    private long _outsideSince;
    private long _revealedUntil;
    internal bool IsHidden => _hidden;

    public WindowEdgeDock(IntPtr hwnd, DispatcherQueue dispatcher, Func<bool> paused, bool startTimer = true)
    {
        _hwnd = hwnd;
        _paused = paused;
        _timer = dispatcher.CreateTimer();
        _timer.Interval = TimeSpan.FromMilliseconds(150);
        _timer.Tick += Tick;
        if (startTimer) _timer.Start();
    }

    private void Tick(DispatcherQueueTimer sender, object args)
    {
        if (GetCursorPos(out var pointer)) Poll(pointer.X, pointer.Y);
    }

    internal void Poll(int x, int y)
    {
        var pointer = new Point { X = x, Y = y };
        if (_disposed || !IsWindowVisible(_hwnd)) return;
        if (IsIconic(_hwnd)) return;
        if (IsZoomed(_hwnd))
        {
            if (_hidden && _placementValid)
            {
                var current = new Placement { Length = Marshal.SizeOf<Placement>() };
                if (GetWindowPlacement(_hwnd, ref current))
                {
                    current.Normal = _placement.Normal;
                    SetWindowPlacement(_hwnd, ref current);
                }
            }
            ReleaseStrip();
            return;
        }
        if (_paused()) { Reveal(); return; }
        if (!GetWindowRect(_hwnd, out var raw)) return;
        var visible = VisibleBounds(raw);
        var monitor = MonitorFromWindow(_hwnd, 2);
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfoW(monitor, ref info)) return;
        var work = info.Work.ToRect();

        if (_hidden)
        {
            if (monitor != _monitor || work != _work || visible.Contains(pointer.X, pointer.Y)) Reveal();
            return;
        }
        long now = Environment.TickCount64;
        if (visible.Contains(pointer.X, pointer.Y) || (GetAsyncKeyState(1) & 0x8000) != 0
            || now < _revealedUntil)
        {
            _outsideSince = 0;
            return;
        }
        double scale = Math.Max(1, GetDpiForWindow(_hwnd)) / 96.0;
        var screen = info.Screen.ToRect();
        var edge = EdgeDockGeometry.FindEdge(visible, screen, work, (int)Math.Round(12 * scale));
        if (edge == DockEdge.None || HasAdjacentMonitor(screen, visible, edge))
        {
            _outsideSince = 0;
            return;
        }
        if (_outsideSince == 0) _outsideSince = now;
        if (now - _outsideSince < 750) return;

        _restore = EdgeDockGeometry.Clamp(visible, work);
        _work = work;
        _monitor = monitor;
        _wasTopmost = (GetWindowLongPtrW(_hwnd, -20).ToInt64() & 8) != 0;
        _placement = new Placement { Length = Marshal.SizeOf<Placement>() };
        _placementValid = GetWindowPlacement(_hwnd, ref _placement);
        var hidden = EdgeDockGeometry.Hide(visible, screen, edge, Math.Max(3, (int)Math.Round(4 * scale)));
        _hidden = MoveVisible(hidden, new IntPtr(-1));
        _outsideSince = 0;
    }

    private static bool HasAdjacentMonitor(DockRect screen, DockRect window, DockEdge edge)
    {
        // A seam between two monitors is not an outside screen edge.
        var point = edge switch
        {
            DockEdge.Left => new Point { X = screen.X - 1, Y = window.Y + window.Height / 2 },
            DockEdge.Right => new Point { X = screen.Right, Y = window.Y + window.Height / 2 },
            _ => new Point { X = window.X + window.Width / 2, Y = screen.Y - 1 }
        };
        return MonitorFromPoint(point, 0) != IntPtr.Zero;
    }

    public void Reveal()
    {
        _outsideSince = 0;
        _revealedUntil = Environment.TickCount64 + 1000;
        if (!_hidden) return;
        if (IsIconic(_hwnd) && _placementValid)
        {
            var placement = _placement;
            placement.Show = 9;
            SetWindowPlacement(_hwnd, ref placement);
        }
        if (GetWindowRect(_hwnd, out var raw))
        {
            var visible = VisibleBounds(raw);
            _restore = _restore with { Width = visible.Width, Height = visible.Height };
        }
        var monitor = MonitorFromWindow(_hwnd, 2);
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        var restore = GetMonitorInfoW(monitor, ref info)
            ? EdgeDockGeometry.Clamp(_restore, info.Work.ToRect()) : _restore;
        MoveVisible(restore, new IntPtr(_wasTopmost ? -1 : -2));
        _hidden = false;
    }

    private void ReleaseStrip()
    {
        if (!_hidden) return;
        SetWindowPos(_hwnd, new IntPtr(_wasTopmost ? -1 : -2), 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0010);
        _hidden = false;
        _outsideSince = 0;
    }

    private bool MoveVisible(DockRect target, IntPtr order)
    {
        if (!GetWindowRect(_hwnd, out var raw)) return false;
        var visible = VisibleBounds(raw);
        return SetWindowPos(_hwnd, order, raw.Left + target.X - visible.X,
            raw.Top + target.Y - visible.Y, 0, 0, 0x0001 | 0x0010);
    }

    private DockRect VisibleBounds(Rect raw) =>
        DwmGetWindowAttribute(_hwnd, 9, out var frame, Marshal.SizeOf<Rect>()) == 0 ? frame.ToRect() : raw.ToRect();

    public void Dispose()
    {
        if (_disposed) return;
        _timer.Stop();
        _timer.Tick -= Tick;
        Reveal();
        _disposed = true;
    }

    [StructLayout(LayoutKind.Sequential)] private struct Point { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct Rect
    {
        public int Left, Top, Right, Bottom;
        public DockRect ToRect() => new(Left, Top, Right - Left, Bottom - Top);
    }
    [StructLayout(LayoutKind.Sequential)] private struct MonitorInfo { public int Size; public Rect Screen, Work; public uint Flags; }
    [StructLayout(LayoutKind.Sequential)] private struct Placement
    {
        public int Length, Flags, Show;
        public Point Min, Max;
        public Rect Normal;
    }
    [DllImport("user32.dll")] private static extern bool GetWindowPlacement(IntPtr hwnd, ref Placement placement);
    [DllImport("user32.dll")] private static extern bool SetWindowPlacement(IntPtr hwnd, ref Placement placement);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd, out Rect rect);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out Point point);
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromPoint(Point point, uint flags);
    [DllImport("user32.dll")] private static extern bool GetMonitorInfoW(IntPtr monitor, ref MonitorInfo info);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool IsZoomed(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int key);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern IntPtr GetWindowLongPtrW(IntPtr hwnd, int index);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hwnd, IntPtr order, int x, int y, int w, int h, uint flags);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr hwnd, uint attribute, out Rect rect, int size);
}
