using Microsoft.Web.WebView2.Wpf;

namespace SessionDock.Services;

internal static class WebSessionException
{
    internal static bool IsExpectedLifecycleFailure(Exception exception) =>
        exception is WebSessionUnavailableException;
}

internal enum WebSessionUnavailableReason
{
    MissingRuntime,
    RuntimeStartFailed,
    ProcessExited,
    Superseded,
    Closed
}

internal sealed class WebSessionUnavailableException : Exception
{
    internal WebSessionUnavailableException(
        WebSessionUnavailableReason reason,
        string message,
        Exception? innerException = null)
        : base(message, innerException)
    {
        Reason = reason;
    }

    internal WebSessionUnavailableReason Reason { get; }
}

internal readonly record struct WebSessionToken(
    int Generation,
    string AccountKey);

internal sealed record WebSessionBrowser(
    WebView2 Browser,
    WebSessionToken Token);

internal sealed class WebSessionEventArgs(WebSessionToken token) : EventArgs
{
    internal WebSessionToken Token { get; } = token;
}

internal sealed class WebSessionUnavailableEventArgs(
    WebSessionToken token,
    WebSessionUnavailableReason reason) : EventArgs
{
    internal WebSessionToken Token { get; } = token;
    internal WebSessionUnavailableReason Reason { get; } = reason;
}
