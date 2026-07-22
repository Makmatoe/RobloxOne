using System.ComponentModel;
using System.IO;
using System.Net.Http;
using SessionDock.ReleaseTrust;
using Velopack.Exceptions;

namespace SessionDock.Services;

internal sealed record UpdateFailurePresentation(
    string Title,
    string Detail,
    string Badge)
{
    public static UpdateFailurePresentation Create(Exception exception)
    {
        if (TryCreate(exception, out var presentation))
            return presentation;

        throw new ArgumentException(
            "The exception is not an expected update failure.",
            nameof(exception));
    }

    public static bool TryCreate(
        Exception exception,
        out UpdateFailurePresentation presentation)
    {
        ArgumentNullException.ThrowIfNull(exception);

        presentation = exception switch
        {
            ReleaseTrustException trustFailure => new(
                "Update was rejected",
                trustFailure.Message,
                "UPDATE REJECTED"),
            AcquireLockFailedException => new(
                "Update files are busy",
                "Another update operation is using SessionDock's update files. Close every SessionDock or Roblox One window and installer, wait a few seconds, reopen the app, and try again. The installed version was left unchanged.",
                "UPDATE BUSY"),
            ChecksumFailedException => new(
                "Downloaded update was rejected",
                "The downloaded package did not match the release feed and was not installed. Try again; if this repeats, keep both local data directories unchanged and download the current Setup and checksum from the canonical GitHub release page.",
                "UPDATE REJECTED"),
            InvalidDataException => new(
                "Downloaded update was rejected",
                "The downloaded package did not pass integrity checks and was not installed. Try again; if this repeats, keep both local data directories unchanged and download the current Setup and checksum from the canonical GitHub release page.",
                "UPDATE REJECTED"),
            NotInstalledException => new(
                "Setup is required",
                "This copy is no longer recognized as an installed app. Keep the SessionDock and RobloxOne local data directories unchanged, then run the current verified SessionDock Setup from the canonical GitHub release page. Do not uninstall or delete local data first.",
                "SETUP REQUIRED"),
            TaskCanceledException => new(
                "GitHub did not respond in time",
                "The update request timed out. Check the internet connection and try again. The installed version was left unchanged.",
                "NETWORK TIMEOUT"),
            OperationCanceledException => new(
                "GitHub did not respond in time",
                "The update request ended before GitHub responded. Check the internet connection and try again. The installed version was left unchanged.",
                "NETWORK TIMEOUT"),
            TimeoutException => new(
                "GitHub did not respond in time",
                "The update request timed out. Check the internet connection and try again. The installed version was left unchanged.",
                "NETWORK TIMEOUT"),
            HttpIOException => new(
                "GitHub connection was interrupted",
                "The connection to the official GitHub release feed ended unexpectedly. Check the internet connection and try again. The installed version was left unchanged.",
                "NETWORK ERROR"),
            HttpRequestException => new(
                "GitHub could not be reached",
                "SessionDock could not contact the official GitHub release feed. Check the internet connection and try again. The installed version was left unchanged.",
                "NETWORK ERROR"),
            UnauthorizedAccessException => new(
                "Update access was denied",
                "SessionDock could not write its update files. Reopen it as the same Windows user that installed it and check whether security software is blocking it, then try again.",
                "ACCESS DENIED"),
            IOException => new(
                "Update files are unavailable",
                "An update file or folder is unavailable or locked. Close every SessionDock or Roblox One window and installer, reopen the app, and try again. The installed version was left unchanged.",
                "UPDATE FILE ERROR"),
            Win32Exception => new(
                "Updater could not start",
                "Windows could not start the SessionDock updater. Close the app, keep both local data directories unchanged, and run the current verified SessionDock Setup from the canonical GitHub release page. Do not uninstall or delete local data first.",
                "UPDATER ERROR"),
            _ => null!
        };
        return presentation is not null;
    }
}
