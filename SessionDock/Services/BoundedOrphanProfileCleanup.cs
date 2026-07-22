namespace SessionDock.Services;

internal sealed class BoundedOrphanProfileCleanup
{
    private readonly Func<TimeSpan, CancellationTokenSource>
        _createBudgetCancellation;

    internal BoundedOrphanProfileCleanup(
        Func<TimeSpan, CancellationTokenSource>? createBudgetCancellation = null)
    {
        _createBudgetCancellation = createBudgetCancellation ??
            (budget => new CancellationTokenSource(budget));
    }

    internal async Task<BoundedOrphanProfileCleanupResult> RunAsync(
        Func<CancellationToken, int> cleanup,
        TimeSpan budget,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(cleanup);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(
            budget,
            TimeSpan.Zero);
        cancellationToken.ThrowIfCancellationRequested();

        var budgetCancellation =
            _createBudgetCancellation(budget) ??
            throw new InvalidOperationException(
                "The orphan-profile cleanup budget could not be created.");
        CancellationTokenSource? cleanupCancellation = null;
        Task<int>? cleanupTask = null;
        try
        {
            cleanupCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                budgetCancellation.Token);
            if (budgetCancellation.IsCancellationRequested)
            {
                cancellationToken.ThrowIfCancellationRequested();
                return new(0, BudgetExpired: true);
            }

            cleanupTask = Task.Run(
                () => cleanup(cleanupCancellation.Token),
                CancellationToken.None);
            try
            {
                var removedProfiles = await cleanupTask.WaitAsync(
                    cleanupCancellation.Token);
                cleanupTask = null;
                cancellationToken.ThrowIfCancellationRequested();
                return new(removedProfiles, BudgetExpired: false);
            }
            catch (OperationCanceledException) when (
                budgetCancellation.IsCancellationRequested &&
                !cancellationToken.IsCancellationRequested)
            {
                return new(0, BudgetExpired: true);
            }
        }
        finally
        {
            DisposeCancellationSourcesWhenSafe(
                cleanupTask,
                cleanupCancellation,
                budgetCancellation);
        }
    }

    private static void DisposeCancellationSourcesWhenSafe(
        Task? cleanupTask,
        CancellationTokenSource? cleanupCancellation,
        CancellationTokenSource budgetCancellation)
    {
        if (cleanupTask is null || cleanupTask.IsCompleted)
        {
            if (cleanupTask?.IsFaulted == true)
                _ = cleanupTask.Exception;
            cleanupCancellation?.Dispose();
            budgetCancellation.Dispose();
            return;
        }

        _ = cleanupTask.ContinueWith(
            completed =>
            {
                if (completed.IsFaulted)
                {
                    System.Diagnostics.Trace.WriteLine(
                        $"A timed-out orphan-profile cleanup later failed: {completed.Exception?.GetBaseException().GetType().Name}.");
                }
                cleanupCancellation?.Dispose();
                budgetCancellation.Dispose();
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }
}

internal sealed record BoundedOrphanProfileCleanupResult(
    int RemovedProfiles,
    bool BudgetExpired);
