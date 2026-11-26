using System;
using System.Threading;
using System.Threading.Tasks;

namespace SNNUAutoLogin.Services;

internal enum DeviceActionOutcome { Succeeded, Failed, TimedOut, Busy, Cancelled }

/// <summary>Limits UI waiting while retaining ownership of late requests until they finish.</summary>
internal sealed class TimedDeviceAction : IDisposable
{
    private readonly object _gate = new();
    private readonly TimeSpan _timeout;
    private CancellationTokenSource? _active;
    private bool _disposed;

    public TimedDeviceAction(TimeSpan? timeout = null) => _timeout = timeout ?? TimeSpan.FromSeconds(8);

    public async Task<DeviceActionOutcome> RunAsync(Func<CancellationToken, Task<bool>> action)
    {
        CancellationTokenSource deadline;
        lock (_gate)
        {
            if (_disposed) return DeviceActionOutcome.Cancelled;
            if (_active is not null) return DeviceActionOutcome.Busy;
            _active = deadline = new CancellationTokenSource(_timeout);
        }
        Task<bool> pending = InvokeAsync(action, deadline.Token);
        try
        {
            bool success = await pending.WaitAsync(deadline.Token);
            return success ? DeviceActionOutcome.Succeeded
                : deadline.IsCancellationRequested ? CancelledOutcome() : DeviceActionOutcome.Failed;
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested) { return CancelledOutcome(); }
        catch { return DeviceActionOutcome.Failed; }
        finally { _ = FinishAsync(pending, deadline); }
    }

    private DeviceActionOutcome CancelledOutcome()
    {
        lock (_gate) return _disposed ? DeviceActionOutcome.Cancelled : DeviceActionOutcome.TimedOut;
    }

    private static async Task<bool> InvokeAsync(Func<CancellationToken, Task<bool>> action, CancellationToken token)
        => await action(token).ConfigureAwait(false);

    private async Task FinishAsync(Task<bool> pending, CancellationTokenSource deadline)
    {
        try { await pending.ConfigureAwait(false); }
        catch { /* The UI reports failure or timeout; late exceptions must still be observed. */ }
        finally
        {
            lock (_gate)
            {
                if (ReferenceEquals(_active, deadline)) _active = null;
                deadline.Dispose();
            }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _active?.Cancel();
        }
    }
}
