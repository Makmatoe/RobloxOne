using SessionDock.Services;

namespace SessionDock.Tests;

public sealed class ShutdownExitWatchdogTests
{
    [Fact]
    public async Task Deadline_FiresExactlyOnce()
    {
        var releaseDeadline = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var exitCount = 0;
        using var watchdog = new ShutdownExitWatchdog(
            TimeSpan.FromSeconds(2),
            () => Interlocked.Increment(ref exitCount),
            (_, _) => releaseDeadline.Task);

        releaseDeadline.SetResult();
        await watchdog.Completion;
        watchdog.Disarm();

        Assert.Equal(1, Volatile.Read(ref exitCount));
    }

    [Fact]
    public async Task DisarmBeforeDeadline_PreventsForcedExit()
    {
        var delayStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var exitCount = 0;
        using var watchdog = new ShutdownExitWatchdog(
            TimeSpan.FromSeconds(2),
            () => Interlocked.Increment(ref exitCount),
            async (_, cancellationToken) =>
            {
                delayStarted.SetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            });

        await delayStarted.Task;
        watchdog.Disarm();
        await watchdog.Completion;

        Assert.Equal(0, Volatile.Read(ref exitCount));
    }

    [Fact]
    public void Constructor_RejectsInvalidDeadline()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new ShutdownExitWatchdog(
                TimeSpan.Zero,
                () => { },
                (_, _) => Task.CompletedTask));
    }
}
