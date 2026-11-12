using System;
using System.Threading;
using System.Threading.Tasks;

namespace SNNUAutoLogin.Services;

internal sealed class OperationLifetime
{
    private readonly object _gate = new();
    private readonly CancellationTokenSource _shutdown = new();
    private readonly TaskCompletionSource _drained = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Task? _stop;
    private int _active;

    public Lease? TryEnter(CancellationToken caller = default)
    {
        lock (_gate)
        {
            if (_stop is not null) return null;
            var linked = caller.CanBeCanceled
                ? CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token, caller)
                : null;
            _active++;
            return new Lease(this, linked, linked?.Token ?? _shutdown.Token);
        }
    }

    public Task StopAsync()
    {
        lock (_gate)
        {
            if (_stop is not null) return _stop;
            if (_active == 0) _drained.TrySetResult();
            return _stop = StopCoreAsync();
        }
    }

    private async Task StopCoreAsync()
    {
        await _shutdown.CancelAsync().ConfigureAwait(false);
        await _drained.Task.ConfigureAwait(false);
        _shutdown.Dispose();
    }

    private void Leave()
    {
        lock (_gate)
        {
            if (--_active == 0 && _stop is not null) _drained.TrySetResult();
        }
    }

    public sealed class Lease : IDisposable
    {
        private readonly OperationLifetime _owner;
        private readonly CancellationTokenSource? _linked;
        public CancellationToken Token { get; }

        internal Lease(OperationLifetime owner, CancellationTokenSource? linked, CancellationToken token)
        {
            _owner = owner;
            _linked = linked;
            Token = token;
        }

        public void Dispose()
        {
            _linked?.Dispose();
            _owner.Leave();
        }
    }
}
