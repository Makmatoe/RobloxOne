using RobloxOneLauncher.Services;

namespace RobloxOneLauncher.Tests;

public sealed class UpdateUriPolicyTests
{
    private static readonly Uri InitialUri = new(
        "https://github.com/Makmatoe/RobloxOne/releases/download/v2.1.0/robloxone-release.json");

    [Theory]
    [InlineData("https://github.com/Makmatoe/RobloxOne/releases/download/v2.1.0/robloxone-release.json")]
    [InlineData("https://release-assets.githubusercontent.com/github-production-release-asset/file?token=value")]
    [InlineData("https://objects.githubusercontent.com/github-production-release-asset/file?token=value")]
    public void IsAllowedManifestUri_CanonicalOrGitHubAssetUri_IsAccepted(
        string value)
    {
        Assert.True(RobloxUpdateService.IsAllowedManifestUri(
            new Uri(value),
            InitialUri));
    }

    [Theory]
    [InlineData("http://release-assets.githubusercontent.com/file")]
    [InlineData("https://github.com/other/repository/file")]
    [InlineData("https://github.example/redirect")]
    [InlineData("https://user@release-assets.githubusercontent.com/file")]
    [InlineData("https://release-assets.githubusercontent.com:444/file")]
    [InlineData("https://release-assets.githubusercontent.com/file#fragment")]
    public void IsAllowedManifestUri_UnexpectedRedirect_IsRejected(string value)
    {
        Assert.False(RobloxUpdateService.IsAllowedManifestUri(
            new Uri(value),
            InitialUri));
    }
}
