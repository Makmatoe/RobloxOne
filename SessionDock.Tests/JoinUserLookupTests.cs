using System.Text.Json;
using SessionDock.Services;

namespace SessionDock.Tests;

public sealed class JoinUserLookupTests
{
    private const string JobId = "A18C877E-4070-4A84-A5F7-36668B46A77D";

    [Fact]
    public void ParseJoinUserResponse_AvailableResultValidatesAndNormalizesFields()
    {
        using var document = JsonDocument.Parse($$"""
            {
              "status": "available",
              "user": {
                "id": 42,
                "name": "Builderman",
                "displayName": "Builder Man"
              },
              "placeId": 123456,
              "gameId": "{{JobId}}"
            }
            """);

        var result = RobloxWebSessionService.ParseJoinUserResponse(
            document.RootElement);

        Assert.Equal(JoinUserAvailability.Available, result.Availability);
        Assert.Equal(42, result.Resolution!.UserId);
        Assert.Equal("Builderman", result.Resolution.Username);
        Assert.Equal("Builder Man", result.Resolution.DisplayName);
        Assert.Equal(123456, result.Resolution.PlaceId);
        Assert.Equal(JobId.ToLowerInvariant(), result.Resolution.ServerJobId);
    }

    [Theory]
    [InlineData("user-not-found", "UserNotFound")]
    [InlineData("offline", "Offline")]
    [InlineData("not-in-experience", "NotInExperience")]
    [InlineData("not-joinable", "NotJoinable")]
    [InlineData("unexpected", "ServiceUnavailable")]
    public void ParseJoinUserResponse_UnavailableStatusDoesNotInventLocation(
        string status,
        string expected)
    {
        using var document = JsonDocument.Parse($$"""
            { "status": "{{status}}", "placeId": 123, "gameId": "{{JobId}}" }
            """);

        var result = RobloxWebSessionService.ParseJoinUserResponse(
            document.RootElement);

        Assert.Equal(expected, result.Availability.ToString());
        Assert.Null(result.Resolution);
    }

    [Theory]
    [InlineData(0, "a18c877e-4070-4a84-a5f7-36668b46a77d")]
    [InlineData(123, "not-a-guid")]
    public void ParseJoinUserResponse_InvalidAvailableLocationFailsClosed(
        long placeId,
        string gameId)
    {
        using var document = JsonDocument.Parse($$"""
            {
              "status": "available",
              "user": { "id": 42, "name": "Builderman", "displayName": "Builder Man" },
              "placeId": {{placeId}},
              "gameId": "{{gameId}}"
            }
            """);

        var result = RobloxWebSessionService.ParseJoinUserResponse(
            document.RootElement);

        Assert.Equal(JoinUserAvailability.ServiceUnavailable, result.Availability);
        Assert.Null(result.Resolution);
    }

    [Fact]
    public void ParseJoinUserResponse_NonStringStatusFailsClosed()
    {
        using var document = JsonDocument.Parse("""
            { "status": 2, "user": {}, "placeId": 123, "gameId": null }
            """);

        var result = RobloxWebSessionService.ParseJoinUserResponse(
            document.RootElement);

        Assert.Equal(JoinUserAvailability.ServiceUnavailable, result.Availability);
        Assert.Null(result.Resolution);
    }

    [Fact]
    public void ResolveJoinUserScript_UsesOfficialEndpointsAndSelectedSessionCookies()
    {
        var script = RobloxWebScripts.ResolveJoinUser(
            "request-id",
            new JoinUserIdentifier(null, "Builderman", "@Builderman"));

        Assert.Contains("https://users.roblox.com/v1/usernames/users", script);
        Assert.Contains("https://presence.roblox.com/v1/presence/users", script);
        Assert.Equal(3, CountOccurrences(script, "credentials: 'include'"));
        Assert.Contains("const requestedUsername = \"Builderman\";", script);
    }

    [Fact]
    public void ResolveJoinUserScript_JsonEncodesUsername()
    {
        var script = RobloxWebScripts.ResolveJoinUser(
            "request-id",
            new JoinUserIdentifier(
                null,
                "bad\"; window.evil = true; //",
                "unused"));

        Assert.DoesNotContain(
            "const requestedUsername = \"bad\"; window.evil",
            script,
            StringComparison.Ordinal);
        Assert.Contains("\\u0022", script, StringComparison.Ordinal);
    }

    private static int CountOccurrences(string value, string expected) =>
        value.Split(expected, StringSplitOptions.None).Length - 1;
}
