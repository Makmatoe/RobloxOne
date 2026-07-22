namespace SessionDock.Services;

internal sealed class ShutdownExitWatchdog : IDisposable
{
    private const int Armed = 0;
    private const int Disarmed = 1;
    private const int Fired = 2;
    private readonly CancellationTokenSource _cancellation = new();
    private readonly Action _forceExit;
    private readonly Task _completion;
    private int _state = Armed;
    private int _disposed;

    internal ShutdownExitWatchdog(
        TimeSpan timeout,
        Action forceExit,
        Func<TimeSpan, CancellationToken, Task> delayAsync)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(
            timeout,
            TimeSpan.Zero);
        _forceExit = forceExit ?? throw new ArgumentNullException(nameof(forceExit));
        ArgumentNullException.ThrowIfNull(delayAsync);
        _completion = MonitorAsync(timeout, delayAsync);
    }

    internal static ShutdownExitWatchdog Start(TimeSpan timeout) =>
        new(
            timeout,
            static () =>
            {
                System.Diagnostics.Trace.WriteLine(
                    "SessionDock forced process exit after shutdown exceeded its deadline.");
                Environment.Exit(1);
            },
            static (delay, cancellationToken) =>
                Task.Delay(delay, cancellationToken));

    internal Task Completion => _completion;

    internal void Disarm()
    {
        if (Interlocked.CompareExchange(ref _state, Disarmed, Armed) != Armed)
            return;

        try
        {
            _cancellation.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // Completion won the race and disposed the cancellation source.
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        Disarm();
        if (_completion.IsCompleted)
        {
            _cancellation.Dispose();
            return;
        }

        _ = _completion.ContinueWith(
            _ => _cancellation.Dispose(),
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private async Task MonitorAsync(
        TimeSpan timeout,
        Func<TimeSpan, CancellationToken, Task> delayAsync)
    {
        try
        {
            await delayAsync(timeout, _cancellation.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (
            _cancellation.IsCancellationRequested)
        {
            return;
        }

        if (Interlocked.CompareExchange(ref _state, Fired, Armed) == Armed)
            _forceExit();
    }
}
