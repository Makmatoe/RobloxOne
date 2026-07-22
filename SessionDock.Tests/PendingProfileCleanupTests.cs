using System.Diagnostics;
using SessionDock.Models;
using SessionDock.Services;

namespace SessionDock.Tests;

public sealed class PendingProfileCleanupTests : IDisposable
{
    private readonly string _storageDirectory = Path.Combine(
        Path.GetTempPath(),
        $"SessionDock-shutdown-tests-{Guid.NewGuid():N}");

    [Fact]
    public async Task TryDeleteAsync_UnlockedProfile_DeletesBeforeDeadline()
    {
        var service = new SettingsService(_storageDirectory);
        var profile = CreateProfile();
        var profileDirectory = service.GetSessionDataDirectory(profile);
        Directory.CreateDirectory(profileDirectory);
        File.WriteAllText(Path.Combine(profileDirectory, "Cookies"), "local-data");

        var deleted = await PendingProfileCleanup.TryDeleteAsync(
            cancellationToken => service.DeleteSessionDataAsync(
                profile,
                cancellationToken),
            TimeSpan.FromSeconds(1));

        Assert.True(deleted);
        Assert.False(Directory.Exists(profileDirectory));
    }

    [Fact]
    public async Task TryDeleteAsync_LockedProfile_ReturnsAtDeadline()
    {
        var service = new SettingsService(_storageDirectory);
        var profile = CreateProfile();
        var profileDirectory = service.GetSessionDataDirectory(profile);
        Directory.CreateDirectory(profileDirectory);
        var lockedPath = Path.Combine(profileDirectory, "WebView.lock");
        File.WriteAllText(lockedPath, "locked");
        using var lockedFile = new FileStream(
            lockedPath,
            FileMode.Open,
            FileAccess.ReadWrite,
            FileShare.None);
        var stopwatch = Stopwatch.StartNew();

        var deleted = await PendingProfileCleanup.TryDeleteAsync(
            cancellationToken => service.DeleteSessionDataAsync(
                profile,
                cancellationToken),
            TimeSpan.FromMilliseconds(75));

        stopwatch.Stop();
        Assert.False(deleted);
        Assert.True(Directory.Exists(profileDirectory));
        Assert.InRange(
            stopwatch.Elapsed,
            TimeSpan.FromMilliseconds(25),
            TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task TryDeleteAsync_UnresponsiveCleanupIsCanceledAtDeadline()
    {
        var cancellationObserved = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);

        var deleted = await PendingProfileCleanup.TryDeleteAsync(
            async cancellationToken =>
            {
                try
                {
                    await Task.Delay(
                        Timeout.InfiniteTimeSpan,
                        cancellationToken);
                    return true;
                }
                catch (OperationCanceledException)
                {
                    cancellationObserved.TrySetResult();
                    throw;
                }
            },
            TimeSpan.FromMilliseconds(75));

        Assert.False(deleted);
        await cancellationObserved.Task.WaitAsync(
            TimeSpan.FromSeconds(1),
            TestContext.Current.CancellationToken);
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(false, false)]
    public void CanDelete_IndeterminateShutdownPreservesProfile(
        bool operationsDrained,
        bool finalSettingsSaved)
    {
        var profile = CreateProfile();

        var canDelete = PendingProfileCleanup.CanDelete(
            operationsDrained,
            finalSettingsSaved,
            profile,
            profile,
            new AppSettings());

        Assert.False(canDelete);
    }

    [Fact]
    public void CanDelete_DurableOrReplacedPendingProfileIsPreserved()
    {
        var profile = CreateProfile();
        var settings = new AppSettings { Accounts = [profile] };

        Assert.False(PendingProfileCleanup.CanDelete(
            operationsDrained: true,
            finalSettingsSaved: true,
            profile,
            profile,
            settings));
        Assert.False(PendingProfileCleanup.CanDelete(
            operationsDrained: true,
            finalSettingsSaved: true,
            profile,
            CreateProfile(),
            new AppSettings()));
    }

    [Fact]
    public void CanDelete_DrainedDurableIncompleteProfileCanBeRemoved()
    {
        var profile = CreateProfile();

        var canDelete = PendingProfileCleanup.CanDelete(
            operationsDrained: true,
            finalSettingsSaved: true,
            profile,
            profile,
            new AppSettings());

        Assert.True(canDelete);
    }

    public void Dispose()
    {
        if (Directory.Exists(_storageDirectory))
            Directory.Delete(_storageDirectory, recursive: true);
    }

    private static AccountProfile CreateProfile()
    {
        var key = Guid.NewGuid().ToString("N");
        return new AccountProfile
        {
            Key = key,
            SessionFolder = $@"Profiles\{key}"
        };
    }
}
