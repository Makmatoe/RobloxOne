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

    [Fact]
    public void CanContinue_SameTokenAfterReadinessLoss_ReturnsFalse()
    {
        var token = new WebSessionToken(7, "account");

        Assert.True(RobloxWebSessionService.CanContinue(
            token,
            generation: 7,
            token,
            isReady: true,
            browserHasCore: true));
        Assert.False(RobloxWebSessionService.CanContinue(
            token,
            generation: 7,
            token,
            isReady: false,
            browserHasCore: true));
    }

    [Fact]
    public void CanContinue_SupersededTokenCannotUseNewBrowserReadiness()
    {
        var oldToken = new WebSessionToken(7, "old");
        var newToken = new WebSessionToken(8, "new");

        Assert.False(RobloxWebSessionService.CanContinue(
            newToken,
            generation: 8,
            oldToken,
            isReady: true,
            browserHasCore: true));
        Assert.True(RobloxWebSessionService.CanContinue(
            newToken,
            generation: 8,
            newToken,
            isReady: true,
            browserHasCore: true));
    }

    [Fact]
    public async Task WaitForSessionWork_SessionFailureWakesBeforeWorkTimeout()
    {
        var work = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var sessionEnded = new TaskCompletionSource<WebSessionUnavailableReason>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var waiting = RobloxWebSessionService.WaitForSessionWorkAsync(
            work.Task,
            sessionEnded.Task,
            TimeSpan.FromSeconds(5),
            TestContext.Current.CancellationToken);

        sessionEnded.SetResult(WebSessionUnavailableReason.ProcessExited);

        Assert.False(await waiting.WaitAsync(
            TimeSpan.FromSeconds(1),
            TestContext.Current.CancellationToken));
        Assert.False(work.Task.IsCompleted);
    }
}
