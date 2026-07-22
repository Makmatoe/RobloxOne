namespace SessionDock.Services;

internal static class ApplicationStartup
{
    private const string LocalDataFailureMessage =
        "SessionDock could not safely open its local data folder:\n\n" +
        "%LOCALAPPDATA%\\SessionDock\n\n" +
        "Make sure this path is a writable folder rather than a file or redirected link, close programs that may be locking it, and then start SessionDock again.";

    internal static bool TryStart(
        Action start,
        Action<string> reportLocalDataFailure)
    {
        ArgumentNullException.ThrowIfNull(start);
        ArgumentNullException.ThrowIfNull(reportLocalDataFailure);
        try
        {
            start();
            return true;
        }
        catch (Exception exception) when (
            LocalDataException.IsExpectedPersistenceFailure(exception))
        {
            reportLocalDataFailure(LocalDataFailureMessage);
            return false;
        }
    }
}
