using SessionDock.Services;

namespace SessionDock.Tests;

public sealed class WebSessionExceptionTests
{
    [Fact]
    public void IsExpectedLifecycleFailure_TypedUnavailableFailure_ReturnsTrue()
    {
        Assert.True(WebSessionException.IsExpectedLifecycleFailure(
            new WebSessionUnavailableException(
                WebSessionUnavailableReason.ProcessExited,
                "browser process exited")));
    }

    [Theory]
    [MemberData(nameof(UntypedFailures))]
    public void IsExpectedLifecycleFailure_UntypedFailure_ReturnsFalse(
        Exception exception)
    {
        Assert.False(WebSessionException.IsExpectedLifecycleFailure(exception));
    }

    public static TheoryData<Exception> UntypedFailures() => new()
    {
        new System.Runtime.InteropServices.COMException("uncorrelated COM fault"),
        new ObjectDisposedException("webview"),
        new InvalidOperationException("programmer failure"),
        new ArgumentOutOfRangeException("value")
    };

    [Theory]
    [InlineData(unchecked((int)0x8007139F))]
    [InlineData(unchecked((int)0x80070578))]
    [InlineData(unchecked((int)0x80070005))]
    [InlineData(unchecked((int)0x80004005))]
    public void IsExpectedInitializationHResult_DocumentedFailure_ReturnsTrue(
        int hResult)
    {
        Assert.True(
            RobloxWebSessionService.IsExpectedInitializationHResult(hResult));
    }

    [Fact]
    public void IsExpectedInitializationHResult_UnrelatedComFault_ReturnsFalse()
    {
        Assert.False(
            RobloxWebSessionService.IsExpectedInitializationHResult(
                unchecked((int)0x8000FFFF)));
    }
}
