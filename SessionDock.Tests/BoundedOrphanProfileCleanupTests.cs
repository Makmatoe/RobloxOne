using SessionDock.Services;

namespace SessionDock.Tests;

[Collection<TimingSensitiveTestCollection>]
public sealed class BoundedOrphanProfileCleanupTests
{
    [Fact]
    public async Task RunAsync_UncooperativeCleanupReturnsAtOwnedBudgetDeadline()
    {
        var testGuardTimeout = TimeSpan.FromSeconds(10);
        var budgetCancellation = new CancellationTokenSource();
        var cleanup = new BoundedOrphanProfileCleanup(
            _ => budgetCancellation);
        var cleanupStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        using var allowLateCompletion = new ManualResetEventSlim();
        var lateCompletion = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);

        var cleanupTask = cleanup.RunAsync(
            _ =>
            {
                cleanupStarted.TrySetResult();
                allowLateCompletion.Wait();
                lateCompletion.TrySetResult();
                return 1;
            },
            TimeSpan.FromMinutes(1),
            TestContext.Current.CancellationToken);
        await cleanupStarted.Task.WaitAsync(
            testGuardTimeout,
            TestContext.Current.CancellationToken);

        budgetCancellation.Cancel();
        try
        {
            var result = await cleanupTask.WaitAsync(
                testGuardTimeout,
                TestContext.Current.CancellationToken);

            Assert.Equal(0, result.RemovedProfiles);
            Assert.True(result.BudgetExpired);
            Assert.False(lateCompletion.Task.IsCompleted);
        }
        finally
        {
            allowLateCompletion.Set();
        }

        await lateCompletion.Task.WaitAsync(
            testGuardTimeout,
            TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task RunAsync_UncooperativeCleanupPropagatesCallerCancellationPromptly()
    {
        var testGuardTimeout = TimeSpan.FromSeconds(10);
        var cleanup = new BoundedOrphanProfileCleanup();
        using var callerCancellation = new CancellationTokenSource();
        var cleanupStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        using var allowLateCompletion = new ManualResetEventSlim();
        var lateCompletion = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);

        var cleanupTask = cleanup.RunAsync(
            _ =>
            {
                cleanupStarted.TrySetResult();
                allowLateCompletion.Wait();
                lateCompletion.TrySetResult();
                return 1;
            },
            TimeSpan.FromMinutes(1),
            callerCancellation.Token);
        await cleanupStarted.Task.WaitAsync(
            testGuardTimeout,
            TestContext.Current.CancellationToken);

        callerCancellation.Cancel();
        try
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                async () => await cleanupTask.WaitAsync(
                    testGuardTimeout,
                    TestContext.Current.CancellationToken));
        }
        finally
        {
            allowLateCompletion.Set();
        }

        await lateCompletion.Task.WaitAsync(
            testGuardTimeout,
            TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task RunAsync_UnexpectedCleanupFaultPropagates()
    {
        var cleanup = new BoundedOrphanProfileCleanup();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => cleanup.RunAsync(
                _ => throw new InvalidOperationException("test fault"),
                TimeSpan.FromSeconds(1),
                TestContext.Current.CancellationToken));

        Assert.Equal("test fault", exception.Message);
    }
}
