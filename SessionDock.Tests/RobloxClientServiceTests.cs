using SessionDock.Services;
using SessionDock.SystemProcesses;

namespace SessionDock.Tests;

public sealed class RobloxClientServiceTests
{
    private static readonly RobloxClientProcessIdentity ExpectedIdentity = new(
        4128,
        new DateTime(2026, 7, 22, 12, 30, 0, DateTimeKind.Utc),
        @"C:\TestData\Roblox\Versions\version-a\RobloxPlayerBeta.exe");

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

    [Fact]
    public void MatchesPlayerIdentity_AcceptsExactIdentity()
    {
        var matches = RobloxClientService.MatchesPlayerIdentity(
            ExpectedIdentity,
            ExpectedIdentity.ProcessId,
            ExpectedIdentity.StartTimeUtc,
            ExpectedIdentity.ExecutablePath.ToUpperInvariant());

        Assert.True(matches);
    }

    [Theory]
    [InlineData(4129, 0, "same")]
    [InlineData(4128, 1, "same")]
    [InlineData(4128, 0, "different")]
    public void MatchesPlayerIdentity_RejectsPidReuseOrPathChange(
        int processId,
        int addedSeconds,
        string pathMode)
    {
        var executablePath = pathMode == "same"
            ? ExpectedIdentity.ExecutablePath
            : @"C:\TestData\Roblox\Versions\version-b\RobloxPlayerBeta.exe";

        var matches = RobloxClientService.MatchesPlayerIdentity(
            ExpectedIdentity,
            processId,
            ExpectedIdentity.StartTimeUtc.AddSeconds(addedSeconds),
            executablePath);

        Assert.False(matches);
    }

    [Fact]
    public void ClosePlayerCore_ClosesExactVisibleClientGracefully()
    {
        var process = new FakeCloseableRobloxProcess
        {
            HasVisibleWindow = true,
            GracefulCloseResult = true
        };

        var result = RobloxClientService.ClosePlayerCore(
            ExpectedIdentity,
            process,
            CancellationToken.None,
            (_, _, _) =>
            {
                process.HasExited = true;
                return true;
            });

        Assert.Equal(CloseRobloxClientStatus.Closed, result.Status);
        Assert.Equal(1, process.GracefulCloseRequests);
        Assert.Equal(0, process.ForceCloseRequests);
    }

    [Fact]
    public void ClosePlayerCore_LeavesInitiallyMismatchedProcessUntouched()
    {
        var process = new FakeCloseableRobloxProcess
        {
            VerificationResult = _ => false
        };

        var result = RobloxClientService.ClosePlayerCore(
            ExpectedIdentity,
            process,
            CancellationToken.None,
            (_, _, _) => false);

        Assert.Equal(
            CloseRobloxClientStatus.IdentityMismatch,
            result.Status);
        Assert.Equal(0, process.GracefulCloseRequests);
        Assert.Equal(0, process.ForceCloseRequests);
    }

    [Fact]
    public void ClosePlayerCore_DoesNotKillPidReusedDuringGracePeriod()
    {
        var process = new FakeCloseableRobloxProcess
        {
            HasVisibleWindow = true,
            GracefulCloseResult = true,
            VerificationResult = call => call == 1
        };

        var result = RobloxClientService.ClosePlayerCore(
            ExpectedIdentity,
            process,
            CancellationToken.None,
            (_, _, _) => false);

        Assert.Equal(
            CloseRobloxClientStatus.IdentityMismatch,
            result.Status);
        Assert.Equal(2, process.VerificationRequests);
        Assert.Equal(1, process.GracefulCloseRequests);
        Assert.Equal(0, process.ForceCloseRequests);
    }

    [Fact]
    public void ClosePlayerCore_ForceClosesExactBackgroundClientOnly()
    {
        var process = new FakeCloseableRobloxProcess
        {
            HasVisibleWindow = false
        };

        var result = RobloxClientService.ClosePlayerCore(
            ExpectedIdentity,
            process,
            CancellationToken.None,
            (_, _, _) =>
            {
                process.HasExited = true;
                return true;
            });

        Assert.Equal(CloseRobloxClientStatus.Closed, result.Status);
        Assert.Equal(0, process.GracefulCloseRequests);
        Assert.Equal(1, process.ForceCloseRequests);
    }

    [Fact]
    public void ClosePlayerCore_CancellationAfterGracePeriodPreventsForceClose()
    {
        var process = new FakeCloseableRobloxProcess
        {
            HasVisibleWindow = true,
            GracefulCloseResult = true
        };
        using var cancellation = new CancellationTokenSource();

        Assert.Throws<OperationCanceledException>(() =>
            RobloxClientService.ClosePlayerCore(
                ExpectedIdentity,
                process,
                cancellation.Token,
                (_, _, _) =>
                {
                    cancellation.Cancel();
                    return false;
                }));

        Assert.Equal(1, process.GracefulCloseRequests);
        Assert.Equal(0, process.ForceCloseRequests);
    }

    private sealed class FakeCloseableRobloxProcess :
        ICloseableRobloxProcess
    {
        public bool HasExited { get; set; }

        public bool HasVisibleWindow { get; init; }

        public bool GracefulCloseResult { get; init; }

        public int VerificationRequests { get; private set; }

        public int GracefulCloseRequests { get; private set; }

        public int ForceCloseRequests { get; private set; }

        public Func<int, bool> VerificationResult { get; init; } =
            _ => true;

        public bool IsStillVerified(RobloxClientProcessIdentity identity)
        {
            Assert.Same(ExpectedIdentity, identity);
            VerificationRequests++;
            return VerificationResult(VerificationRequests);
        }

        public bool RequestGracefulClose()
        {
            GracefulCloseRequests++;
            return GracefulCloseResult;
        }

        public void ForceClose()
        {
            ForceCloseRequests++;
        }
    }
}
