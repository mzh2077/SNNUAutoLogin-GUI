using System.Diagnostics;
using SNNUAutoLogin.Services;

static void Check(bool ok, string label)
{
    if (!ok) throw new Exception(label);
    Console.WriteLine("PASS " + label);
}

static async Task<DeviceActionOutcome> NextRequestAsync(TimedDeviceAction action)
{
    DeviceActionOutcome result = DeviceActionOutcome.Busy;
    for (int i = 0; i < 100 && result == DeviceActionOutcome.Busy; i++)
    {
        await Task.Delay(5);
        result = await action.RunAsync(_ => Task.FromResult(true));
    }
    return result;
}

var screen = new DockRect(-1920, 0, 1920, 1080);
var work = screen with { Height = 1040 };
var left = new DockRect(-1916, 100, 480, 640);
Check(EdgeDockGeometry.FindEdge(left, screen, work, 12) == DockEdge.Left, "negative-coordinate monitor left edge");
var hidden = EdgeDockGeometry.Hide(left, screen, DockEdge.Left, 4);
Check(hidden.Right == screen.X + 4 && hidden.Y == left.Y, "left collapse leaves four-pixel reveal strip");
var right = new DockRect(-480, 100, 480, 640);
Check(EdgeDockGeometry.FindEdge(right, screen, work, 12) == DockEdge.Right, "right edge detected");
Check(EdgeDockGeometry.Hide(right, screen, DockEdge.Right, 8).X == -8, "scaled strip stays on its monitor");
var top = new DockRect(-1400, 5, 480, 640);
Check(EdgeDockGeometry.FindEdge(top, screen, work, 12) == DockEdge.Top, "top edge detected");
Check(EdgeDockGeometry.Hide(top, screen, DockEdge.Top, 4).Bottom == 4, "top strip remains reachable");
Check(EdgeDockGeometry.FindEdge(top with { Y = 400 }, screen, work, 12) == DockEdge.None, "bottom taskbar is not a docking target");
Check(EdgeDockGeometry.FindEdge(left, screen, work with { X = -1880, Width = 1880 }, 12) == DockEdge.None, "vertical taskbar edge excluded");
Check(EdgeDockGeometry.FindEdge(work, screen, work, 12) == DockEdge.None, "full work-area window never docks");
Check(EdgeDockGeometry.Clamp(left, new(0, 0, 1600, 900)).X == 0, "display removal restores window onto surviving display");
Check(EdgeDockGeometry.Clamp(new(-200, -100, 1800, 1000), new(0, 0, 1600, 900)).X == 0, "oversized window restoration does not throw");
Check(EdgeDockGeometry.Fit(new(1800, 900, 1200, 1000), new(0, 0, 1024, 728)) == new DockRect(0, 0, 1024, 728),
    "resolution reduction fits both window position and size");
Check(EdgeDockGeometry.Fit(new(2000, 200, 780, 1020), new(-1280, 0, 1280, 680)) == new DockRect(-780, 0, 780, 680),
    "display removal fits the window onto a negative-coordinate monitor");
Check(EdgeDockGeometry.Fit(new(100, 100, 520, 680), new(0, 0, 1920, 1040)) == new DockRect(100, 100, 520, 680),
    "display refresh preserves a window that already fits");

using (var action = new TimedDeviceAction(TimeSpan.FromMilliseconds(80)))
{
    var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var watch = Stopwatch.StartNew();
    var result = await action.RunAsync(async ct =>
    {
        try { await Task.Delay(Timeout.Infinite, ct); return true; }
        finally { cancelled.TrySetResult(); }
    });
    Check(result == DeviceActionOutcome.TimedOut && watch.Elapsed < TimeSpan.FromSeconds(2), "unresponsive device request releases UI at deadline");
    await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(2));
    Check(await NextRequestAsync(action) == DeviceActionOutcome.Succeeded, "cooperative cancellation allows the next request");
}

using (var action = new TimedDeviceAction(TimeSpan.FromMilliseconds(60)))
{
    var late = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
    Check(await action.RunAsync(_ => late.Task) == DeviceActionOutcome.TimedOut, "ignoring cancellation still cannot hold the UI");
    int duplicate = 0;
    Check(await action.RunAsync(_ => { duplicate++; return Task.FromResult(true); }) == DeviceActionOutcome.Busy
        && duplicate == 0, "late request retains ownership and blocks duplicate side effects");
    late.SetException(new IOException("late portal error"));
    DeviceActionOutcome next = DeviceActionOutcome.Busy;
    for (int i = 0; i < 100 && next == DeviceActionOutcome.Busy; i++)
    {
        await Task.Delay(5);
        next = await action.RunAsync(_ => Task.FromResult(true));
    }
    Check(next == DeviceActionOutcome.Succeeded, "late exception is observed and request ownership released");
}

using (var action = new TimedDeviceAction(TimeSpan.FromSeconds(5)))
{
    var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var operation = action.RunAsync(async ct => { entered.SetResult(); await Task.Delay(Timeout.Infinite, ct); return true; });
    await entered.Task;
    action.Dispose();
    Check(await operation.WaitAsync(TimeSpan.FromSeconds(2)) == DeviceActionOutcome.Cancelled, "window exit cancels its outstanding device action");
    Check(await action.RunAsync(_ => Task.FromResult(true)) == DeviceActionOutcome.Cancelled, "closed window accepts no new actions");
}
