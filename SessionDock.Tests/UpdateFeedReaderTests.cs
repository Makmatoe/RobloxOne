using System.Text.Json;
using SessionDock.ReleaseTrust;
using SessionDock.Services;

namespace SessionDock.Tests;

public sealed class UpdateFeedReaderTests
{
    [Theory]
    [MemberData(nameof(InvalidFeedFailures))]
    public async Task ReadAsync_InvalidExternalFeed_IsRejectedSafely(
        Exception feedFailure)
    {
        var exception = await Assert.ThrowsAsync<ReleaseTrustException>(() =>
            UpdateFeedReader.ReadAsync<int>(() =>
                Task.FromException<int>(feedFailure)));

        Assert.Equal(
            "GitHub returned an invalid update feed. The installed version was left unchanged.",
            exception.Message);
        Assert.Same(feedFailure, exception.InnerException);
    }

    [Theory]
    [MemberData(nameof(ProgrammerFaults))]
    public async Task ReadAsync_ProgrammerFault_RemainsObservable(
        Exception programmerFault)
    {
        var exception = await Assert.ThrowsAsync(
            programmerFault.GetType(),
            () => UpdateFeedReader.ReadAsync<int>(() =>
                Task.FromException<int>(programmerFault)));

        Assert.Same(programmerFault, exception);
    }

    [Fact]
    public async Task ReadAsync_ValidFeed_ReturnsResult()
    {
        var result = await UpdateFeedReader.ReadAsync(() => Task.FromResult(42));

        Assert.Equal(42, result);
    }

    public static TheoryData<Exception> InvalidFeedFailures => new()
    {
        new JsonException("malformed JSON"),
        new ArgumentException("invalid feed version")
    };

    public static TheoryData<Exception> ProgrammerFaults => new()
    {
        new InvalidOperationException("programmer fault"),
        new ArgumentNullException("requiredValue"),
        new ArgumentOutOfRangeException("requiredValue")
    };
}
