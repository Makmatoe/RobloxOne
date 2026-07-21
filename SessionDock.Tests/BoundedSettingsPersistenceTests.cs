using System.Diagnostics;
using SessionDock.Services;

namespace SessionDock.Tests;

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
}
