using SessionDock.Models;
using SessionDock.Services;

namespace SessionDock.Tests;

public sealed class RobloxLaunchUriBuilderTests
{
    [Fact]
    public void Build_PublicDestination_UsesRequestGameAndEscapesTicket()
    {
        var launchUri = RobloxLaunchUriBuilder.Build(
            new LaunchTarget(123456, null, null),
            "ticket +/=");

        var parts = ParseProtocolParts(launchUri);
        var placeLauncherUri = ParsePlaceLauncherUri(parts);
        var query = ParseQuery(placeLauncherUri.Query);

        Assert.Equal("roblox-player:1", parts[0]);
        Assert.Equal("play", parts["launchmode"]);
        Assert.Equal("ticket +/=", Uri.UnescapeDataString(parts["gameinfo"]));
        Assert.Equal("RequestGame", query["request"]);
        Assert.Equal("123456", query["placeId"]);
        Assert.Equal("false", query["isPlayTogetherGame"]);
        Assert.False(query.ContainsKey("linkCode"));
        Assert.Equal(parts["browsertrackerid"], query["browserTrackerId"]);
        Assert.True(long.Parse(parts["browsertrackerid"]) > 0);
        Assert.True(long.Parse(parts["launchtime"]) > 0);
        Assert.True(Guid.TryParse(query["joinAttemptId"], out _));
    }

    [Fact]
    public void Build_PrivateDestination_UsesRequestPrivateGameAndEscapesLinkCode()
    {
        var launchUri = RobloxLaunchUriBuilder.Build(
            new LaunchTarget(123456, "private code/+", null),
            "ticket");

        var query = ParseQuery(ParsePlaceLauncherUri(ParseProtocolParts(launchUri)).Query);

        Assert.Equal("RequestPrivateGame", query["request"]);
        Assert.Equal("123456", query["placeId"]);
        Assert.Equal("private code/+", query["linkCode"]);
        Assert.Equal(string.Empty, query["accessCode"]);
    }

    [Fact]
    public void Build_ProtocolAndQueryDelimiters_CannotInjectLaunchFields()
    {
        const string ticket = "ticket+channel:untrusted";
        const string linkCode = "private&gameId=untrusted+channel:value";

        var launchUri = RobloxLaunchUriBuilder.Build(
            new LaunchTarget(123456, linkCode, null),
            ticket);

        var parts = ParseProtocolParts(launchUri);
        var query = ParseQuery(ParsePlaceLauncherUri(parts).Query);

        Assert.Equal(ticket, Uri.UnescapeDataString(parts["gameinfo"]));
        Assert.Equal(string.Empty, parts["channel"]);
        Assert.Equal(linkCode, query["linkCode"]);
        Assert.False(query.ContainsKey("gameId"));
    }

    [Theory]
    [InlineData("ticket\rvalue")]
    [InlineData("ticket\nvalue")]
    [InlineData("ticket\0value")]
    public void Build_AuthenticationTicketWithControlCharacter_Throws(string ticket)
    {
        Assert.Throws<ArgumentException>(() =>
            RobloxLaunchUriBuilder.Build(
                new LaunchTarget(123456, null, null),
                ticket));
    }

    [Fact]
    public void Build_OversizedAuthenticationTicket_Throws()
    {
        Assert.Throws<ArgumentException>(() =>
            RobloxLaunchUriBuilder.Build(
                new LaunchTarget(123456, null, null),
                new string('A', (8 * 1024) + 1)));
    }

    [Fact]
    public void Build_TrackedServer_UsesNormalizedJobId()
    {
        const string inputJobId = "A18C877E-4070-4A84-A5F7-36668B46A77D";

        var launchUri = RobloxLaunchUriBuilder.Build(
            new LaunchTarget(123456, null, null),
            "ticket",
            inputJobId);

        var query = ParseQuery(ParsePlaceLauncherUri(ParseProtocolParts(launchUri)).Query);

        Assert.Equal("RequestGameJob", query["request"]);
        Assert.Equal("a18c877e-4070-4a84-a5f7-36668b46a77d", query["gameId"]);
        Assert.Equal("false", query["isPlayTogetherGame"]);
    }

    [Fact]
    public void Build_PrivateTrackedServer_PreservesPrivateLinkCode()
    {
        var launchUri = RobloxLaunchUriBuilder.Build(
            new LaunchTarget(123456, "private-code", null),
            "ticket",
            "a18c877e-4070-4a84-a5f7-36668b46a77d");

        var query = ParseQuery(ParsePlaceLauncherUri(ParseProtocolParts(launchUri)).Query);

        Assert.Equal("RequestGameJob", query["request"]);
        Assert.Equal("private-code", query["linkCode"]);
        Assert.Equal(string.Empty, query["accessCode"]);
        Assert.False(query.ContainsKey("isPlayTogetherGame"));
    }

    [Fact]
    public void BuildFollowUser_UsesRobloxFollowRequestAndSelectedAccountTicket()
    {
        var launchUri = RobloxLaunchUriBuilder.BuildFollowUser(
            123456,
            "ticket +/=",
            "nl-NL");

        var parts = ParseProtocolParts(launchUri);
        var query = ParseQuery(ParsePlaceLauncherUri(parts).Query);

        Assert.Equal("roblox-player:1", parts[0]);
        Assert.Equal("play", parts["launchmode"]);
        Assert.Equal("ticket +/=", Uri.UnescapeDataString(parts["gameinfo"]));
        Assert.Equal("RequestFollowUser", query["request"]);
        Assert.Equal("123456", query["userId"]);
        Assert.False(query.ContainsKey("placeId"));
        Assert.False(query.ContainsKey("gameId"));
        Assert.Equal(parts["browsertrackerid"], query["browserTrackerId"]);
        Assert.True(Guid.TryParse(query["joinAttemptId"], out _));
        Assert.Equal("nl_nl", parts["robloxLocale"]);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void BuildFollowUser_InvalidUserIdThrows(long userId)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            RobloxLaunchUriBuilder.BuildFollowUser(userId, "ticket"));
    }

    [Fact]
    public void Build_NullTarget_Throws()
    {
        Assert.Throws<ArgumentNullException>(() =>
            RobloxLaunchUriBuilder.Build(null!, "ticket"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Build_MissingAuthenticationTicket_Throws(string? ticket)
    {
        Assert.ThrowsAny<ArgumentException>(() =>
            RobloxLaunchUriBuilder.Build(new LaunchTarget(123456, null, null), ticket!));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Build_InvalidPlaceId_Throws(long placeId)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            RobloxLaunchUriBuilder.Build(new LaunchTarget(placeId, null, null), "ticket"));
    }

    [Fact]
    public void Build_InvalidServerJobId_Throws()
    {
        Assert.Throws<ArgumentException>(() =>
            RobloxLaunchUriBuilder.Build(
                new LaunchTarget(123456, null, null),
                "ticket",
                "not-a-guid"));
    }

    [Theory]
    [InlineData("nl-NL", "nl_nl")]
    [InlineData("pt_BR", "pt_br")]
    [InlineData(null, "en_us")]
    [InlineData("invalid+value", "en_us")]
    public void Build_UsesValidatedRobloxLocale(string? locale, string expected)
    {
        var launchUri = RobloxLaunchUriBuilder.Build(
            new LaunchTarget(123456, null, null),
            "ticket",
            locale: locale);

        var parts = ParseProtocolParts(launchUri);

        Assert.Equal(expected, parts["robloxLocale"]);
        Assert.Equal(expected, parts["gameLocale"]);
    }

    private static ProtocolPartsDictionary ParseProtocolParts(string launchUri)
    {
        var segments = launchUri.Split('+');
        var values = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["0"] = segments[0]
        };

        foreach (var segment in segments.Skip(1))
        {
            var pair = segment.Split(':', 2);
            values[pair[0]] = pair.Length == 2 ? pair[1] : string.Empty;
        }

        return new ProtocolPartsDictionary(segments[0], values);
    }

    private static Uri ParsePlaceLauncherUri(Dictionary<string, string> parts) =>
        new(Uri.UnescapeDataString(parts["placelauncherurl"]));

    private static Dictionary<string, string> ParseQuery(string query) =>
        query.TrimStart('?')
            .Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(item => item.Split('=', 2))
            .ToDictionary(
                pair => Uri.UnescapeDataString(pair[0]),
                pair => pair.Length == 2 ? Uri.UnescapeDataString(pair[1]) : string.Empty,
                StringComparer.Ordinal);

    private sealed class ProtocolPartsDictionary(
        string firstSegment,
        IDictionary<string, string> values)
        : Dictionary<string, string>(values)
    {
        public string this[int index] => index == 0
            ? firstSegment
            : throw new IndexOutOfRangeException();
    }
}
