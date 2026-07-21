using SessionDock.SystemProcesses;

namespace SessionDock.Tests;

public sealed class CompositeLaunchHookTests
{
    [Fact]
    public async Task NotifyLaunchAsync_FaultingHook_DoesNotBlockOtherHooks()
    {
        var faulting = new StubLaunchHook
        {
            Notify = (_, _) => throw new InvalidOperationException("optional failure")
        };
        var delivered = 0;
        var healthy = new StubLaunchHook
        {
            Notify = (_, _) =>
            {
                Interlocked.Increment(ref delivered);
                return Task.CompletedTask;
            }
        };
        using var composite = new CompositeLaunchHook(faulting, healthy);

        await composite.NotifyLaunchAsync(
            CreateEvent(),
            TestContext.Current.CancellationToken);

        Assert.Equal(1, Volatile.Read(ref delivered));
    }

    [Fact]
    public async Task NotifyLaunchAsync_CanceledOptionalHook_DoesNotEscape()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var hook = new StubLaunchHook
        {
            Notify = (_, token) => Task.FromCanceled(token)
        };
        using var composite = new CompositeLaunchHook(hook);

        await composite.NotifyLaunchAsync(CreateEvent(), cancellation.Token);
    }

    [Fact]
    public void Dispose_FaultingHook_DoesNotBlockOtherHooks()
    {
        var faulting = new StubLaunchHook
        {
            DisposeAction = () => throw new InvalidOperationException("optional failure")
        };
        var disposed = false;
        var healthy = new StubLaunchHook
        {
            DisposeAction = () => disposed = true
        };
        var composite = new CompositeLaunchHook(faulting, healthy);

        composite.Dispose();

        Assert.True(disposed);
    }

    private static LaunchHookEvent CreateEvent() =>
        new(
            "event-id",
            DateTimeOffset.UnixEpoch,
            123,
            456,
            "Experience",
            false,
            789,
            "builder",
            null);

    private sealed class StubLaunchHook : ILaunchHook
    {
        public Func<LaunchHookEvent, CancellationToken, Task>? Notify { get; init; }

        public Action? DisposeAction { get; init; }

        public bool IsConfigured { get; init; } = true;

        public Task NotifyLaunchAsync(
            LaunchHookEvent launchEvent,
            CancellationToken cancellationToken = default) =>
            Notify?.Invoke(launchEvent, cancellationToken) ?? Task.CompletedTask;

        public void Dispose() => DisposeAction?.Invoke();
    }
}
