using SessionDock.Services;
using SessionDock.SystemProcesses;

namespace SessionDock.Tests;

public sealed class RobloxClientServiceTests
{
    [Fact]
    public void CreatePlayerStartInfo_RemovesLaunchHookConfiguration()
    {
        var inheritedEnvironment = new Dictionary<string, string?>(
            StringComparer.OrdinalIgnoreCase)
        {
            [LocalApiLaunchHook.UrlEnvironmentVariable] =
                "http://127.0.0.1:4312/launch",
            [LocalApiLaunchHook.TokenEnvironmentVariable] = "current-secret",
            ["ROBLOX_ONE_LAUNCH_HOOK_URL"] =
                "http://127.0.0.1:4313/legacy",
            ["ROBLOX_ONE_LAUNCH_HOOK_BEARER_TOKEN"] = "legacy-secret",
            ["SESSIONDOCK_ORDINARY_TEST_VALUE"] = "preserve-me"
        };

        var startInfo = RobloxClientService.CreatePlayerStartInfo(
            @"C:\Roblox\RobloxPlayerBeta.exe",
            "roblox-player:1+launchmode:play",
            inheritedEnvironment);

        Assert.False(startInfo.UseShellExecute);
        Assert.Equal(
            @"C:\Roblox\RobloxPlayerBeta.exe",
            startInfo.FileName);
        Assert.Equal(
            ["roblox-player:1+launchmode:play"],
            startInfo.ArgumentList);
        Assert.Equal(
            "preserve-me",
            startInfo.Environment["SESSIONDOCK_ORDINARY_TEST_VALUE"]);
        Assert.DoesNotContain(
            LocalApiLaunchHook.UrlEnvironmentVariable,
            startInfo.Environment.Keys,
            StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain(
            LocalApiLaunchHook.TokenEnvironmentVariable,
            startInfo.Environment.Keys,
            StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain(
            "ROBLOX_ONE_LAUNCH_HOOK_URL",
            startInfo.Environment.Keys,
            StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain(
            "ROBLOX_ONE_LAUNCH_HOOK_BEARER_TOKEN",
            startInfo.Environment.Keys,
            StringComparer.OrdinalIgnoreCase);

        Assert.Equal(
            "current-secret",
            inheritedEnvironment[LocalApiLaunchHook.TokenEnvironmentVariable]);
        Assert.Equal(
            "legacy-secret",
            inheritedEnvironment["ROBLOX_ONE_LAUNCH_HOOK_BEARER_TOKEN"]);
    }
}
