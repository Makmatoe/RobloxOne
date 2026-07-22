using System.Text.Json;
using SessionDock.Models;
using SessionDock.Services;

namespace SessionDock.Tests;

public sealed class PendingProfileDeletionIntentTests : IDisposable
{
    private readonly string _storageDirectory = Path.Combine(
        Path.GetTempPath(),
        $"SessionDock-profile-intent-{Guid.NewGuid():N}");

    [Fact]
    public async Task DeletePendingProfile_GuardedRecoveryDeletesOnlyExplicitTarget()
    {
        Directory.CreateDirectory(_storageDirectory);
        File.WriteAllText(
            Path.Combine(_storageDirectory, "profile-cleanup-paused.txt"),
            "test guard");
        var service = new SettingsService(_storageDirectory);
        var targetKey = Guid.NewGuid().ToString("N");
        var unjournaledKey = Guid.NewGuid().ToString("N");
        var target = CreateProfileDirectory(targetKey);
        var unjournaled = CreateProfileDirectory(unjournaledKey);
        var settings = new AppSettings
        {
            PendingProfileDeletionKeys = [targetKey]
        };
        service.StageProfileDeletion(targetKey);

        var deleted = await service.DeletePendingProfileAsync(
            targetKey,
            settings,
            TestContext.Current.CancellationToken);

        Assert.False(service.CanReconcileProfiles);
        Assert.True(deleted);
        Assert.False(Directory.Exists(target));
        Assert.True(Directory.Exists(unjournaled));
    }

    [Fact]
    public async Task DeletePendingProfile_LegacyIntentDeletesWebSession()
    {
        var service = new SettingsService(_storageDirectory);
        var legacyDirectory = Path.Combine(_storageDirectory, "WebSession");
        Directory.CreateDirectory(legacyDirectory);
        File.WriteAllText(Path.Combine(legacyDirectory, "Cookies"), "secret");
        var settings = new AppSettings
        {
            PendingProfileDeletionKeys = ["legacy"]
        };
        service.StageProfileDeletion("legacy");

        var deleted = await service.DeletePendingProfileAsync(
            "legacy",
            settings,
            TestContext.Current.CancellationToken);

        Assert.True(deleted);
        Assert.False(Directory.Exists(legacyDirectory));
    }

    [Fact]
    public async Task DeletePendingProfile_CurrentAccountReferenceFailsClosed()
    {
        var service = new SettingsService(_storageDirectory);
        var key = Guid.NewGuid().ToString("N");
        var directory = CreateProfileDirectory(key);
        var settings = new AppSettings
        {
            Accounts =
            [
                new AccountProfile
                {
                    Key = key,
                    UserId = 42,
                    Username = "builder",
                    SessionFolder = $@"Profiles\{key}"
                }
            ],
            PendingProfileDeletionKeys = [key]
        };
        service.StageProfileDeletion(key);

        var deleted = await service.DeletePendingProfileAsync(
            key,
            settings,
            TestContext.Current.CancellationToken);

        Assert.False(deleted);
        Assert.True(Directory.Exists(directory));
    }

    [Fact]
    public async Task DeletePendingProfile_InvalidKeyCannotTouchOutsideSentinel()
    {
        var service = new SettingsService(_storageDirectory);
        var sentinel = Path.Combine(
            Path.GetDirectoryName(_storageDirectory)!,
            $"SessionDock-profile-sentinel-{Guid.NewGuid():N}.txt");
        File.WriteAllText(sentinel, "keep");
        try
        {
            var invalidKey = $"..\\{Path.GetFileNameWithoutExtension(sentinel)}";
            var settings = new AppSettings
            {
                PendingProfileDeletionKeys = [invalidKey]
            };

            var deleted = await service.DeletePendingProfileAsync(
                invalidKey,
                settings,
                TestContext.Current.CancellationToken);

            Assert.False(deleted);
            Assert.True(File.Exists(sentinel));
        }
        finally
        {
            File.Delete(sentinel);
        }
    }

    [Fact]
    public async Task DeletePendingProfile_DiscardedAccountMetadataFailsClosed()
    {
        Directory.CreateDirectory(_storageDirectory);
        var key = Guid.NewGuid().ToString("N");
        var directory = CreateProfileDirectory(key);
        var damagedSettings = new AppSettings
        {
            Accounts =
            [
                new AccountProfile
                {
                    Key = key,
                    UserId = 0,
                    Username = "damaged",
                    SessionFolder = $@"Profiles\{key}"
                }
            ],
            PendingProfileDeletionKeys = [key]
        };
        File.WriteAllText(
            Path.Combine(_storageDirectory, "settings.json"),
            JsonSerializer.Serialize(damagedSettings));
        var service = new SettingsService(_storageDirectory);
        var loaded = service.Load();

        var deleted = await service.DeletePendingProfileAsync(
            key,
            loaded,
            TestContext.Current.CancellationToken);

        Assert.False(service.CanReconcileProfiles);
        Assert.False(deleted);
        Assert.True(Directory.Exists(directory));
    }

    [Fact]
    public async Task DeletionJournal_PrimaryRecoveryReappliesConfirmedRemoval()
    {
        var key = Guid.NewGuid().ToString("N");
        var service = new SettingsService(_storageDirectory);
        var account = CreateAccount(key);
        service.Save(new AppSettings
        {
            Accounts = [account],
            ActiveAccountKey = key
        });
        service.StageProfileDeletion(key);
        service.Save(new AppSettings
        {
            PendingProfileDeletionKeys = [key]
        });
        var directory = CreateProfileDirectory(key);
        File.WriteAllText(
            Path.Combine(_storageDirectory, "settings.json"),
            "{ unreadable");

        var recoveryService = new SettingsService(_storageDirectory);
        var recovered = recoveryService.Load();

        Assert.Equal(key, Assert.Single(recovered.Accounts).Key);
        Assert.Empty(recovered.PendingProfileDeletionKeys);
        Assert.Contains(
            key,
            recoveryService.GetJournaledProfileDeletionKeys(),
            StringComparer.OrdinalIgnoreCase);

        recovered.Accounts.Clear();
        recovered.ActiveAccountKey = null;
        recovered.PendingProfileDeletionKeys = [key];
        recoveryService.Save(recovered);
        var deleted = await recoveryService.DeletePendingProfileAsync(
            key,
            recovered,
            TestContext.Current.CancellationToken);

        Assert.True(deleted);
        Assert.False(Directory.Exists(directory));
    }

    [Fact]
    public void Load_PendingDeletionKeysAreCanonicalDeduplicatedAndBounded()
    {
        Directory.CreateDirectory(_storageDirectory);
        var keys = Enumerable.Range(
                0,
                SettingsService.MaximumPendingProfileDeletions + 50)
            .Select(_ => Guid.NewGuid().ToString("N"))
            .ToList();
        keys.Add(keys[0].ToUpperInvariant());
        keys.Add("..\\outside");
        File.WriteAllText(
            Path.Combine(_storageDirectory, "settings.json"),
            JsonSerializer.Serialize(new AppSettings
            {
                PendingProfileDeletionKeys = keys
            }));

        var loaded = new SettingsService(_storageDirectory).Load();

        Assert.Equal(
            SettingsService.MaximumPendingProfileDeletions,
            loaded.PendingProfileDeletionKeys.Count);
        Assert.Equal(
            loaded.PendingProfileDeletionKeys.Count,
            loaded.PendingProfileDeletionKeys
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Count());
        Assert.DoesNotContain("..\\outside", loaded.PendingProfileDeletionKeys);
    }

    [Fact]
    public async Task DeleteSessionData_NoncanonicalInRootFolderIsRejected()
    {
        var service = new SettingsService(_storageDirectory);
        var firstKey = Guid.NewGuid().ToString("N");
        var secondKey = Guid.NewGuid().ToString("N");
        var first = CreateProfileDirectory(firstKey);
        var second = CreateProfileDirectory(secondKey);
        var unsafeProfile = CreateAccount(firstKey);
        unsafeProfile.SessionFolder = "Profiles";

        var deleted = await service.DeleteSessionDataAsync(
            unsafeProfile,
            TestContext.Current.CancellationToken);

        Assert.False(deleted);
        Assert.True(Directory.Exists(first));
        Assert.True(Directory.Exists(second));
    }

    [Fact]
    public async Task DeleteSessionData_RecursiveTraversalRunsOffCallerThread()
    {
        var key = Guid.NewGuid().ToString("N");
        using var releaseDeletion = new ManualResetEventSlim();
        var deletionStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var service = new SettingsService(
            _storageDirectory,
            File.GetAttributes,
            (_, cancellationToken) =>
            {
                deletionStarted.TrySetResult();
                releaseDeletion.Wait(cancellationToken);
                return true;
            });
        _ = CreateProfileDirectory(key);

        var deletion = service.DeleteSessionDataAsync(
            CreateAccount(key),
            TestContext.Current.CancellationToken);
        try
        {
            await deletionStarted.Task.WaitAsync(
                TimeSpan.FromSeconds(1),
                TestContext.Current.CancellationToken);
            Assert.False(deletion.IsCompleted);
        }
        finally
        {
            releaseDeletion.Set();
        }

        Assert.True(await deletion);
    }

    public void Dispose()
    {
        if (Directory.Exists(_storageDirectory))
            Directory.Delete(_storageDirectory, recursive: true);
    }

    private string CreateProfileDirectory(string key)
    {
        var directory = Path.Combine(_storageDirectory, "Profiles", key);
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "Cookies"), "secret");
        return directory;
    }

    private static AccountProfile CreateAccount(string key) => new()
    {
        Key = key,
        UserId = 42,
        Username = "builder",
        SessionFolder = $@"Profiles\{key}"
    };
}
