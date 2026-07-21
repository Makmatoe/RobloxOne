using System.IO;

namespace SessionDock.Services;

internal static class LocalDataException
{
    internal static bool IsExpectedPersistenceFailure(Exception exception) =>
        exception is IOException or UnauthorizedAccessException;
}
