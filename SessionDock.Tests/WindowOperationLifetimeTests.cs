using System.Diagnostics;
using SessionDock.Services;

namespace SessionDock.Tests;

public sealed class WindowOperationLifetimeTests
{
    [Fact]
    public async Task BeginShutdown_CancelsAndDrainsTrackedOperation()
    {
        using var lifetime = new WindowOperationLifetime();
        var started = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var operation = lifetime.RunAsync(async cancellationToken =>
        {
            started.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        });
        await started.Task;

        Assert.True(lifetime.BeginShutdown());

        Assert.True(await lifetime.DrainAsync(TimeSpan.FromSeconds(1)));
        await operation;
        Assert.True(lifetime.Token.IsCancellationRequested);
    }

    [Fact]
    public async Task RunAsync_ShutdownDisposalRace_DoesNotEscapeAsyncHandler()
    {
        using var lifetime = new WindowOperationLifetime();
        var started = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var operation = lifetime.RunAsync(async cancellationToken =>
        {
            started.TrySetResult();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                throw new ObjectDisposedException("native-browser");
            }
        });
        await started.Task;

        lifetime.BeginShutdown();

        await operation;
        Assert.True(await lifetime.DrainAsync(TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public async Task DrainAsync_UnresponsiveOperation_ReturnsAtBound()
    {
        using var lifetime = new WindowOperationLifetime();
        var started = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var operation = lifetime.RunAsync(async _ =>
        {
            started.TrySetResult();
            await release.Task;
        });
        await started.Task;
        lifetime.BeginShutdown();
        var stopwatch = Stopwatch.StartNew();

        var drained = await lifetime.DrainAsync(TimeSpan.FromMilliseconds(50));

        stopwatch.Stop();
        Assert.False(drained);
        Assert.InRange(
            stopwatch.Elapsed,
            TimeSpan.FromMilliseconds(25),
            TimeSpan.FromSeconds(1));
        release.TrySetResult();
        await operation;
    }

    [Fact]
    public async Task RunAsync_AfterShutdown_DoesNotStartNewWork()
    {
        using var lifetime = new WindowOperationLifetime();
        lifetime.BeginShutdown();
        var invoked = false;

        await lifetime.RunAsync(_ =>
        {
            invoked = true;
            return Task.CompletedTask;
        });

        Assert.False(invoked);
    }

    [Fact]
    public void BeginShutdown_FaultingCancellationCallback_DoesNotEscape()
    {
        using var lifetime = new WindowOperationLifetime();
        using var registration = lifetime.Token.Register(
            () => throw new InvalidOperationException("callback failure"));

        Assert.True(lifetime.BeginShutdown());
        Assert.True(lifetime.Token.IsCancellationRequested);
    }

    [Fact]
    public async Task BeginShutdown_BlockingCancellationCallbackDoesNotBlockCaller()
    {
        using var lifetime = new WindowOperationLifetime();
        using var release = new ManualResetEventSlim();
        using var registration = lifetime.Token.Register(release.Wait);

        var beginShutdown = Task.Run(
            lifetime.BeginShutdown,
            TestContext.Current.CancellationToken);
        var winner = await Task.WhenAny(
            beginShutdown,
            Task.Delay(
                TimeSpan.FromMilliseconds(250),
                TestContext.Current.CancellationToken));

        release.Set();
        Assert.Same(beginShutdown, winner);
        Assert.True(await beginShutdown.WaitAsync(
            TimeSpan.FromSeconds(1),
            TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task RunAsync_NonShutdownFailure_RemainsObservable()
    {
        using var lifetime = new WindowOperationLifetime();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            lifetime.RunAsync(_ => throw new InvalidOperationException("failure")));
    }

    [Fact]
    public async Task RunAsync_IOException_IsDeliveredToOperationalErrorBoundary()
    {
        using var lifetime = new WindowOperationLifetime();
        Exception? handled = null;

        await lifetime.RunAsync(
            _ => throw new IOException("disk unavailable"),
            exception => handled = exception);

        Assert.IsType<IOException>(handled);
    }

    [Fact]
    public async Task RunAsync_UnauthorizedAccess_IsDeliveredToOperationalErrorBoundary()
    {
        using var lifetime = new WindowOperationLifetime();
        Exception? handled = null;

        await lifetime.RunAsync(
            _ => throw new UnauthorizedAccessException("write denied"),
            exception => handled = exception);

        Assert.IsType<UnauthorizedAccessException>(handled);
    }

    [Fact]
    public async Task RunAsync_UnexpectedFailure_BypassesOperationalErrorBoundary()
    {
        using var lifetime = new WindowOperationLifetime();
        var handled = false;

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            lifetime.RunAsync(
                _ => throw new InvalidOperationException("programmer failure"),
                _ => handled = true));

        Assert.False(handled);
    }

    [Fact]
    public async Task RunAsync_UnexpectedFailureDuringShutdown_RemainsObservable()
    {
        using var lifetime = new WindowOperationLifetime();
        var started = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var operation = lifetime.RunAsync(async _ =>
        {
            started.TrySetResult();
            await release.Task;
            throw new InvalidOperationException("programmer failure");
        });
        await started.Task;
        lifetime.BeginShutdown();
        release.TrySetResult();

        await Assert.ThrowsAsync<InvalidOperationException>(() => operation);
    }

    [Fact]
    public async Task RunAsync_CustomExpectedFailureFilter_HandlesTypedWebFailure()
    {
        using var lifetime = new WindowOperationLifetime();
        Exception? handled = null;

        await lifetime.RunAsync(
            _ => throw new WebSessionUnavailableException(
                WebSessionUnavailableReason.ProcessExited,
                "browser exited"),
            WebSessionException.IsExpectedLifecycleFailure,
            exception => handled = exception);

        Assert.IsType<WebSessionUnavailableException>(handled);
    }
}
