using System.Collections.Concurrent;
using SessionDock.Services;

namespace SessionDock.Tests;

public sealed class DestinationPersistenceDebouncerTests
{
    [Fact]
    public async Task ScheduleAsync_RapidChangeCommitsOnlyNewestRequest()
    {
        var delays = new ConcurrentQueue<TaskCompletionSource>();
        var committed = new ConcurrentQueue<DestinationPersistenceRequest>();
        var debouncer = new DestinationPersistenceDebouncer(
            TimeSpan.FromSeconds(1),
            request =>
            {
                committed.Enqueue(request);
                return Task.FromResult(true);
            },
            (_, cancellationToken) =>
            {
                var delay = new TaskCompletionSource(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                delays.Enqueue(delay);
                return delay.Task.WaitAsync(cancellationToken);
            });
        var firstRequest = new DestinationPersistenceRequest("one", 1, 1, "111");
        var secondRequest = new DestinationPersistenceRequest("two", 2, 1, "222");

        var first = debouncer.ScheduleAsync(firstRequest);
        Assert.True(delays.TryDequeue(out _));
        var second = debouncer.ScheduleAsync(secondRequest);
        Assert.True(delays.TryDequeue(out var secondDelay));
        secondDelay.SetResult();

        await Task.WhenAll(first, second);
        Assert.Equal([secondRequest], committed);
    }

    [Fact]
    public async Task FlushAsync_CancelsDelayAndCommitsImmediately()
    {
        var committed = new ConcurrentQueue<DestinationPersistenceRequest>();
        var debouncer = new DestinationPersistenceDebouncer(
            TimeSpan.FromSeconds(1),
            request =>
            {
                committed.Enqueue(request);
                return Task.FromResult(true);
            },
            (_, cancellationToken) =>
                Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken));
        var delayedRequest = new DestinationPersistenceRequest("one", 1, 1, "111");
        var finalRequest = new DestinationPersistenceRequest("one", 1, 2, "222");

        var delayed = debouncer.ScheduleAsync(delayedRequest);
        await debouncer.FlushAsync(finalRequest);
        await delayed;

        Assert.Equal([finalRequest], committed);
    }

    [Fact]
    public async Task Dispose_CancelsPendingRequestWithoutFaultingIt()
    {
        var debouncer = new DestinationPersistenceDebouncer(
            TimeSpan.FromSeconds(1),
            _ => Task.FromResult(true),
            (_, cancellationToken) =>
                Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken));

        var pending = debouncer.ScheduleAsync(
            new DestinationPersistenceRequest("one", 1, 1, "111"));
        debouncer.Dispose();

        Assert.False(await pending);
    }

    [Fact]
    public async Task Cancel_DoesNotReleaseCancellationSourceWhileCallbackRuns()
    {
        using var cancelEntered = new ManualResetEventSlim();
        using var releaseCancel = new ManualResetEventSlim();
        var delay = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationTokenRegistration registration = default;
        var debouncer = new DestinationPersistenceDebouncer(
            TimeSpan.FromSeconds(1),
            _ => Task.FromResult(true),
            (_, cancellationToken) =>
            {
                registration = cancellationToken.Register(() =>
                {
                    cancelEntered.Set();
                    releaseCancel.Wait();
                });
                return delay.Task;
            });
        var pending = debouncer.ScheduleAsync(
            new DestinationPersistenceRequest("one", 1, 1, "111"));
        var cancel = Task.Run(
            debouncer.Cancel,
            TestContext.Current.CancellationToken);

        try
        {
            Assert.True(cancelEntered.Wait(
                TimeSpan.FromSeconds(2),
                TestContext.Current.CancellationToken));
            delay.TrySetResult();
            var earlyCompletion = await Task.WhenAny(
                pending,
                Task.Delay(
                    TimeSpan.FromMilliseconds(100),
                    TestContext.Current.CancellationToken));
            Assert.NotSame(pending, earlyCompletion);
        }
        finally
        {
            delay.TrySetResult();
            releaseCancel.Set();
        }
        await cancel;
        Assert.False(await pending);
        registration.Dispose();
        debouncer.Dispose();
    }

    [Fact]
    public async Task ScheduleAsync_AfterDispose_IsRejected()
    {
        var debouncer = new DestinationPersistenceDebouncer(
            TimeSpan.FromSeconds(1),
            _ => Task.FromResult(true));
        debouncer.Dispose();

        await Assert.ThrowsAsync<ObjectDisposedException>(() =>
            debouncer.ScheduleAsync(
                new DestinationPersistenceRequest("one", 1, 1, "111")));
    }

    [Fact]
    public async Task FlushAsync_AfterDispose_IsRejected()
    {
        var debouncer = new DestinationPersistenceDebouncer(
            TimeSpan.FromSeconds(1),
            _ => Task.FromResult(true));
        debouncer.Dispose();

        await Assert.ThrowsAsync<ObjectDisposedException>(() =>
            debouncer.FlushAsync(
                new DestinationPersistenceRequest("one", 1, 1, "111")));
    }
}
