using SessionDock.Models;
using SessionDock.Services;

namespace SessionDock.Tests;

public sealed class PendingProfileDeletionReplayTests : IDisposable
{
    private readonly string _storageDirectory = Path.Combine(
        Path.GetTempPath(),
        $"SessionDock-profile-replay-{Guid.NewGuid():N}");

    [Fact]
    public async Task ReplayAsync_InvokesSequentiallyAndReturnsOnlyDeletedKeys()
    {
        var replay = new PendingProfileDeletionReplay();
        var invocationOrder = new List<string>();
        var concurrentInvocations = 0;
        var maximumConcurrency = 0;

        var result = await replay.ReplayAsync(
            ["first", "locked", "third"],
            async (accountKey, cancellationToken) =>
            {
                invocationOrder.Add(accountKey);
                var concurrency = Interlocked.Increment(
                    ref concurrentInvocations);
                maximumConcurrency = Math.Max(
                    maximumConcurrency,
                    concurrency);
                try
                {
                    await Task.Yield();
                    cancellationToken.ThrowIfCancellationRequested();
                    return accountKey != "locked";
                }
                finally
                {
                    Interlocked.Decrement(ref concurrentInvocations);
                }
            },
            TimeSpan.FromMinutes(1),
            TestContext.Current.CancellationToken);

        Assert.Equal(["first", "locked", "third"], invocationOrder);
        Assert.Equal(1, maximumConcurrency);
        Assert.Equal(["first", "third"], result.DeletedKeys);
        Assert.False(result.BudgetExpired);
    }

    [Fact]
    public async Task ReplayAsync_OwnBudgetCancellationReturnsCompletedPrefix()
    {
        var budgetCancellation = new CancellationTokenSource();
        var replay = new PendingProfileDeletionReplay(
            _ => budgetCancellation);
        var blockedDeletionStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var attemptedKeys = new List<string>();

        var replayTask = replay.ReplayAsync(
            ["deleted", "blocked", "unattempted"],
            async (accountKey, cancellationToken) =>
            {
                attemptedKeys.Add(accountKey);
                if (accountKey == "deleted")
                    return true;

                blockedDeletionStarted.TrySetResult();
                await Task.Delay(
                    Timeout.InfiniteTimeSpan,
                    cancellationToken);
                return true;
            },
            TimeSpan.FromMinutes(1),
            TestContext.Current.CancellationToken);
        await blockedDeletionStarted.Task.WaitAsync(
            TimeSpan.FromSeconds(1),
            TestContext.Current.CancellationToken);

        budgetCancellation.Cancel();
        var result = await replayTask.WaitAsync(
            TimeSpan.FromSeconds(1),
            TestContext.Current.CancellationToken);

        Assert.Equal(["deleted", "blocked"], attemptedKeys);
        Assert.Equal(["deleted"], result.DeletedKeys);
        Assert.True(result.BudgetExpired);
    }

    [Fact]
    public async Task ReplayAsync_UncooperativeDeletionReturnsAtOwnedBudgetDeadline()
    {
        var budgetCancellation = new CancellationTokenSource();
        var replay = new PendingProfileDeletionReplay(
            _ => budgetCancellation);
        var deletionStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var allowLateCompletion = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        var replayTask = replay.ReplayAsync(
            ["blocked"],
            (_, _) =>
            {
                deletionStarted.TrySetResult();
                return allowLateCompletion.Task;
            },
            TimeSpan.FromMinutes(1),
            TestContext.Current.CancellationToken);
        await deletionStarted.Task.WaitAsync(
            TimeSpan.FromSeconds(1),
            TestContext.Current.CancellationToken);

        budgetCancellation.Cancel();
        try
        {
            var result = await replayTask.WaitAsync(
                TimeSpan.FromSeconds(1),
                TestContext.Current.CancellationToken);

            Assert.Empty(result.DeletedKeys);
            Assert.True(result.BudgetExpired);
        }
        finally
        {
            allowLateCompletion.TrySetResult(true);
        }
    }

    [Fact]
    public async Task ReplayAsync_SynchronouslyBlockedDeletionReturnsAtOwnedBudgetDeadline()
    {
        var budgetCancellation = new CancellationTokenSource();
        var replay = new PendingProfileDeletionReplay(
            _ => budgetCancellation);
        var deletionStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        using var allowLateCompletion = new ManualResetEventSlim();
        var lateCompletion = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);

        var replayTask = Task.Run(
            () => replay.ReplayAsync(
                ["blocked"],
                (_, _) =>
                {
                    deletionStarted.TrySetResult();
                    allowLateCompletion.Wait();
                    lateCompletion.TrySetResult();
                    return Task.FromResult(true);
                },
                TimeSpan.FromMinutes(1),
                TestContext.Current.CancellationToken),
            CancellationToken.None);
        await deletionStarted.Task.WaitAsync(
            TimeSpan.FromSeconds(1),
            TestContext.Current.CancellationToken);

        budgetCancellation.Cancel();
        try
        {
            var result = await replayTask.WaitAsync(
                TimeSpan.FromSeconds(1),
                TestContext.Current.CancellationToken);

            Assert.Empty(result.DeletedKeys);
            Assert.True(result.BudgetExpired);
        }
        finally
        {
            allowLateCompletion.Set();
        }

        await lateCompletion.Task.WaitAsync(
            TimeSpan.FromSeconds(1),
            TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task ReplayAsync_CallerCancellationPropagates()
    {
        var budgetCancellation = new CancellationTokenSource();
        var replay = new PendingProfileDeletionReplay(
            _ => budgetCancellation);
        using var callerCancellation = new CancellationTokenSource();
        var deletionStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);

        var replayTask = replay.ReplayAsync(
            ["blocked"],
            async (_, cancellationToken) =>
            {
                deletionStarted.TrySetResult();
                await Task.Delay(
                    Timeout.InfiniteTimeSpan,
                    cancellationToken);
                return true;
            },
            TimeSpan.FromMinutes(1),
            callerCancellation.Token);
        await deletionStarted.Task.WaitAsync(
            TimeSpan.FromSeconds(1),
            TestContext.Current.CancellationToken);

        callerCancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () => await replayTask);
    }

    [Fact]
    public async Task ReplayAsync_UncooperativeDeletionPropagatesCallerCancellationPromptly()
    {
        var budgetCancellation = new CancellationTokenSource();
        var replay = new PendingProfileDeletionReplay(
            _ => budgetCancellation);
        using var callerCancellation = new CancellationTokenSource();
        var deletionStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var allowLateCompletion = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        var replayTask = replay.ReplayAsync(
            ["blocked"],
            (_, _) =>
            {
                deletionStarted.TrySetResult();
                return allowLateCompletion.Task;
            },
            TimeSpan.FromMinutes(1),
            callerCancellation.Token);
        await deletionStarted.Task.WaitAsync(
            TimeSpan.FromSeconds(1),
            TestContext.Current.CancellationToken);

        callerCancellation.Cancel();
        try
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                async () => await replayTask.WaitAsync(
                    TimeSpan.FromSeconds(1),
                    TestContext.Current.CancellationToken));
        }
        finally
        {
            allowLateCompletion.TrySetResult(true);
        }
    }

    [Fact]
    public async Task ReplayAsync_DelegateCancellationWithoutExpiredBudgetPropagates()
    {
        var replay = new PendingProfileDeletionReplay();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => replay.ReplayAsync(
                ["faulted"],
                (_, _) => Task.FromCanceled<bool>(
                    new CancellationToken(canceled: true)),
                TimeSpan.FromMinutes(1),
                TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ReplayAsync_UnexpectedDelegateFaultPropagates()
    {
        var replay = new PendingProfileDeletionReplay();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => replay.ReplayAsync(
                ["faulted"],
                (_, _) => throw new InvalidOperationException("test fault"),
                TimeSpan.FromMinutes(1),
                TestContext.Current.CancellationToken));

        Assert.Equal("test fault", exception.Message);
    }

    [Fact]
    public async Task ReplayAsync_LockedProfilePreservesDataAndDeletionIntent()
    {
        var service = new SettingsService(_storageDirectory);
        var deletedKey = Guid.NewGuid().ToString("N");
        var lockedKey = Guid.NewGuid().ToString("N");
        var deletedDirectory = CreateProfileDirectory(deletedKey);
        var lockedDirectory = CreateProfileDirectory(lockedKey);
        var lockedPath = Path.Combine(lockedDirectory, "WebView.lock");
        File.WriteAllText(lockedPath, "preserve me");
        service.StageProfileDeletion(deletedKey);
        service.StageProfileDeletion(lockedKey);
        var settings = new AppSettings
        {
            PendingProfileDeletionKeys = [deletedKey, lockedKey]
        };
        PendingProfileDeletionReplayResult result;

        using (var lockedFile = new FileStream(
                   lockedPath,
                   FileMode.Open,
                   FileAccess.ReadWrite,
                   FileShare.None))
        {
            result = await new PendingProfileDeletionReplay().ReplayAsync(
                [deletedKey, lockedKey],
                (accountKey, cancellationToken) =>
                    service.DeletePendingProfileAsync(
                        accountKey,
                        settings,
                        cancellationToken),
                TimeSpan.FromMilliseconds(250),
                TestContext.Current.CancellationToken);
        }

        Assert.Equal([deletedKey], result.DeletedKeys);
        Assert.True(result.BudgetExpired);
        Assert.False(Directory.Exists(deletedDirectory));
        Assert.True(Directory.Exists(lockedDirectory));
        Assert.Equal("preserve me", File.ReadAllText(lockedPath));
        Assert.Equal([deletedKey, lockedKey], settings.PendingProfileDeletionKeys);
        Assert.Contains(
            lockedKey,
            service.GetJournaledProfileDeletionKeys(),
            StringComparer.OrdinalIgnoreCase);
    }

    public void Dispose()
    {
        if (Directory.Exists(_storageDirectory))
            Directory.Delete(_storageDirectory, recursive: true);
    }

    private string CreateProfileDirectory(string accountKey)
    {
        var directory = Path.Combine(
            _storageDirectory,
            "Profiles",
            accountKey);
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "Cookies"), "local-data");
        return directory;
    }
}
