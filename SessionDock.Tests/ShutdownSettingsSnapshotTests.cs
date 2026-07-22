using SessionDock.Models;
using SessionDock.Services;

namespace SessionDock.Tests;

public sealed class ShutdownSettingsSnapshotTests
{
    [Fact]
    public void Create_OverlaysDraftCapturedBeforeShutdownWaits()
    {
        var settings = new AppSettings
        {
            Accounts =
            [
                new AccountProfile
                {
                    Key = "one",
                    Destination = "confirmed"
                }
            ],
            ActiveAccountKey = "one"
        };
        var capturedDraft = new DestinationPersistenceRequest(
            "one",
            OwnerEpoch: 3,
            Revision: 7,
            Destination: "captured-edit");

        settings.Accounts[0].Destination = "queued-mutation";
        var snapshot = ShutdownSettingsSnapshot.Create(
            settings,
            capturedDraft,
            capturedDraft);
        settings.Accounts[0].Destination = "later-live-edit";

        Assert.Equal(
            "captured-edit",
            Assert.Single(snapshot.Accounts).Destination);
    }

    [Fact]
    public void Create_RemovedDraftOwnerIsNotResurrected()
    {
        var settings = new AppSettings();
        var capturedDraft = new DestinationPersistenceRequest(
            "removed",
            OwnerEpoch: 3,
            Revision: 7,
            Destination: "captured-edit");

        var snapshot = ShutdownSettingsSnapshot.Create(
            settings,
            capturedDraft,
            capturedDraft);

        Assert.Empty(snapshot.Accounts);
    }

    [Fact]
    public void Create_StaleCapturedDraftDoesNotOverwriteNewerDestination()
    {
        var settings = new AppSettings
        {
            Accounts =
            [
                new AccountProfile
                {
                    Key = "one",
                    Destination = "newer-committed"
                }
            ],
            ActiveAccountKey = "one"
        };
        var capturedDraft = new DestinationPersistenceRequest(
            "one",
            OwnerEpoch: 3,
            Revision: 7,
            Destination: "captured-edit");
        var currentDraft = capturedDraft with
        {
            Revision = 8,
            Destination = "newer-committed"
        };

        var snapshot = ShutdownSettingsSnapshot.Create(
            settings,
            capturedDraft,
            currentDraft);

        Assert.Equal(
            "newer-committed",
            Assert.Single(snapshot.Accounts).Destination);
    }
}
