namespace SessionDock.Services;

internal sealed class WindowOperationLifetime : IDisposable
{
    private readonly object _sync = new();
    private readonly CancellationTokenSource _shutdownCancellation = new();
    private readonly HashSet<OperationRegistration> _operations = [];
    private bool _isShuttingDown;
    private bool _disposed;

    public WindowOperationLifetime()
    {
        Token = _shutdownCancellation.Token;
    }

    public CancellationToken Token { get; }

    public bool IsShuttingDown
    {
        get
        {
            lock (_sync)
                return _isShuttingDown;
        }
    }

    public async Task RunAsync(Func<CancellationToken, Task> operation)
    {
        await RunCoreAsync(operation, expectedFailureHandler: null);
    }

    public async Task RunAsync(
        Func<CancellationToken, Task> operation,
        Action<Exception> expectedFailureHandler)
    {
        ArgumentNullException.ThrowIfNull(expectedFailureHandler);
        await RunCoreAsync(operation, expectedFailureHandler);
    }

    private async Task RunCoreAsync(
        Func<CancellationToken, Task> operation,
        Action<Exception>? expectedFailureHandler)
    {
        ArgumentNullException.ThrowIfNull(operation);
        var registration = TryRegister();
        if (registration is null)
            return;

        using (registration)
        {
            try
            {
                await operation(Token);
            }
            catch (OperationCanceledException) when (IsShuttingDown)
            {
                // Window shutdown owns this cancellation.
            }
            catch (Exception) when (IsShuttingDown)
            {
                // A bounded shutdown can release a native dependency after an
                // operation was asked to stop. Native wrappers do not consistently
                // report that race as ObjectDisposedException.
            }
            catch (Exception exception) when (
                expectedFailureHandler is not null &&
                LocalDataException.IsExpectedPersistenceFailure(exception))
            {
                expectedFailureHandler(exception);
            }
        }
    }

    public bool BeginShutdown()
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_isShuttingDown)
                return false;
            _isShuttingDown = true;
        }

        CancelWithoutEscapingCallbacks();
        return true;
    }

    public async Task<bool> DrainAsync(TimeSpan timeout)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(
            timeout,
            TimeSpan.Zero);

        Task[] activeOperations;
        lock (_sync)
        {
            if (!_isShuttingDown)
            {
                throw new InvalidOperationException(
                    "Shutdown must begin before operations can be drained.");
            }
            activeOperations = _operations
                .Select(registration => registration.Completion)
                .ToArray();
        }

        if (activeOperations.Length == 0)
            return true;

        try
        {
            await Task.WhenAll(activeOperations).WaitAsync(timeout);
            return true;
        }
        catch (TimeoutException)
        {
            return false;
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed)
                return;
            _disposed = true;
            _isShuttingDown = true;
        }

        CancelWithoutEscapingCallbacks();
        _shutdownCancellation.Dispose();
    }

    private OperationRegistration? TryRegister()
    {
        lock (_sync)
        {
            if (_disposed || _isShuttingDown)
                return null;
            var registration = new OperationRegistration(this);
            _operations.Add(registration);
            return registration;
        }
    }

    private void Complete(OperationRegistration registration)
    {
        lock (_sync)
            _operations.Remove(registration);
        registration.SignalCompletion();
    }

    private void CancelWithoutEscapingCallbacks()
    {
        try
        {
            _shutdownCancellation.Cancel();
        }
        catch (Exception ex) when (
            ex is AggregateException or ObjectDisposedException)
        {
            // Cancellation callback failures cannot be allowed to strand shutdown.
        }
    }

    private sealed class OperationRegistration(WindowOperationLifetime owner) :
        IDisposable
    {
        private readonly TaskCompletionSource _completion = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private int _disposed;

        public Task Completion => _completion.Task;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
                owner.Complete(this);
        }

        public void SignalCompletion() => _completion.TrySetResult();
    }
}
