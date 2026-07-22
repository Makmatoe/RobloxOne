using SessionDock.Models;
using SessionDock.Services;

namespace SessionDock.Tests;

public sealed class SettingsMutationCoordinatorTests
{
    [Fact]
    public async Task CommitAsync_EarlierFailureRollsBackBeforeLaterMutationRuns()
    {
        using var releaseFirst = new ManualResetEventSlim();
        var firstStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var attempts = 0;
        var settings = CreateSettings("Original");
        var writer = new SerializedSettingsWriter(_ =>
        {
            if (Interlocked.Increment(ref attempts) != 1)
                return;
            firstStarted.TrySetResult();
            releaseFirst.Wait();
            throw new IOException("first write failed");
        });
        var coordinator = new SettingsMutationCoordinator(settings, writer);

        var first = coordinator.CommitAsync(
            () => settings.Accounts[0].Label = "First");
        await firstStarted.Task;
        var secondMutationRan = false;
        var second = coordinator.CommitAsync(() =>
        {
            secondMutationRan = true;
            Assert.Equal("Original", settings.Accounts[0].Label);
            settings.Accounts[0].Label = "Second";
        });

        Assert.False(secondMutationRan);
        Assert.Equal("First", settings.Accounts[0].Label);
        releaseFirst.Set();
        var firstResult = await first;
        var secondResult = await second;

        Assert.False(firstResult.Committed);
        Assert.IsType<IOException>(firstResult.Failure);
        Assert.True(secondResult.Committed);
        Assert.True(secondMutationRan);
        Assert.Equal("Second", settings.Accounts[0].Label);
        Assert.False(coordinator.HasPendingCommits);
    }

    [Fact]
    public async Task CommitAsync_UnexpectedFailureRestoresAndRemainsObservable()
    {
        var settings = CreateSettings("Original");
        var coordinator = new SettingsMutationCoordinator(
            settings,
            new SerializedSettingsWriter(_ =>
                throw new InvalidOperationException("programmer failure")));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            coordinator.CommitAsync(
                () => settings.Accounts[0].Label = "Changed"));

        Assert.Equal("Original", settings.Accounts[0].Label);
        Assert.False(coordinator.HasPendingCommits);
    }

    [Fact]
    public async Task CommitAsync_CommittedCallbackRunsBeforeNextMutation()
    {
        var settings = CreateSettings("Original");
        var coordinator = new SettingsMutationCoordinator(
            settings,
            new SerializedSettingsWriter(_ => { }));
        var firstPromoted = false;

        var first = coordinator.CommitAsync(
            () => settings.Accounts[0].Label = "First",
            () => firstPromoted = true);
        var second = coordinator.CommitAsync(() =>
        {
            Assert.True(firstPromoted);
            Assert.Equal("First", settings.Accounts[0].Label);
            settings.Accounts[0].Label = "Second";
        });

        Assert.True((await first).Committed);
        Assert.True((await second).Committed);
        Assert.Equal("Second", settings.Accounts[0].Label);
    }

    [Fact]
    public async Task CommitAsync_CommittedCallbackFaultRemainsObservableAndQueueContinues()
    {
        var settings = CreateSettings("Original");
        var coordinator = new SettingsMutationCoordinator(
            settings,
            new SerializedSettingsWriter(_ => { }));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            coordinator.CommitAsync(
                () => settings.Accounts[0].Label = "Durable",
                () => throw new InvalidOperationException("UI promotion defect")));
        var next = await coordinator.CommitAsync(() =>
        {
            Assert.Equal("Durable", settings.Accounts[0].Label);
            settings.Accounts[0].Label = "Next";
        });

        Assert.True(next.Committed);
        Assert.Equal("Next", settings.Accounts[0].Label);
    }

    [Fact]
    public async Task CommitAsync_UnauthorizedWriteRestoresOriginalSettings()
    {
        var settings = CreateSettings("Original");
        var coordinator = new SettingsMutationCoordinator(
            settings,
            new SerializedSettingsWriter(_ =>
                throw new UnauthorizedAccessException("write denied")));

        var result = await coordinator.CommitAsync(
            () => settings.Accounts[0].Label = "Changed");

        Assert.False(result.Committed);
        Assert.IsType<UnauthorizedAccessException>(result.Failure);
        Assert.Equal("Original", settings.Accounts[0].Label);
    }

    [Fact]
    public async Task CompleteAsync_QueuesFinalSnapshotLastAndRejectsNewCommits()
    {
        using var releaseFirst = new ManualResetEventSlim();
        var firstStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var savedLabels = new List<string>();
        var settings = CreateSettings("Original");
        var writer = new SerializedSettingsWriter(snapshot =>
        {
            var label = Assert.Single(snapshot.Accounts).Label!;
            lock (savedLabels)
                savedLabels.Add(label);
            if (label == "First")
            {
                firstStarted.TrySetResult();
                releaseFirst.Wait();
            }
        });
        var coordinator = new SettingsMutationCoordinator(settings, writer);

        var first = coordinator.CommitAsync(
            () => settings.Accounts[0].Label = "First");
        await firstStarted.Task;
        var second = coordinator.CommitAsync(
            () => settings.Accounts[0].Label = "Second");
        var completion = coordinator.CompleteAsync(() =>
            AppSettingsSnapshot.Create(settings));
        var rejected = await coordinator.CommitAsync(
            () => settings.Accounts[0].Label = "Too late");

        Assert.True(rejected.Closed);
        releaseFirst.Set();
        Assert.True((await first).Committed);
        Assert.True((await second).Committed);
        await completion;
        Assert.Equal(["First", "Second", "Second"], savedLabels);
        Assert.Equal(3, writer.LastCompletedRevision);
    }

    private static AppSettings CreateSettings(string label)
    {
        var key = Guid.NewGuid().ToString("N");
        return new AppSettings
        {
            Accounts =
            [
                new AccountProfile
                {
                    Key = key,
                    UserId = 42,
                    Username = "builder",
                    Label = label,
                    SessionFolder = $@"Profiles\{key}"
                }
            ],
            ActiveAccountKey = key
        };
    }
}
