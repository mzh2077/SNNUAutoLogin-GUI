using System;

namespace SNNUAutoLogin.Services;

internal enum DockEdge { None, Left, Right, Top }

internal readonly record struct DockRect(int X, int Y, int Width, int Height)
{
    public int Right => X + Width;
    public int Bottom => Y + Height;
    public bool Contains(int x, int y) => x >= X && x < Right && y >= Y && y < Bottom;
}

internal static class EdgeDockGeometry
{
    public static DockRect Fit(DockRect window, DockRect work) => Clamp(window with
    {
        Width = Math.Clamp(window.Width, 1, Math.Max(1, work.Width)),
        Height = Math.Clamp(window.Height, 1, Math.Max(1, work.Height))
    }, work);

    public static DockEdge FindEdge(DockRect window, DockRect screen, DockRect work, int distance)
    {
        if (window.Width >= work.Width || window.Height >= work.Height) return DockEdge.None;
        if (work.X == screen.X && Math.Abs(window.X - work.X) <= distance) return DockEdge.Left;
        if (work.Right == screen.Right && Math.Abs(window.Right - work.Right) <= distance) return DockEdge.Right;
        if (work.Y == screen.Y && Math.Abs(window.Y - work.Y) <= distance) return DockEdge.Top;
        return DockEdge.None;
    }

    public static DockRect Hide(DockRect window, DockRect screen, DockEdge edge, int strip) => edge switch
    {
        DockEdge.Left => window with { X = screen.X - window.Width + strip },
        DockEdge.Right => window with { X = screen.Right - strip },
        DockEdge.Top => window with { Y = screen.Y - window.Height + strip },
        _ => window
    };

    public static DockRect Clamp(DockRect window, DockRect work) => window with
    {
        X = Math.Clamp(window.X, work.X, Math.Max(work.X, work.Right - window.Width)),
        Y = Math.Clamp(window.Y, work.Y, Math.Max(work.Y, work.Bottom - window.Height))
    };
}
