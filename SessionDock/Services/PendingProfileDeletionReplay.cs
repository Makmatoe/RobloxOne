namespace SessionDock.Services;

internal sealed class PendingProfileDeletionReplay
{
    private readonly Func<TimeSpan, CancellationTokenSource>
        _createBudgetCancellation;

    internal PendingProfileDeletionReplay(
        Func<TimeSpan, CancellationTokenSource>? createBudgetCancellation = null)
    {
        _createBudgetCancellation = createBudgetCancellation ??
            (budget => new CancellationTokenSource(budget));
    }

    internal async Task<PendingProfileDeletionReplayResult> ReplayAsync(
        IReadOnlyList<string> accountKeys,
        Func<string, CancellationToken, Task<bool>> deleteAsync,
        TimeSpan budget,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(accountKeys);
        ArgumentNullException.ThrowIfNull(deleteAsync);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(
            budget,
            TimeSpan.Zero);
        cancellationToken.ThrowIfCancellationRequested();

        var budgetCancellation =
            _createBudgetCancellation(budget) ??
            throw new InvalidOperationException(
                "The profile-deletion replay budget could not be created.");
        CancellationTokenSource? replayCancellation = null;
        Task<bool>? activeDeletionTask = null;
        var deletedKeys = new List<string>();
        try
        {
            replayCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                budgetCancellation.Token);

            foreach (var accountKey in accountKeys)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (budgetCancellation.IsCancellationRequested)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    return CreateResult(deletedKeys, budgetExpired: true);
                }

                try
                {
                    activeDeletionTask = Task.Run(
                        () => deleteAsync(
                                accountKey,
                                replayCancellation.Token) ??
                            throw new InvalidOperationException(
                                "Profile deletion returned no operation."),
                        CancellationToken.None);
                    if (await activeDeletionTask.WaitAsync(
                            replayCancellation.Token))
                    {
                        deletedKeys.Add(accountKey);
                    }
                    activeDeletionTask = null;
                }
                catch (OperationCanceledException) when (
                    budgetCancellation.IsCancellationRequested &&
                    !cancellationToken.IsCancellationRequested)
                {
                    return CreateResult(deletedKeys, budgetExpired: true);
                }
            }

            cancellationToken.ThrowIfCancellationRequested();
            return CreateResult(deletedKeys, budgetExpired: false);
        }
        finally
        {
            DisposeCancellationSourcesWhenSafe(
                activeDeletionTask,
                replayCancellation,
                budgetCancellation);
        }
    }

    private static void DisposeCancellationSourcesWhenSafe(
        Task? activeDeletionTask,
        CancellationTokenSource? replayCancellation,
        CancellationTokenSource budgetCancellation)
    {
        if (activeDeletionTask is null || activeDeletionTask.IsCompleted)
        {
            if (activeDeletionTask?.IsFaulted == true)
                _ = activeDeletionTask.Exception;
            replayCancellation?.Dispose();
            budgetCancellation.Dispose();
            return;
        }

        _ = activeDeletionTask.ContinueWith(
            completed =>
            {
                if (completed.IsFaulted)
                {
                    System.Diagnostics.Trace.WriteLine(
                        $"A timed-out profile deletion later failed: {completed.Exception?.GetBaseException().GetType().Name}.");
                }
                replayCancellation?.Dispose();
                budgetCancellation.Dispose();
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private static PendingProfileDeletionReplayResult CreateResult(
        List<string> deletedKeys,
        bool budgetExpired) =>
        new(deletedKeys.AsReadOnly(), budgetExpired);
}

internal sealed record PendingProfileDeletionReplayResult(
    IReadOnlyList<string> DeletedKeys,
    bool BudgetExpired);
