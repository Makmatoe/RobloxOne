using System.Text.Json;
using SessionDock.Models;
using SessionDock.Services;

namespace SessionDock.Tests;

public sealed class AppDataPathsTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        $"SessionDock-path-tests-{Guid.NewGuid():N}");

    [Fact]
    public void ResolveForDirectories_MovesLegacyDataToSessionDock()
    {
        var preferred = Path.Combine(_root, "SessionDock");
        var legacy = Path.Combine(_root, "RobloxOne");
        Directory.CreateDirectory(legacy);
        File.WriteAllText(Path.Combine(legacy, "settings.json"), "saved-data");

        var resolved = AppDataPaths.ResolveForDirectories(preferred, legacy);

        Assert.Equal(preferred, resolved);
        Assert.False(Directory.Exists(legacy));
        Assert.Equal(
            "saved-data",
            File.ReadAllText(Path.Combine(preferred, "settings.json")));
    }

    [Fact]
    public void ResolveForDirectories_PrefersExistingSessionDockData()
    {
        var preferred = Path.Combine(_root, "SessionDock");
        var legacy = Path.Combine(_root, "RobloxOne");
        Directory.CreateDirectory(preferred);
        Directory.CreateDirectory(legacy);
        File.WriteAllText(Path.Combine(preferred, "settings.json"), "current");
        File.WriteAllText(Path.Combine(legacy, "settings.json"), "legacy");

        var resolved = AppDataPaths.ResolveForDirectories(preferred, legacy);

        Assert.Equal(preferred, resolved);
        Assert.Equal(
            "current",
            File.ReadAllText(Path.Combine(preferred, "settings.json")));
        Assert.True(Directory.Exists(legacy));
    }

    [Fact]
    public void ResolveForDirectories_MergesLegacyDataWithoutOverwritingCurrentFiles()
    {
        var preferred = Path.Combine(_root, "SessionDock");
        var legacy = Path.Combine(_root, "RobloxOne");
        Directory.CreateDirectory(preferred);
        Directory.CreateDirectory(Path.Combine(legacy, "Profiles", "account"));
        File.WriteAllText(Path.Combine(preferred, "handlescope.json"), "current");
        File.WriteAllText(Path.Combine(legacy, "settings.json"), "legacy-settings");
        File.WriteAllText(
            Path.Combine(legacy, "Profiles", "account", "Cookies"),
            "session");

        var resolved = AppDataPaths.ResolveForDirectories(preferred, legacy);

        Assert.Equal(preferred, resolved);
        Assert.False(Directory.Exists(legacy));
        Assert.Equal(
            "current",
            File.ReadAllText(Path.Combine(preferred, "handlescope.json")));
        Assert.Equal(
            "legacy-settings",
            File.ReadAllText(Path.Combine(preferred, "settings.json")));
        Assert.Equal(
            "session",
            File.ReadAllText(Path.Combine(
                preferred,
                "Profiles",
                "account",
                "Cookies")));
    }

    [Fact]
    public void ResolveForDirectories_DualSettingsRoots_PreservesLegacyProfileDuringCleanup()
    {
        var preferred = Path.Combine(_root, "SessionDock");
        var legacy = Path.Combine(_root, "RobloxOne");
        var currentKey = Guid.NewGuid().ToString("N");
        var legacyKey = Guid.NewGuid().ToString("N");
        var currentProfile = Path.Combine(preferred, "Profiles", currentKey);
        var legacyProfile = Path.Combine(legacy, "Profiles", legacyKey);
        Directory.CreateDirectory(currentProfile);
        Directory.CreateDirectory(legacyProfile);
        WriteSettings(preferred, currentKey, 101, "current-user");
        WriteSettings(legacy, legacyKey, 202, "legacy-user");
        var sentinel = Path.Combine(legacyProfile, "Cookies");
        File.WriteAllText(sentinel, "legacy-session");

        var resolved = AppDataPaths.ResolveForDirectories(preferred, legacy);
        var settingsService = new SettingsService(resolved);
        var loaded = settingsService.Load();
        var removed = settingsService.CleanupOrphanedSessionDirectories(
            loaded,
            TestContext.Current.CancellationToken);

        Assert.Equal(preferred, resolved);
        Assert.Equal(currentKey, Assert.Single(loaded.Accounts).Key);
        Assert.Equal(0, removed);
        Assert.False(settingsService.CanReconcileProfiles);
        var loadNotice = Assert.IsType<string>(settingsService.LoadNotice);
        Assert.Contains(
            "RobloxOne",
            loadNotice,
            StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(
            preferred,
            AppDataPaths.MigrationConflictFileName)));
        Assert.True(File.Exists(sentinel));
        Assert.True(File.Exists(Path.Combine(legacy, "settings.json")));
        Assert.False(Directory.Exists(Path.Combine(preferred, "Profiles", legacyKey)));
    }

    [Fact]
    public void ResolveForDirectories_PreexistingPartialMigration_PreservesMovedLegacyProfile()
    {
        var preferred = Path.Combine(_root, "SessionDock");
        var legacy = Path.Combine(_root, "RobloxOne");
        var currentKey = Guid.NewGuid().ToString("N");
        var legacyKey = Guid.NewGuid().ToString("N");
        var movedLegacyProfile = Path.Combine(preferred, "Profiles", legacyKey);
        Directory.CreateDirectory(movedLegacyProfile);
        Directory.CreateDirectory(legacy);
        WriteSettings(preferred, currentKey, 101, "current-user");
        WriteSettings(legacy, legacyKey, 202, "legacy-user");
        var sentinel = Path.Combine(movedLegacyProfile, "Cookies");
        File.WriteAllText(sentinel, "legacy-session");

        var resolved = AppDataPaths.ResolveForDirectories(preferred, legacy);
        var settingsService = new SettingsService(resolved);
        var loaded = settingsService.Load();
        var removed = settingsService.CleanupOrphanedSessionDirectories(
            loaded,
            TestContext.Current.CancellationToken);

        Assert.Equal(0, removed);
        Assert.False(settingsService.CanReconcileProfiles);
        Assert.True(File.Exists(sentinel));
        Assert.True(File.Exists(Path.Combine(legacy, "settings.json")));
    }

    [Fact]
    public void ResolveForDirectories_MergeFailureAfterProfileMove_PausesCleanup()
    {
        var preferred = Path.Combine(_root, "SessionDock");
        var legacy = Path.Combine(_root, "RobloxOne");
        var legacyKey = Guid.NewGuid().ToString("N");
        Directory.CreateDirectory(preferred);
        var legacyProfile = Path.Combine(legacy, "Profiles", legacyKey);
        Directory.CreateDirectory(legacyProfile);
        File.WriteAllText(Path.Combine(preferred, "handlescope.json"), "current");
        WriteSettings(legacy, legacyKey, 202, "legacy-user");
        var sentinelName = "Cookies";
        File.WriteAllText(
            Path.Combine(legacyProfile, sentinelName),
            "legacy-session");
        using var settingsLock = new FileStream(
            Path.Combine(legacy, "settings.json"),
            FileMode.Open,
            FileAccess.Read,
            FileShare.None);

        var resolved = AppDataPaths.ResolveForDirectories(preferred, legacy);
        var settingsService = new SettingsService(resolved);
        var loaded = settingsService.Load();
        var removed = settingsService.CleanupOrphanedSessionDirectories(
            loaded,
            TestContext.Current.CancellationToken);

        Assert.Equal(0, removed);
        Assert.False(settingsService.CanReconcileProfiles);
        Assert.Contains(
            "did not finish cleanly",
            Assert.IsType<string>(settingsService.LoadNotice),
            StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(
            preferred,
            AppDataPaths.MigrationInProgressFileName)));
        Assert.True(File.Exists(Path.Combine(
            preferred,
            "Profiles",
            legacyKey,
            sentinelName)));
        Assert.True(File.Exists(Path.Combine(legacy, "settings.json")));
    }

    [Fact]
    public void ResolveForDirectories_UnverifiableMigrationCompletion_KeepsGuard()
    {
        var preferred = Path.Combine(_root, "SessionDock");
        var legacy = Path.Combine(_root, "RobloxOne");
        var legacyKey = Guid.NewGuid().ToString("N");
        Directory.CreateDirectory(preferred);
        Directory.CreateDirectory(legacy);
        File.WriteAllText(Path.Combine(preferred, "handlescope.json"), "current");
        WriteSettings(legacy, legacyKey, 202, "legacy-user");
        var legacyProfile = Path.Combine(legacy, "Profiles", legacyKey);
        Directory.CreateDirectory(legacyProfile);
        File.WriteAllText(Path.Combine(legacyProfile, "Cookies"), "legacy-session");

        var resolved = AppDataPaths.ResolveForDirectories(
            preferred,
            legacy,
            _ => throw new UnauthorizedAccessException(
                "The legacy root could not be positively inspected."));
        var settingsService = new SettingsService(resolved);
        var loaded = settingsService.Load();
        var removed = settingsService.CleanupOrphanedSessionDirectories(
            loaded,
            TestContext.Current.CancellationToken);

        Assert.Equal(0, removed);
        Assert.False(settingsService.CanReconcileProfiles);
        Assert.True(File.Exists(Path.Combine(
            preferred,
            AppDataPaths.MigrationInProgressFileName)));
        Assert.True(File.Exists(Path.Combine(
            preferred,
            "Profiles",
            legacyKey,
            "Cookies")));
    }

    [Fact]
    public void ResolveForDirectories_OneSettingsRoot_MigratesReferencedLegacyProfile()
    {
        var preferred = Path.Combine(_root, "SessionDock");
        var legacy = Path.Combine(_root, "RobloxOne");
        var legacyKey = Guid.NewGuid().ToString("N");
        Directory.CreateDirectory(preferred);
        var legacyProfile = Path.Combine(legacy, "Profiles", legacyKey);
        Directory.CreateDirectory(legacyProfile);
        File.WriteAllText(Path.Combine(preferred, "handlescope.json"), "current");
        WriteSettings(legacy, legacyKey, 202, "legacy-user");
        var sentinel = Path.Combine(legacyProfile, "Cookies");
        File.WriteAllText(sentinel, "legacy-session");

        var resolved = AppDataPaths.ResolveForDirectories(preferred, legacy);
        var settingsService = new SettingsService(resolved);
        var loaded = settingsService.Load();
        var removed = settingsService.CleanupOrphanedSessionDirectories(
            loaded,
            TestContext.Current.CancellationToken);

        Assert.Equal(0, removed);
        Assert.True(settingsService.CanReconcileProfiles);
        Assert.Equal(legacyKey, Assert.Single(loaded.Accounts).Key);
        Assert.True(File.Exists(Path.Combine(
            preferred,
            "Profiles",
            legacyKey,
            "Cookies")));
        Assert.False(Directory.Exists(legacy));
        Assert.False(File.Exists(Path.Combine(
            preferred,
            AppDataPaths.MigrationConflictFileName)));
        Assert.False(File.Exists(Path.Combine(
            preferred,
            AppDataPaths.MigrationInProgressFileName)));
    }

    [Theory]
    [InlineData("settings.json", "settings.backup.json")]
    [InlineData("settings.backup.json", "settings.json")]
    public void ResolveForDirectories_PrimaryOrBackupInBothRoots_PreservesLegacyData(
        string preferredSettingsFile,
        string legacySettingsFile)
    {
        var preferred = Path.Combine(_root, "SessionDock");
        var legacy = Path.Combine(_root, "RobloxOne");
        var currentKey = Guid.NewGuid().ToString("N");
        var legacyKey = Guid.NewGuid().ToString("N");
        Directory.CreateDirectory(Path.Combine(preferred, "Profiles", currentKey));
        var legacyProfile = Path.Combine(legacy, "Profiles", legacyKey);
        Directory.CreateDirectory(legacyProfile);
        WriteSettings(
            preferred,
            currentKey,
            101,
            "current-user",
            preferredSettingsFile);
        WriteSettings(
            legacy,
            legacyKey,
            202,
            "legacy-user",
            legacySettingsFile);
        var sentinel = Path.Combine(legacyProfile, "Cookies");
        File.WriteAllText(sentinel, "legacy-session");

        var resolved = AppDataPaths.ResolveForDirectories(preferred, legacy);
        var settingsService = new SettingsService(resolved);
        var loaded = settingsService.Load();
        var removed = settingsService.CleanupOrphanedSessionDirectories(
            loaded,
            TestContext.Current.CancellationToken);

        Assert.Equal(currentKey, Assert.Single(loaded.Accounts).Key);
        Assert.Equal(0, removed);
        Assert.False(settingsService.CanReconcileProfiles);
        Assert.True(File.Exists(sentinel));
        Assert.True(File.Exists(Path.Combine(legacy, legacySettingsFile)));
    }

    [Fact]
    public void ResolveForDirectories_RejectsTheSamePathForBothIdentities()
    {
        var path = Path.Combine(_root, "SessionDock");

        Assert.Throws<ArgumentException>(() =>
            AppDataPaths.ResolveForDirectories(path, path));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private static void WriteSettings(
        string root,
        string key,
        long userId,
        string username,
        string fileName = "settings.json")
    {
        var settings = new AppSettings
        {
            Accounts =
            [
                new AccountProfile
                {
                    Key = key,
                    UserId = userId,
                    Username = username,
                    SessionFolder = $@"Profiles\{key}"
                }
            ],
            ActiveAccountKey = key
        };
        File.WriteAllText(
            Path.Combine(root, fileName),
            JsonSerializer.Serialize(settings));
    }
}
