using System.Diagnostics;
using SessionDock.Services;

namespace SessionDock.Tests;

[Collection<TimingSensitiveTestCollection>]
public sealed class BoundedSettingsPersistenceTests
{
    [Fact]
    public async Task TrySaveAsync_IOException_ReturnsFalse()
    {
        var saved = await BoundedSettingsPersistence.TrySaveAsync(
            () => throw new IOException("disk unavailable"),
            TimeSpan.FromSeconds(1));

        Assert.False(saved);
    }

    [Fact]
    public async Task TrySaveAsync_UnexpectedFailure_RemainsObservable()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            BoundedSettingsPersistence.TrySaveAsync(
                () => throw new InvalidOperationException("programmer failure"),
                TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public async Task TrySaveAsync_UnresponsiveWrite_ReturnsAtDeadline()
    {
        using var release = new ManualResetEventSlim();
        var started = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var stopwatch = Stopwatch.StartNew();

        var saved = await BoundedSettingsPersistence.TrySaveAsync(
            () =>
            {
                started.TrySetResult();
                release.Wait();
            },
            TimeSpan.FromMilliseconds(75));

        stopwatch.Stop();
        Assert.False(saved);
        Assert.True(started.Task.IsCompleted);
        Assert.InRange(
            stopwatch.Elapsed,
            TimeSpan.FromMilliseconds(25),
            TimeSpan.FromSeconds(1));
        release.Set();
    }

    [Fact]
    public async Task TrySaveAsync_ActiveSerializedWrite_BoundsNewerShutdownSave()
    {
        using var releaseFirst = new ManualResetEventSlim();
        var firstStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var attempts = 0;
        var writer = new SerializedSettingsWriter(_ =>
        {
            if (Interlocked.Increment(ref attempts) != 1)
                return;
            firstStarted.TrySetResult();
            releaseFirst.Wait();
        });
        var activeWrite = writer.SaveAsync(new());
        await firstStarted.Task;
        var stopwatch = Stopwatch.StartNew();

        var saved = await BoundedSettingsPersistence.TrySaveAsync(
            () => writer.SaveAsync(new()),
            TimeSpan.FromMilliseconds(75));

        stopwatch.Stop();
        Assert.False(saved);
        Assert.InRange(
            stopwatch.Elapsed,
            TimeSpan.FromMilliseconds(25),
            TimeSpan.FromSeconds(1));
        Assert.Equal(1, Volatile.Read(ref attempts));
        releaseFirst.Set();
        await activeWrite;
    }

    [Fact]
    public async Task TrySaveAsync_SynchronousAsyncDelegateRunsOffCallerThread()
    {
        var callerThreadId = 0;
        var saveThreadId = 0;

        var saved = await Task.Factory.StartNew(
            () =>
            {
                callerThreadId = Environment.CurrentManagedThreadId;
                return BoundedSettingsPersistence.TrySaveAsync(
                        () =>
                        {
                            saveThreadId = Environment.CurrentManagedThreadId;
                            return Task.CompletedTask;
                        },
                        TimeSpan.FromSeconds(10))
                    .GetAwaiter()
                    .GetResult();
            },
            CancellationToken.None,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default);

        Assert.True(saved);
        Assert.NotEqual(callerThreadId, saveThreadId);
    }
}
