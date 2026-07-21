namespace SessionDock.Services;

internal static class PendingProfileCleanup
{
    public static async Task<bool> TryDeleteAsync(
        Func<CancellationToken, Task<bool>> deleteAsync,
        TimeSpan timeout)
    {
        ArgumentNullException.ThrowIfNull(deleteAsync);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(
            timeout,
            TimeSpan.Zero);

        using var timeoutCancellation = new CancellationTokenSource(timeout);
        try
        {
            return await Task.Run(() => deleteAsync(timeoutCancellation.Token))
                .WaitAsync(timeoutCancellation.Token);
        }
        catch
        {
            // Cleanup is best-effort and startup reconciles an abandoned profile.
            return false;
        }
    }
}
