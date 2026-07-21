using RobloxOneLauncher.Models;
using RobloxOneLauncher.Services;

namespace RobloxOneLauncher.Tests;

public sealed class DestinationParserTests
{
    [Theory]
    [InlineData("123456", 123456)]
    [InlineData("  987654321  ", 987654321)]
    public void TryParse_PositivePlaceId_ReturnsPublicTarget(
        string input,
        long expectedPlaceId)
    {
        var parsed = DestinationParser.TryParse(input, out var target, out var error);

        Assert.True(parsed, error);
        Assert.Equal(new LaunchTarget(expectedPlaceId, null, null), target);
        Assert.False(target!.IsPrivateServer);
    }

    [Theory]
    [InlineData("Abc_12-Z", "Abc_12-Z")]
    [InlineData("code=Abc_12-Z", "Abc_12-Z")]
    [InlineData(" CODE=Abc_12-Z ", "Abc_12-Z")]
    public void TryParse_ShareCode_ReturnsPrivateTarget(
        string input,
        string expectedShareCode)
    {
        var parsed = DestinationParser.TryParse(input, out var target, out var error);

        Assert.True(parsed, error);
        Assert.Equal(new LaunchTarget(0, null, expectedShareCode), target);
        Assert.True(target!.IsPrivateServer);
    }

    [Fact]
    public void TryParse_OfficialShareLink_ExtractsDecodedCode()
    {
        const string input = "https://www.roblox.com/share?code=Abc_12-Z&type=Server";

        var parsed = DestinationParser.TryParse(input, out var target, out var error);

        Assert.True(parsed, error);
        Assert.Equal(new LaunchTarget(0, null, "Abc_12-Z"), target);
    }

    [Theory]
    [InlineData(
        "https://www.roblox.com/games/123456/My-Game?privateServerLinkCode=Private%2DCode",
        "Private-Code")]
    [InlineData(
        "https://roblox.com/games/123456/My-Game?linkCode=Legacy_Code",
        "Legacy_Code")]
    public void TryParse_OfficialGameLink_ExtractsPrivateServerCode(
        string input,
        string expectedLinkCode)
    {
        var parsed = DestinationParser.TryParse(input, out var target, out var error);

        Assert.True(parsed, error);
        Assert.Equal(new LaunchTarget(123456, expectedLinkCode, null), target);
    }

    [Fact]
    public void TryParse_OfficialGameLinkWithoutCode_ReturnsPublicTarget()
    {
        var parsed = DestinationParser.TryParse(
            "https://www.roblox.com/games/123456/My-Game",
            out var target,
            out var error);

        Assert.True(parsed, error);
        Assert.Equal(new LaunchTarget(123456, null, null), target);
    }

    [Theory]
    [InlineData("http://www.roblox.com/games/123456")]
    [InlineData("https://roblox.com.example.test/games/123456")]
    [InlineData("https://example.test/games/123456?code=Abc_12-Z")]
    public void TryParse_UntrustedUrl_IsRejected(string input)
    {
        var parsed = DestinationParser.TryParse(input, out var target, out var error);

        Assert.False(parsed);
        Assert.Null(target);
        Assert.Equal("Only official roblox.com links are accepted.", error);
    }

    [Theory]
    [InlineData("")]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("short")]
    [InlineData("contains spaces")]
    [InlineData("https://www.roblox.com/home")]
    public void TryParse_InvalidDestination_IsRejected(string input)
    {
        var parsed = DestinationParser.TryParse(input, out var target, out _);

        Assert.False(parsed);
        Assert.Null(target);
    }
}
