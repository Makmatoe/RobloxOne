using SessionDock.Models;
using SessionDock.Services;

namespace SessionDock.Tests;

public sealed class SettingsServiceRecoveryTests : IDisposable
{
    private readonly string _storageDirectory = Path.Combine(
        Path.GetTempPath(),
        $"SessionDock-tests-{Guid.NewGuid():N}");

    [Fact]
    public void Load_RecoversValidatedBackup_WhenPrimaryIsUnreadable()
    {
        var service = new SettingsService(_storageDirectory);
        var original = CreateSettings("Original");
        service.Save(original);

        var current = CreateSettings("Current");
        service.Save(current);
        File.WriteAllText(Path.Combine(_storageDirectory, "settings.json"), "{not-json");

        var recoveryService = new SettingsService(_storageDirectory);
        var recovered = recoveryService.Load();

        Assert.Equal("Original", Assert.Single(recovered.Accounts).Label);
        Assert.NotNull(recoveryService.LoadNotice);
        var reloaded = new SettingsService(_storageDirectory).Load();
        Assert.Equal("Original", Assert.Single(reloaded.Accounts).Label);
        Assert.Single(Directory.GetFiles(_storageDirectory, "settings.corrupt-*.json"));
    }

    [Fact]
    public void Load_PreservesProfiles_WhenPrimaryAndBackupAreUnreadable()
    {
        Directory.CreateDirectory(_storageDirectory);
        File.WriteAllText(Path.Combine(_storageDirectory, "settings.json"), "bad");
        File.WriteAllText(Path.Combine(_storageDirectory, "settings.backup.json"), "bad");
        var orphan = Path.Combine(
            _storageDirectory,
            "Profiles",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(orphan);

        var service = new SettingsService(_storageDirectory);
        var loaded = service.Load();
        var removed = service.CleanupOrphanedSessionDirectories(loaded);

        Assert.Empty(loaded.Accounts);
        Assert.False(service.CanReconcileProfiles);
        Assert.Equal(0, removed);
        Assert.True(Directory.Exists(orphan));
        Assert.Equal(2, Directory.GetFiles(_storageDirectory, "*.corrupt-*.json").Length);

        service.Save(loaded);
        var restartedService = new SettingsService(_storageDirectory);
        var restartedSettings = restartedService.Load();
        var removedAfterRestart = restartedService.CleanupOrphanedSessionDirectories(
            restartedSettings);

        Assert.False(restartedService.CanReconcileProfiles);
        Assert.Equal(0, removedAfterRestart);
        Assert.True(Directory.Exists(orphan));
    }

    [Fact]
    public void Load_RestoresBackup_WhenPrimaryFileIsMissing()
    {
        var service = new SettingsService(_storageDirectory);
        service.Save(CreateSettings("Backup"));
        service.Save(CreateSettings("Primary"));
        File.Delete(Path.Combine(_storageDirectory, "settings.json"));

        var recoveryService = new SettingsService(_storageDirectory);
        var recovered = recoveryService.Load();

        Assert.Equal("Backup", Assert.Single(recovered.Accounts).Label);
        Assert.True(File.Exists(Path.Combine(_storageDirectory, "settings.json")));
        Assert.NotNull(recoveryService.LoadNotice);
    }

    [Fact]
    public void CleanupOrphanedSessionDirectories_RemovesOnlyUnreferencedProfiles()
    {
        var service = new SettingsService(_storageDirectory);
        var settings = CreateSettings("Kept");
        var referenced = service.GetSessionDataDirectory(settings.Accounts[0]);
        var orphan = Path.Combine(
            _storageDirectory,
            "Profiles",
            Guid.NewGuid().ToString("N"));
        var unrelated = Path.Combine(_storageDirectory, "Profiles", "notes");
        Directory.CreateDirectory(referenced);
        Directory.CreateDirectory(orphan);
        Directory.CreateDirectory(unrelated);

        var removed = service.CleanupOrphanedSessionDirectories(settings);

        Assert.Equal(1, removed);
        Assert.True(Directory.Exists(referenced));
        Assert.False(Directory.Exists(orphan));
        Assert.True(Directory.Exists(unrelated));
    }

    public void Dispose()
    {
        if (Directory.Exists(_storageDirectory))
            Directory.Delete(_storageDirectory, recursive: true);
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
