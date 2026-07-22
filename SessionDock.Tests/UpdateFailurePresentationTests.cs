using System.ComponentModel;
using System.IO;
using System.Net.Http;
using System.Runtime.CompilerServices;
using SessionDock.ReleaseTrust;
using SessionDock.Services;
using Velopack.Exceptions;

namespace SessionDock.Tests;

public sealed class UpdateFailurePresentationTests
{
    [Fact]
    public void Create_LockedUpdateFiles_ExplainsHowToRecover()
    {
        var result = UpdateFailurePresentation.Create(
            CreateWithoutConstructor<AcquireLockFailedException>());

        Assert.Equal("Update files are busy", result.Title);
        Assert.Contains("close", result.Detail, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("reopen", result.Detail, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("UPDATE BUSY", result.Badge);
    }

    [Theory]
    [MemberData(nameof(ExpectedFailures))]
    public void TryCreate_ExpectedOperationalFailure_IsClassified(
        Exception exception,
        string expectedTitle,
        string expectedBadge)
    {
        var classified = UpdateFailurePresentation.TryCreate(
            exception,
            out var result);

        Assert.True(classified);
        Assert.Equal(expectedTitle, result.Title);
        Assert.Equal(expectedBadge, result.Badge);
        Assert.NotEmpty(result.Detail);
    }

    [Fact]
    public void Create_ReleaseTrustFailure_PreservesSafePolicyMessage()
    {
        const string policyMessage = "The signed descriptor was rejected.";

        var result = UpdateFailurePresentation.Create(
            new ReleaseTrustException(policyMessage));

        Assert.Equal("Update was rejected", result.Title);
        Assert.Equal(policyMessage, result.Detail);
        Assert.Equal("UPDATE REJECTED", result.Badge);
    }

    [Fact]
    public void TryCreate_ProgrammerFault_IsNotClassified()
    {
        var classified = UpdateFailurePresentation.TryCreate(
            new InvalidOperationException("programmer fault"),
            out _);

        Assert.False(classified);
    }

    public static TheoryData<Exception, string, string> ExpectedFailures => new()
    {
        {
            new HttpRequestException("offline"),
            "GitHub could not be reached",
            "NETWORK ERROR"
        },
        {
            new TaskCanceledException("HTTP timeout"),
            "GitHub did not respond in time",
            "NETWORK TIMEOUT"
        },
        {
            new OperationCanceledException("HTTP operation stopped"),
            "GitHub did not respond in time",
            "NETWORK TIMEOUT"
        },
        {
            new TimeoutException("timeout"),
            "GitHub did not respond in time",
            "NETWORK TIMEOUT"
        },
        {
            new HttpIOException(
                HttpRequestError.ConnectionError,
                "connection interrupted",
                null),
            "GitHub connection was interrupted",
            "NETWORK ERROR"
        },
        {
            new UnauthorizedAccessException("denied"),
            "Update access was denied",
            "ACCESS DENIED"
        },
        {
            new IOException("locked"),
            "Update files are unavailable",
            "UPDATE FILE ERROR"
        },
        {
            new InvalidDataException("invalid package"),
            "Downloaded update was rejected",
            "UPDATE REJECTED"
        },
        {
            new ChecksumFailedException("package.nupkg"),
            "Downloaded update was rejected",
            "UPDATE REJECTED"
        },
        {
            CreateWithoutConstructor<NotInstalledException>(),
            "Setup is required",
            "SETUP REQUIRED"
        },
        {
            new Win32Exception(2, "updater missing"),
            "Updater could not start",
            "UPDATER ERROR"
        }
    };

    private static T CreateWithoutConstructor<T>() where T : Exception =>
        (T)RuntimeHelpers.GetUninitializedObject(typeof(T));
}
