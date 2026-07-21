using RobloxOneLauncher.Models;
using RobloxOneLauncher.Services;

namespace RobloxOneLauncher.Tests;

public sealed class RecentDestinationTests
{
    [Theory]
    [InlineData("123456", "public:123456")]
    [InlineData(
        "https://www.roblox.com/games/123456/Game?privateServerLinkCode=private-code",
        "private:123456:private-code")]
    [InlineData("code=Abc_12-Z", "share:Abc_12-Z")]
    [InlineData(" invalid destination ", "raw:invalid destination")]
    public void CreateKey_NormalizesDestinationIdentity(string destination, string expected)
    {
        var recent = new RecentExperience { Destination = destination };

        Assert.Equal(expected, RecentDestinationIdentity.CreateKey(recent));
    }

    [Fact]
    public void Matches_EquivalentPublicDestinations_ReturnsTrue()
    {
        var left = new RecentExperience { Destination = "123456" };
        var right = new RecentExperience
        {
            Destination = "https://www.roblox.com/games/123456/Different-Display-Name"
        };

        Assert.True(RecentDestinationIdentity.Matches(left, right));
    }

    [Fact]
    public void CreateKey_NullRecent_Throws()
    {
        Assert.Throws<ArgumentNullException>(() =>
            RecentDestinationIdentity.CreateKey(null!));
    }

    [Fact]
    public void TryResolve_ServerJobId_ReturnsMostRecentMatchingJoin()
    {
        const string serverJobId = "a18c877e-4070-4a84-a5f7-36668b46a77d";
        var older = new RecentExperience
        {
            Destination = "111",
            PlaceId = 111,
            ServerJobId = serverJobId,
            LastLaunchedAt = DateTimeOffset.Parse("2026-01-01T00:00:00Z")
        };
        var newer = new RecentExperience
        {
            Destination = "222",
            PlaceId = 222,
            ServerJobId = serverJobId.ToUpperInvariant(),
            LastLaunchedAt = DateTimeOffset.Parse("2026-01-02T00:00:00Z")
        };

        var resolved = RecentServerJoinResolver.TryResolve(
            serverJobId,
            new[] { older, newer },
            out var trackedServer);

        Assert.True(resolved);
        Assert.Same(newer, trackedServer);
    }

    [Theory]
    [InlineData("not-a-guid")]
    [InlineData("")]
    public void TryResolve_InvalidJobId_ReturnsFalse(string input)
    {
        var resolved = RecentServerJoinResolver.TryResolve(
            input,
            Array.Empty<RecentExperience>(),
            out var trackedServer);

        Assert.False(resolved);
        Assert.Null(trackedServer);
    }

    [Fact]
    public void TryResolve_IgnoresMatchesWithoutValidPlaceId()
    {
        const string serverJobId = "a18c877e-4070-4a84-a5f7-36668b46a77d";
        var recent = new RecentExperience
        {
            Destination = "code=Abc_12-Z",
            PlaceId = 0,
            ServerJobId = serverJobId,
            LastLaunchedAt = DateTimeOffset.UtcNow
        };

        var resolved = RecentServerJoinResolver.TryResolve(
            serverJobId,
            new[] { recent },
            out var trackedServer);

        Assert.False(resolved);
        Assert.Null(trackedServer);
    }

    [Fact]
    public void TryResolve_NullArguments_Throw()
    {
        Assert.Throws<ArgumentNullException>(() =>
            RecentServerJoinResolver.TryResolve(null!, Array.Empty<RecentExperience>(), out _));
        Assert.Throws<ArgumentNullException>(() =>
            RecentServerJoinResolver.TryResolve(
                Guid.NewGuid().ToString(),
                null!,
                out _));
    }
}
