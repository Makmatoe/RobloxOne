using System.Collections.Concurrent;
using System.Text.Json;
using SessionDock.Models;
using SessionDock.Services;

namespace SessionDock.Tests;

public sealed class SerializedSettingsWriterTests
{
    [Fact]
    public async Task SaveAsync_UsesImmutableSnapshotsInArrivalOrder()
    {
        using var releaseFirst = new ManualResetEventSlim();
        var firstStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var savedLabels = new ConcurrentQueue<string>();
        var saveCount = 0;
        var writer = new SerializedSettingsWriter(settings =>
        {
            if (Interlocked.Increment(ref saveCount) == 1)
            {
                firstStarted.TrySetResult();
                releaseFirst.Wait();
            }
            savedLabels.Enqueue(Assert.Single(settings.Accounts).Label!);
        });
        var firstSettings = CreateSettings("First");
        var secondSettings = CreateSettings("Second");

        var firstSave = writer.SaveAsync(firstSettings);
        await firstStarted.Task;
        var secondSave = writer.SaveAsync(secondSettings);
        firstSettings.Accounts[0].Label = "Mutated after enqueue";
        secondSettings.Accounts[0].Label = "Also mutated";

        Assert.Equal(1, Volatile.Read(ref saveCount));
        releaseFirst.Set();
        await Task.WhenAll(firstSave, secondSave);

        Assert.Equal(["First", "Second"], savedLabels);
    }

    [Fact]
    public async Task SaveAsync_FailedWriteDoesNotBlockNewerWrite()
    {
        var attempts = new ConcurrentQueue<string>();
        var writer = new SerializedSettingsWriter(settings =>
        {
            var label = Assert.Single(settings.Accounts).Label!;
            attempts.Enqueue(label);
            if (label == "First")
                throw new IOException("disk unavailable");
        });

        var firstSave = writer.SaveAsync(CreateSettings("First"));
        var secondSave = writer.SaveAsync(CreateSettings("Second"));

        await Assert.ThrowsAsync<IOException>(() => firstSave);
        await secondSave;
        Assert.Equal(["First", "Second"], attempts);
    }

    [Fact]
    public async Task SaveAsync_SnapshotOwnsEveryPersistedSettingsField()
    {
        string? savedJson = null;
        var writer = new SerializedSettingsWriter(settings =>
            savedJson = JsonSerializer.Serialize(settings));
        var settings = CreateSettings("Original");
        settings.Accounts[0].Destination = "123";
        settings.Accounts[0].ColorHex = "#123456";
        settings.RecentExperiences.Add(new RecentExperience
        {
            Destination = "456",
            PlaceId = 456,
            Name = "Experience",
            CustomName = "Favorite",
            IsPrivateServer = true,
            IsPinned = true,
            ServerJobId = Guid.NewGuid().ToString("D"),
            AccountUserId = 42,
            AccountUsername = "builder",
            LastLaunchedAt = DateTimeOffset.Parse("2026-01-02T03:04:05Z")
        });
        settings.UiSoundsEnabled = false;
        settings.StartupSound = "custom";
        settings.CustomStartupSoundFileName = "startup-custom.wav";
        settings.PendingProfileDeletionKeys = [Guid.NewGuid().ToString("N")];
        settings.LockedUserId = 7;
        settings.LockedUsername = "legacy";
        settings.PlaceId = 99;
        settings.Destination = "legacy-destination";
        var expectedJson = JsonSerializer.Serialize(settings);

        var save = writer.SaveAsync(settings);
        settings.Accounts[0].Destination = "mutated";
        settings.RecentExperiences[0].CustomName = "mutated";
        settings.RecentExperiences.Clear();
        settings.UiSoundsEnabled = true;
        settings.LockedUsername = "mutated";
        await save;

        Assert.Equal(expectedJson, savedJson);
    }

    [Fact]
    public async Task SaveAsync_ReturnsWhilePhysicalWriteIsBlocked()
    {
        using var releaseWrite = new ManualResetEventSlim();
        var writeStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var callReturned = new TaskCompletionSource<Task>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var writer = new SerializedSettingsWriter(_ =>
        {
            writeStarted.TrySetResult();
            releaseWrite.Wait();
        });
        var caller = new Thread(() =>
        {
            try
            {
                callReturned.TrySetResult(
                    writer.SaveAsync(CreateSettings("Background")));
            }
            catch (Exception exception)
            {
                callReturned.TrySetException(exception);
            }
        });

        caller.Start();
        await writeStarted.Task.WaitAsync(
            TimeSpan.FromSeconds(2),
            TestContext.Current.CancellationToken);
        Task saveTask;
        try
        {
            saveTask = await callReturned.Task.WaitAsync(
                TimeSpan.FromSeconds(2),
                TestContext.Current.CancellationToken);
            Assert.False(saveTask.IsCompleted);
        }
        finally
        {
            releaseWrite.Set();
            caller.Join(TimeSpan.FromSeconds(2));
        }

        await saveTask;
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
