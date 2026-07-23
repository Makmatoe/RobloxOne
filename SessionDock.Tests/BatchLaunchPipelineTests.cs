using System.Collections.Concurrent;
using SessionDock.Services;

namespace SessionDock.Tests;

public sealed class BatchLaunchPipelineTests
{
    [Fact]
    public void BatchLaunch_RequestsTicketsAtStartInsteadOfWhileQueued()
    {
        var source = ReadBatchSource();
        var queueStart = source.IndexOf(
            "private async Task<QueuedBatchLaunchResult> QueueBatchLaunchAsync",
            StringComparison.Ordinal);
        var startStart = source.IndexOf(
            "private async Task<StartedBatchLaunchResult> StartQueuedBatchAccountAsync",
            StringComparison.Ordinal);
        var completionStart = source.IndexOf(
            "private async Task CompleteStartedBatchLaunchAsync",
            StringComparison.Ordinal);

        Assert.True(queueStart >= 0);
        Assert.True(startStart > queueStart);
        Assert.True(completionStart > startStart);
        Assert.DoesNotContain(
            "GetAuthenticationTicketAsync",
            source[queueStart..startStart],
            StringComparison.Ordinal);
        Assert.Contains(
            "GetAuthenticationTicketAsync",
            source[startStart..completionStart],
            StringComparison.Ordinal);
    }

    [Fact]
    public void BatchActivation_DoesNotPersistTemporaryAccountSwitches()
    {
        var source = ReadBatchSource();
        var activationStart = source.IndexOf(
            "private async Task<WebSessionToken?> ActivateBatchAccountAsync",
            StringComparison.Ordinal);
        var queueStart = source.IndexOf(
            "private async Task<QueuedBatchLaunchResult> QueueBatchLaunchAsync",
            StringComparison.Ordinal);

        Assert.True(activationStart >= 0);
        Assert.True(queueStart > activationStart);
        var activationSource = source[activationStart..queueStart];
        Assert.DoesNotContain(
            "TryCommitSettingsMutationAsync",
            activationSource,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "_settings.ActiveAccountKey",
            activationSource,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_QueuesNextItemWhileCurrentItemCompletes()
    {
        var events = new ConcurrentQueue<string>();
        var secondQueueStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseSecondQueue = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);

        var results = await BatchLaunchPipeline.RunAsync<int, int, int, int>(
            [10, 20],
            async (plan, index, cancellationToken) =>
            {
                events.Enqueue($"queue-start-{index}");
                if (index == 1)
                {
                    secondQueueStarted.TrySetResult();
                    await releaseSecondQueue.Task.WaitAsync(
                        cancellationToken);
                }
                events.Enqueue($"queue-end-{index}");
                return plan;
            },
            (queued, index, _) =>
            {
                events.Enqueue($"start-{index}");
                return Task.FromResult(queued);
            },
            async (started, index, _, cancellationToken) =>
            {
                events.Enqueue($"complete-start-{index}");
                if (index == 0)
                {
                    await secondQueueStarted.Task.WaitAsync(
                        TimeSpan.FromSeconds(2),
                        cancellationToken);
                    releaseSecondQueue.TrySetResult();
                }
                events.Enqueue($"complete-end-{index}");
                return started + 1;
            },
            TestContext.Current.CancellationToken);

        Assert.Equal([11, 21], results);
        var orderedEvents = events.ToArray();
        Assert.True(
            Array.IndexOf(orderedEvents, "start-0") <
            Array.IndexOf(orderedEvents, "queue-start-1"));
        Assert.True(
            Array.IndexOf(orderedEvents, "queue-start-1") <
            Array.IndexOf(orderedEvents, "complete-start-0"));
        Assert.True(
            Array.IndexOf(orderedEvents, "complete-end-0") <
            Array.IndexOf(orderedEvents, "start-1"));
    }

    [Fact]
    public async Task RunAsync_NeverPreparesMoreThanOneItemAhead()
    {
        var thirdQueueStarted = false;

        var results = await BatchLaunchPipeline.RunAsync<int, int, int, int>(
            [10, 20, 30],
            (plan, index, _) =>
            {
                if (index == 2)
                    thirdQueueStarted = true;
                return Task.FromResult(plan);
            },
            (queued, _, _) => Task.FromResult(queued),
            (started, index, _, _) =>
            {
                if (index == 0)
                    Assert.False(thirdQueueStarted);
                return Task.FromResult(started);
            },
            TestContext.Current.CancellationToken);

        Assert.Equal([10, 20, 30], results);
        Assert.True(thirdQueueStarted);
    }

    [Fact]
    public async Task RunAsync_ConsumerFailureCancelsAndObservesLookAhead()
    {
        var secondQueueStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var secondQueueCancelled = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            BatchLaunchPipeline.RunAsync<int, int, int, int>(
                [10, 20],
                async (plan, index, cancellationToken) =>
                {
                    if (index == 0)
                        return plan;

                    secondQueueStarted.TrySetResult();
                    try
                    {
                        await Task.Delay(
                            Timeout.InfiniteTimeSpan,
                            cancellationToken);
                    }
                    catch (OperationCanceledException)
                    {
                        secondQueueCancelled.TrySetResult();
                        throw;
                    }
                    return plan;
                },
                (queued, _, _) => Task.FromResult(queued),
                async (_, _, _, cancellationToken) =>
                {
                    await secondQueueStarted.Task.WaitAsync(
                        TimeSpan.FromSeconds(2),
                        cancellationToken);
                    throw new InvalidOperationException("launch fault");
                },
                TestContext.Current.CancellationToken));

        Assert.Equal("launch fault", exception.Message);
        await secondQueueCancelled.Task.WaitAsync(
            TimeSpan.FromSeconds(2),
            TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task RunAsync_QueueFailurePreservesTheOriginalException()
    {
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            BatchLaunchPipeline.RunAsync<int, int, int, int>(
                [10],
                (_, _, _) => throw new InvalidOperationException("queue fault"),
                (queued, _, _) => Task.FromResult(queued),
                (started, _, _, _) => Task.FromResult(started),
                TestContext.Current.CancellationToken));

        Assert.Equal("queue fault", exception.Message);
    }

    private static string ReadBatchSource() => File.ReadAllText(Path.Combine(
        FindRepositoryRoot(),
        "SessionDock",
        "MainWindow.Batch.cs"));

    private static string FindRepositoryRoot()
    {
        foreach (var start in new[]
                 {
                     Environment.CurrentDirectory,
                     AppContext.BaseDirectory
                 })
        {
            for (var directory = new DirectoryInfo(start);
                 directory is not null;
                 directory = directory.Parent)
            {
                if (File.Exists(Path.Combine(
                        directory.FullName,
                        "SessionDock.slnx")))
                {
                    return directory.FullName;
                }
            }
        }

        throw new DirectoryNotFoundException(
            "The SessionDock repository root could not be located.");
    }
}
