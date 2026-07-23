using SessionDock.Services;

namespace SessionDock.Tests;

public sealed class JoinUserDestinationTests
{
    [Theory]
    [InlineData("Builderman", "@Builderman")]
    [InlineData("@Builderman", "@Builderman")]
    [InlineData("123456", "123456")]
    [InlineData("https://www.roblox.com/users/123456/profile", "123456")]
    [InlineData("https://roblox.com/users/123456/profile/", "123456")]
    public void TryParseInput_AcceptsUnambiguousUserIdentifiers(
        string input,
        string expectedDisplayValue)
    {
        var success = JoinUserDestination.TryParseInput(
            input,
            out var identifier,
            out var error);

        Assert.True(success, error);
        Assert.Equal(expectedDisplayValue, identifier!.DisplayValue);
        if (expectedDisplayValue.StartsWith('@'))
            Assert.Equal(expectedDisplayValue[1..], identifier.Username);
        else
            Assert.Equal(long.Parse(expectedDisplayValue), identifier.UserId);
    }

    [Theory]
    [InlineData("")]
    [InlineData("display name")]
    [InlineData("ab")]
    [InlineData("name-with-dash")]
    [InlineData("https://example.com/users/123/profile")]
    [InlineData("http://www.roblox.com/users/123/profile")]
    [InlineData("https://www.roblox.com/games/123")]
    [InlineData("https://user@www.roblox.com/users/123/profile")]
    [InlineData("https://www.roblox.com/users/123/profile?redirect=1")]
    public void TryParseInput_RejectsAmbiguousOrUntrustedValues(string input)
    {
        Assert.False(JoinUserDestination.TryParseInput(
            input,
            out var identifier,
            out _));
        Assert.Null(identifier);
    }

    [Fact]
    public void StoredValue_RoundTripsWithoutConfusingUserIdForPlaceId()
    {
        Assert.True(JoinUserDestination.TryParseInput(
            "123456",
            out var parsed,
            out _));

        var stored = JoinUserDestination.CreateStoredValue(parsed!);
        var roundTrip = JoinUserDestination.TryParseStored(
            stored,
            out var reparsed,
            out var error);

        Assert.Equal("user:123456", stored);
        Assert.True(roundTrip, error);
        Assert.Equal(123456, reparsed!.UserId);
        Assert.True(JoinUserDestination.IsStoredValue(stored));
        Assert.False(JoinUserDestination.IsStoredValue("123456"));
    }
}
