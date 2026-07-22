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
        var removed = service.CleanupOrphanedSessionDirectories(
            loaded,
            TestContext.Current.CancellationToken);

        Assert.Empty(loaded.Accounts);
        Assert.False(service.CanReconcileProfiles);
        Assert.Equal(0, removed);
        Assert.True(Directory.Exists(orphan));
        Assert.Equal(2, Directory.GetFiles(_storageDirectory, "*.corrupt-*.json").Length);

        service.Save(loaded);
        var restartedService = new SettingsService(_storageDirectory);
        var restartedSettings = restartedService.Load();
        var removedAfterRestart = restartedService.CleanupOrphanedSessionDirectories(
            restartedSettings,
            TestContext.Current.CancellationToken);

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
    public void Load_PersistentRecoveryWarning_IsShownOnceUntilStateClears()
    {
        var initialService = new SettingsService(_storageDirectory);
        initialService.Save(new AppSettings());
        var cleanupGuard = Path.Combine(
            _storageDirectory,
            "profile-cleanup-paused.txt");
        var receipt = Path.Combine(
            _storageDirectory,
            AppDataPaths.LegacyInstallMigrationReceiptFileName);
        File.WriteAllText(cleanupGuard, "keep recovered profiles");
        File.WriteAllText(receipt, "completed recovery");

        var firstService = new SettingsService(_storageDirectory);
        _ = firstService.Load();

        Assert.Contains(
            "account records are available",
            Assert.IsType<string>(firstService.LoadNotice),
            StringComparison.OrdinalIgnoreCase);
        Assert.False(firstService.CanReconcileProfiles);
        firstService.AcknowledgeLoadNotice();

        var acknowledgedService = new SettingsService(_storageDirectory);
        _ = acknowledgedService.Load();

        Assert.Null(acknowledgedService.LoadNotice);
        Assert.False(acknowledgedService.CanReconcileProfiles);
        Assert.True(File.Exists(Path.Combine(
            _storageDirectory,
            SettingsService.RecoveryNoticeAcknowledgementFileName)));

        // Clearing and recreating the protected state before another Load is
        // still a new recovery event, even though its user-facing text matches.
        File.Delete(cleanupGuard);
        File.Delete(receipt);
        File.WriteAllText(cleanupGuard, "new recovery state");
        File.WriteAllText(receipt, "new completed recovery");
        var repeatedStateService = new SettingsService(_storageDirectory);
        _ = repeatedStateService.Load();

        Assert.NotNull(repeatedStateService.LoadNotice);
        Assert.False(repeatedStateService.CanReconcileProfiles);
        repeatedStateService.AcknowledgeLoadNotice();

        // A locked stale acknowledgement may survive a clean Load. Its old
        // state fingerprint must not suppress a later recovery instance.
        File.Delete(cleanupGuard);
        File.Delete(receipt);
        var acknowledgement = Path.Combine(
            _storageDirectory,
            SettingsService.RecoveryNoticeAcknowledgementFileName);
        using (new FileStream(
                   acknowledgement,
                   FileMode.Open,
                   FileAccess.Read,
                   FileShare.None))
        {
            var cleanService = new SettingsService(_storageDirectory);
            _ = cleanService.Load();

            Assert.Null(cleanService.LoadNotice);
            Assert.True(cleanService.CanReconcileProfiles);
            Assert.True(File.Exists(acknowledgement));
        }

        File.WriteAllText(cleanupGuard, "third recovery state");
        File.WriteAllText(receipt, "third completed recovery");
        var afterFailedDeleteService = new SettingsService(_storageDirectory);
        _ = afterFailedDeleteService.Load();

        Assert.NotNull(afterFailedDeleteService.LoadNotice);
        Assert.False(afterFailedDeleteService.CanReconcileProfiles);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Load_BackupRecovery_PreservesProfileMissingFromOlderBackup(
        bool corruptPrimary)
    {
        var service = new SettingsService(_storageDirectory);
        var settings = CreateSettings("Original");
        service.Save(settings);

        var newerKey = Guid.NewGuid().ToString("N");
        settings.Accounts.Add(new AccountProfile
        {
            Key = newerKey,
            UserId = 99,
            Username = "newer",
            Label = "Newer",
            SessionFolder = $@"Profiles\{newerKey}"
        });
        settings.ActiveAccountKey = newerKey;
        var newerProfile = service.GetSessionDataDirectory(settings.Accounts[1]);
        Directory.CreateDirectory(newerProfile);
        var sentinel = Path.Combine(newerProfile, "Cookies");
        File.WriteAllText(sentinel, "authenticated-session");
        service.Save(settings);

        var primaryPath = Path.Combine(_storageDirectory, "settings.json");
        if (corruptPrimary)
            File.WriteAllText(primaryPath, "{not-json");
        else
            File.Delete(primaryPath);

        var recoveryService = new SettingsService(_storageDirectory);
        var recovered = recoveryService.Load();
        var removed = recoveryService.CleanupOrphanedSessionDirectories(
            recovered,
            TestContext.Current.CancellationToken);

        Assert.Equal("Original", Assert.Single(recovered.Accounts).Label);
        Assert.False(recoveryService.CanReconcileProfiles);
        Assert.Equal(0, removed);
        Assert.True(File.Exists(sentinel));
    }

    [Fact]
    public void Load_ValidEmptyPrimary_PreservesProfileReferencedOnlyByBackup()
    {
        var service = new SettingsService(_storageDirectory);
        var backupSettings = CreateSettings("Backup only");
        service.Save(backupSettings);
        var profile = service.GetSessionDataDirectory(backupSettings.Accounts[0]);
        Directory.CreateDirectory(profile);
        var sentinel = Path.Combine(profile, "Cookies");
        File.WriteAllText(sentinel, "authenticated-session");
        service.Save(new AppSettings());

        var recoveryService = new SettingsService(_storageDirectory);
        var loaded = recoveryService.Load();
        var removed = recoveryService.CleanupOrphanedSessionDirectories(
            loaded,
            TestContext.Current.CancellationToken);

        Assert.Empty(loaded.Accounts);
        Assert.False(recoveryService.CanReconcileProfiles);
        Assert.Equal(0, removed);
        Assert.True(File.Exists(sentinel));
        Assert.True(File.Exists(Path.Combine(
            _storageDirectory,
            "profile-cleanup-paused.txt")));
    }

    [Fact]
    public void Load_ValidPartialPrimary_PreservesProfileReferencedOnlyByBackup()
    {
        var service = new SettingsService(_storageDirectory);
        var settings = CreateSettings("Retained");
        var backupOnlyKey = Guid.NewGuid().ToString("N");
        var backupOnlyAccount = new AccountProfile
        {
            Key = backupOnlyKey,
            UserId = 99,
            Username = "backup-only",
            Label = "Backup only",
            SessionFolder = $@"Profiles\{backupOnlyKey}"
        };
        settings.Accounts.Add(backupOnlyAccount);
        service.Save(settings);
        var backupOnlyProfile = service.GetSessionDataDirectory(backupOnlyAccount);
        Directory.CreateDirectory(backupOnlyProfile);
        var sentinel = Path.Combine(backupOnlyProfile, "Cookies");
        File.WriteAllText(sentinel, "authenticated-session");
        settings.Accounts.Remove(backupOnlyAccount);
        service.Save(settings);

        var recoveryService = new SettingsService(_storageDirectory);
        var loaded = recoveryService.Load();
        var removed = recoveryService.CleanupOrphanedSessionDirectories(
            loaded,
            TestContext.Current.CancellationToken);

        Assert.Equal("Retained", Assert.Single(loaded.Accounts).Label);
        Assert.False(recoveryService.CanReconcileProfiles);
        Assert.Equal(0, removed);
        Assert.True(File.Exists(sentinel));
    }

    [Fact]
    public async Task Load_BackupOnlyProfileWithDurableDeletionIntent_CanBeDeletedExactly()
    {
        var key = Guid.NewGuid().ToString("N");
        var account = new AccountProfile
        {
            Key = key,
            UserId = 42,
            Username = "removed",
            SessionFolder = $@"Profiles\{key}"
        };
        var service = new SettingsService(_storageDirectory);
        service.Save(new AppSettings
        {
            Accounts = [account],
            ActiveAccountKey = key
        });
        var target = service.GetSessionDataDirectory(account);
        Directory.CreateDirectory(target);
        File.WriteAllText(Path.Combine(target, "Cookies"), "authenticated-session");
        var unrelatedKey = Guid.NewGuid().ToString("N");
        var unrelated = Path.Combine(
            _storageDirectory,
            "Profiles",
            unrelatedKey);
        Directory.CreateDirectory(unrelated);
        service.StageProfileDeletion(key);
        service.Save(new AppSettings
        {
            PendingProfileDeletionKeys = [key]
        });

        var recoveryService = new SettingsService(_storageDirectory);
        var loaded = recoveryService.Load();
        var deleted = await recoveryService.DeletePendingProfileOnceAsync(
            key,
            loaded,
            TestContext.Current.CancellationToken);

        Assert.True(recoveryService.CanReconcileProfiles);
        Assert.True(deleted);
        Assert.False(Directory.Exists(target));
        Assert.True(Directory.Exists(unrelated));
    }

    [Fact]
    public void Load_UnreadableBackup_PausesProfileCleanupDespiteValidPrimary()
    {
        var service = new SettingsService(_storageDirectory);
        service.Save(new AppSettings());
        service.Save(new AppSettings());
        File.WriteAllText(
            Path.Combine(_storageDirectory, "settings.backup.json"),
            "{not-json");
        var profile = Path.Combine(
            _storageDirectory,
            "Profiles",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(profile);

        var recoveryService = new SettingsService(_storageDirectory);
        var loaded = recoveryService.Load();
        var removed = recoveryService.CleanupOrphanedSessionDirectories(
            loaded,
            TestContext.Current.CancellationToken);

        Assert.Empty(loaded.Accounts);
        Assert.False(recoveryService.CanReconcileProfiles);
        Assert.Equal(0, removed);
        Assert.True(Directory.Exists(profile));
    }

    [Fact]
    public void Load_DiscardedInvalidAccount_PreservesItsProfile()
    {
        var key = Guid.NewGuid().ToString("N");
        Directory.CreateDirectory(_storageDirectory);
        File.WriteAllText(
            Path.Combine(_storageDirectory, "settings.json"),
            $$"""
              {
                "accounts": [
                  {
                    "key": "{{key}}",
                    "userId": 42,
                    "username": "",
                    "sessionFolder": "Profiles\\{{key}}"
                  }
                ],
                "recentExperiences": [],
                "uiSoundsEnabled": true,
                "startupSound": "soft"
              }
              """);
        var profile = Path.Combine(_storageDirectory, "Profiles", key);
        Directory.CreateDirectory(profile);
        var sentinel = Path.Combine(profile, "Cookies");
        File.WriteAllText(sentinel, "authenticated-session");

        var service = new SettingsService(_storageDirectory);
        var loaded = service.Load();
        var removed = service.CleanupOrphanedSessionDirectories(
            loaded,
            TestContext.Current.CancellationToken);

        Assert.Empty(loaded.Accounts);
        Assert.False(service.CanReconcileProfiles);
        Assert.Equal(0, removed);
        Assert.True(File.Exists(sentinel));
    }

    [Fact]
    public void Load_ProfileCleanupGuardPathAsDirectory_RemainsBlocked()
    {
        Directory.CreateDirectory(_storageDirectory);
        Directory.CreateDirectory(Path.Combine(
            _storageDirectory,
            "profile-cleanup-paused.txt"));
        var orphan = Path.Combine(
            _storageDirectory,
            "Profiles",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(orphan);

        var service = new SettingsService(_storageDirectory);
        var settings = service.Load();
        var removed = service.CleanupOrphanedSessionDirectories(
            settings,
            TestContext.Current.CancellationToken);

        Assert.False(service.CanReconcileProfiles);
        Assert.Equal(0, removed);
        Assert.True(Directory.Exists(orphan));
    }

    [Fact]
    public void Load_MissingOnlySettingsFile_PreservesExistingProfile()
    {
        var service = new SettingsService(_storageDirectory);
        var settings = CreateSettings("Only revision");
        service.Save(settings);
        var profile = service.GetSessionDataDirectory(settings.Accounts[0]);
        Directory.CreateDirectory(profile);
        var sentinel = Path.Combine(profile, "Cookies");
        File.WriteAllText(sentinel, "authenticated-session");
        File.Delete(Path.Combine(_storageDirectory, "settings.json"));

        var recoveryService = new SettingsService(_storageDirectory);
        var recovered = recoveryService.Load();
        var removed = recoveryService.CleanupOrphanedSessionDirectories(
            recovered,
            TestContext.Current.CancellationToken);

        Assert.Empty(recovered.Accounts);
        Assert.False(recoveryService.CanReconcileProfiles);
        Assert.Equal(0, removed);
        Assert.True(File.Exists(sentinel));
    }

    [Fact]
    public void Load_InaccessibleSettingsProbe_PreservesExistingProfile()
    {
        var service = new SettingsService(_storageDirectory);
        var settings = CreateSettings("Inaccessible");
        service.Save(settings);
        var profile = service.GetSessionDataDirectory(settings.Accounts[0]);
        Directory.CreateDirectory(profile);
        var sentinel = Path.Combine(profile, "Cookies");
        File.WriteAllText(sentinel, "authenticated-session");
        var settingsPath = Path.Combine(_storageDirectory, "settings.json");

        var recoveryService = new SettingsService(
            _storageDirectory,
            path => path.Equals(settingsPath, StringComparison.OrdinalIgnoreCase)
                ? throw new UnauthorizedAccessException("probe denied")
                : File.GetAttributes(path));
        var recovered = recoveryService.Load();
        var removed = recoveryService.CleanupOrphanedSessionDirectories(
            recovered,
            TestContext.Current.CancellationToken);

        Assert.Empty(recovered.Accounts);
        Assert.False(recoveryService.CanReconcileProfiles);
        Assert.Equal(0, removed);
        Assert.True(File.Exists(sentinel));
    }

    [Fact]
    public void Load_InaccessibleCleanupGuardProbe_BlocksProfileCleanup()
    {
        var service = new SettingsService(_storageDirectory);
        service.Save(new());
        var orphan = Path.Combine(
            _storageDirectory,
            "Profiles",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(orphan);
        var guardPath = Path.Combine(
            _storageDirectory,
            "profile-cleanup-paused.txt");

        var guardedService = new SettingsService(
            _storageDirectory,
            path => path.Equals(guardPath, StringComparison.OrdinalIgnoreCase)
                ? throw new UnauthorizedAccessException("probe denied")
                : File.GetAttributes(path));
        var loaded = guardedService.Load();
        var removed = guardedService.CleanupOrphanedSessionDirectories(
            loaded,
            TestContext.Current.CancellationToken);

        Assert.False(guardedService.CanReconcileProfiles);
        Assert.Equal(0, removed);
        Assert.True(Directory.Exists(orphan));
    }

    [Fact]
    public void Save_LockedPrimary_PreservesPrimaryBackupAndRemovesTemporaryFile()
    {
        var service = new SettingsService(_storageDirectory);
        service.Save(CreateSettings("Backup"));
        service.Save(CreateSettings("Primary"));
        var primaryPath = Path.Combine(_storageDirectory, "settings.json");
        var backupPath = Path.Combine(
            _storageDirectory,
            "settings.backup.json");
        var primaryBefore = File.ReadAllBytes(primaryPath);
        var backupBefore = File.ReadAllBytes(backupPath);

        using (var locked = new FileStream(
                   primaryPath,
                   FileMode.Open,
                   FileAccess.Read,
                   FileShare.Read))
        {
            Assert.Throws<IOException>(() =>
                service.Save(CreateSettings("Blocked")));
        }

        Assert.Equal(primaryBefore, File.ReadAllBytes(primaryPath));
        Assert.Equal(backupBefore, File.ReadAllBytes(backupPath));
        Assert.Empty(Directory.GetFiles(
            _storageDirectory,
            "settings.save.*.tmp"));
    }

    [Fact]
    public void CleanupOrphanedSessionDirectories_NonDirectoryProfilesPath_PausesCleanup()
    {
        var service = new SettingsService(_storageDirectory);
        File.WriteAllText(
            Path.Combine(_storageDirectory, "Profiles"),
            "unexpected-file");

        var removed = service.CleanupOrphanedSessionDirectories(
            new(),
            TestContext.Current.CancellationToken);

        Assert.Equal(0, removed);
        Assert.False(service.CanReconcileProfiles);
        Assert.True(File.Exists(Path.Combine(
            _storageDirectory,
            "profile-cleanup-paused.txt")));
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

        var removed = service.CleanupOrphanedSessionDirectories(
            settings,
            TestContext.Current.CancellationToken);

        Assert.Equal(1, removed);
        Assert.True(Directory.Exists(referenced));
        Assert.False(Directory.Exists(orphan));
        Assert.True(Directory.Exists(unrelated));
    }

    [Fact]
    public void CleanupOrphanedSessionDirectories_CancellationPreservesUnfinishedProfile()
    {
        using var cancellation = new CancellationTokenSource();
        var service = new SettingsService(
            _storageDirectory,
            File.GetAttributes,
            (_, cancellationToken) =>
            {
                Assert.True(cancellationToken.CanBeCanceled);
                cancellation.Cancel();
                cancellationToken.ThrowIfCancellationRequested();
                return true;
            });
        var orphan = Path.Combine(
            _storageDirectory,
            "Profiles",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(orphan);

        Assert.ThrowsAny<OperationCanceledException>(() =>
            service.CleanupOrphanedSessionDirectories(
                new AppSettings(),
                cancellation.Token));

        Assert.True(Directory.Exists(orphan));
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
