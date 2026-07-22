using SessionDock.Models;

namespace SessionDock.Services;

internal static class PendingProfileCleanup
{
    internal static bool CanDelete(
        bool operationsDrained,
        bool finalSettingsSaved,
        AccountProfile? incompleteProfile,
        AccountProfile? currentPendingProfile,
        AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return operationsDrained &&
            finalSettingsSaved &&
            incompleteProfile is not null &&
            ReferenceEquals(incompleteProfile, currentPendingProfile) &&
            !settings.Accounts.Any(account =>
                account.Key.Equals(
                    incompleteProfile.Key,
                    StringComparison.OrdinalIgnoreCase));
    }

    public static async Task<bool> TryDeleteAsync(
        Func<CancellationToken, Task<bool>> deleteAsync,
        TimeSpan timeout)
    {
        ArgumentNullException.ThrowIfNull(deleteAsync);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(
            timeout,
            TimeSpan.Zero);

        var timeoutCancellation = new CancellationTokenSource(timeout);
        var deleteTask = Task.Run(() => deleteAsync(timeoutCancellation.Token));
        try
        {
            return await deleteTask.WaitAsync(timeoutCancellation.Token);
        }
        catch (Exception exception)
        {
            // Cleanup is best-effort and startup reconciles an abandoned profile.
            System.Diagnostics.Trace.WriteLine(
                $"Pending profile cleanup failed: {exception.GetType().Name}.");
            return false;
        }
        finally
        {
            if (deleteTask.IsCompleted)
            {
                timeoutCancellation.Dispose();
            }
            else
            {
                ObserveLateCompletion(deleteTask, timeoutCancellation);
            }
        }
    }

    private static void ObserveLateCompletion(
        Task deleteTask,
        CancellationTokenSource timeoutCancellation)
    {
        _ = deleteTask.ContinueWith(
            completed =>
            {
                if (completed.IsFaulted)
                {
                    System.Diagnostics.Trace.WriteLine(
                        $"A timed-out profile cleanup later failed: {completed.Exception?.GetBaseException().GetType().Name}.");
                }
                timeoutCancellation.Dispose();
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }
}
