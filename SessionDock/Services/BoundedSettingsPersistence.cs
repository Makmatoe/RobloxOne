namespace SessionDock.Services;

internal static class BoundedSettingsPersistence
{
    internal static async Task<bool> TrySaveAsync(
        Action save,
        TimeSpan timeout)
    {
        ArgumentNullException.ThrowIfNull(save);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(
            timeout,
            TimeSpan.Zero);

        return await TrySaveAsync(
            () =>
            {
                save();
                return Task.CompletedTask;
            },
            timeout);
    }

    internal static async Task<bool> TrySaveAsync(
        Func<Task> saveAsync,
        TimeSpan timeout)
    {
        ArgumentNullException.ThrowIfNull(saveAsync);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(
            timeout,
            TimeSpan.Zero);

        var saveTask = Task.Run(saveAsync);
        try
        {
            await saveTask.WaitAsync(timeout);
            return true;
        }
        catch (TimeoutException)
        {
            ObserveLaterFailure(saveTask);
            return false;
        }
        catch (Exception exception) when (
            LocalDataException.IsExpectedPersistenceFailure(exception))
        {
            // Shutdown persistence is best-effort and must not strand Close.
            return false;
        }
    }

    private static void ObserveLaterFailure(Task task)
    {
        _ = task.ContinueWith(
            completed => System.Diagnostics.Trace.WriteLine(
                $"A timed-out settings save later failed: {completed.Exception?.GetBaseException().GetType().Name}."),
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously |
                TaskContinuationOptions.OnlyOnFaulted,
            TaskScheduler.Default);
    }
}
