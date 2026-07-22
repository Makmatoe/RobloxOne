namespace SessionDock.Services;

internal sealed record DestinationPersistenceRequest(
    string AccountKey,
    long OwnerEpoch,
    long Revision,
    string? Destination);

internal sealed class DestinationPersistenceDebouncer : IDisposable
{
    private readonly Func<TimeSpan, CancellationToken, Task> _delayAsync;
    private readonly TimeSpan _delay;
    private readonly Func<DestinationPersistenceRequest, Task<bool>> _persistAsync;
    private readonly object _stateLock = new();
    private ScheduledCancellation? _pendingCancellation;
    private bool _disposed;

    internal DestinationPersistenceDebouncer(
        TimeSpan delay,
        Func<DestinationPersistenceRequest, Task<bool>> persistAsync)
        : this(delay, persistAsync, Task.Delay)
    {
    }

    internal DestinationPersistenceDebouncer(
        TimeSpan delay,
        Func<DestinationPersistenceRequest, Task<bool>> persistAsync,
        Func<TimeSpan, CancellationToken, Task> delayAsync)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(
            delay,
            TimeSpan.Zero);
        _delay = delay;
        _persistAsync = persistAsync ??
            throw new ArgumentNullException(nameof(persistAsync));
        _delayAsync = delayAsync ??
            throw new ArgumentNullException(nameof(delayAsync));
    }

    internal Task<bool> ScheduleAsync(DestinationPersistenceRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ScheduledCancellation cancellation;
        CancellationToken cancellationToken;
        ScheduledCancellation? previous;
        lock (_stateLock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            cancellation = new ScheduledCancellation();
            cancellationToken = cancellation.Token;
            previous = _pendingCancellation;
            _pendingCancellation = cancellation;
        }
        previous?.Cancel();
        return RunScheduledAsync(
            request,
            cancellation,
            cancellationToken);
    }

    internal Task<bool> FlushAsync(DestinationPersistenceRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        lock (_stateLock)
            ObjectDisposedException.ThrowIf(_disposed, this);
        Cancel();
        return _persistAsync(request);
    }

    internal void Cancel()
    {
        ScheduledCancellation? pending;
        lock (_stateLock)
        {
            pending = _pendingCancellation;
            _pendingCancellation = null;
        }
        pending?.Cancel();
    }

    public void Dispose()
    {
        lock (_stateLock)
        {
            if (_disposed)
                return;
            _disposed = true;
        }
        Cancel();
    }

    private async Task<bool> RunScheduledAsync(
        DestinationPersistenceRequest request,
        ScheduledCancellation cancellation,
        CancellationToken cancellationToken)
    {
        try
        {
            await _delayAsync(_delay, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            return await _persistAsync(request);
        }
        catch (OperationCanceledException) when (
            cancellationToken.IsCancellationRequested)
        {
            // A newer edit or an explicit flush superseded this request.
            return false;
        }
        finally
        {
            lock (_stateLock)
            {
                if (ReferenceEquals(_pendingCancellation, cancellation))
                    _pendingCancellation = null;
            }
            await cancellation.DisposeWhenIdleAsync();
        }
    }

    private sealed class ScheduledCancellation
    {
        private readonly object _sync = new();
        private readonly CancellationTokenSource _source = new();
        private TaskCompletionSource? _cancelCompletion;
        private bool _cancelRequested;
        private bool _cancelInProgress;
        private bool _ownerFinished;

        internal CancellationToken Token => _source.Token;

        internal void Cancel()
        {
            TaskCompletionSource completion;
            lock (_sync)
            {
                if (_ownerFinished || _cancelRequested)
                    return;
                _cancelRequested = true;
                _cancelInProgress = true;
                completion = new TaskCompletionSource(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                _cancelCompletion = completion;
            }

            try
            {
                _source.Cancel();
            }
            finally
            {
                lock (_sync)
                    _cancelInProgress = false;
                completion.TrySetResult();
            }
        }

        internal async Task DisposeWhenIdleAsync()
        {
            Task? cancellationCompleted;
            lock (_sync)
            {
                _ownerFinished = true;
                cancellationCompleted = _cancelInProgress
                    ? _cancelCompletion!.Task
                    : null;
            }

            if (cancellationCompleted is not null)
                await cancellationCompleted.ConfigureAwait(false);
            _source.Dispose();
        }
    }
}
