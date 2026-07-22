using SessionDock.Services;

namespace SessionDock.Tests;

public sealed class RunningClientsDialogTests
{
    [Fact]
    public async Task CloseIdentitySnapshot_InvokesOnlyConfirmedIdentities()
    {
        var first = CreateIdentity(101);
        var second = CreateIdentity(202);
        var newlyLaunched = CreateIdentity(303);
        var invoked = new List<RobloxClientProcessIdentity>();

        var results = await RunningClientsDialog.CloseIdentitySnapshotAsync(
            [first, second],
            identity =>
            {
                invoked.Add(identity);
                return Task.FromResult(new CloseRobloxClientResult(
                    CloseRobloxClientStatus.Closed));
            });

        Assert.Equal([first, second], invoked);
        Assert.DoesNotContain(newlyLaunched, invoked);
        Assert.Equal(2, results.Length);
    }

    [Fact]
    public void CloseStatus_ReportsEveryUntouchedProcessCategory()
    {
        var closeStatus = RunningClientsDialog.GetCloseDisplayedStatus(
        [
            CloseRobloxClientStatus.Closed,
            CloseRobloxClientStatus.IdentityMismatch,
            CloseRobloxClientStatus.Failed
        ]);

        var status = RunningClientsDialog.AppendUnverifiedWarning(
            closeStatus,
            unverifiedCount: 1);

        Assert.Contains("Closed one verified Roblox client", status);
        Assert.Contains("changed identity and was left untouched", status);
        Assert.Contains("could not be closed", status);
        Assert.Contains(
            "One unverified Roblox-named process was left untouched",
            status);
    }

    private static RobloxClientProcessIdentity CreateIdentity(int processId) =>
        new(
            processId,
            new DateTime(2026, 7, 22, 12, 30, 0, DateTimeKind.Utc),
            @"C:\TestData\Roblox\RobloxPlayerBeta.exe");
}
