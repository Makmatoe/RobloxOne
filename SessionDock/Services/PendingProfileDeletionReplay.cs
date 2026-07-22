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

        using var budgetCancellation =
            _createBudgetCancellation(budget) ??
            throw new InvalidOperationException(
                "The profile-deletion replay budget could not be created.");
        using var replayCancellation =
            CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                budgetCancellation.Token);
        var deletedKeys = new List<string>();

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
                if (await deleteAsync(
                        accountKey,
                        replayCancellation.Token))
                {
                    deletedKeys.Add(accountKey);
                }
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

    private static PendingProfileDeletionReplayResult CreateResult(
        List<string> deletedKeys,
        bool budgetExpired) =>
        new(deletedKeys.AsReadOnly(), budgetExpired);
}

internal sealed record PendingProfileDeletionReplayResult(
    IReadOnlyList<string> DeletedKeys,
    bool BudgetExpired);
