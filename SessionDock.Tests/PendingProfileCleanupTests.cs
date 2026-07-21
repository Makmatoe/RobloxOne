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
