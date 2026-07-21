using System.Text.Json;
using RobloxOneLauncher.Models;
using RobloxOneLauncher.Services;

namespace RobloxOneLauncher.Tests;

public sealed class LocalDataSecurityTests : IDisposable
{
    private readonly string _storageDirectory = Path.Combine(
        Path.GetTempPath(),
        $"RobloxOne-security-tests-{Guid.NewGuid():N}");

    [Fact]
    public void Load_DropsUnsafePathsAndMalformedDestinationsFromLocalSettings()
    {
        Directory.CreateDirectory(_storageDirectory);
        var unsafeSettings = new AppSettings
        {
            Accounts =
            [
                new AccountProfile
                {
                    Key = new string('a', 32),
                    UserId = 42,
                    Username = "builder",
                    SessionFolder = @"..\outside",
                    Destination =
                        "https://www.roblox.com/share?code=%00private-code"
                }
            ],
            RecentExperiences =
            [
                new RecentExperience
                {
                    Destination =
                        "https://www.roblox.com/games/123?linkCode=bad%0Acode",
                    PlaceId = 123,
                    LastLaunchedAt = DateTimeOffset.UtcNow
                }
            ],
            StartupSound = UiSoundService.StartupCustom,
            CustomStartupSoundFileName = @"..\outside.wav"
        };
        File.WriteAllText(
            Path.Combine(_storageDirectory, "settings.json"),
            JsonSerializer.Serialize(unsafeSettings));

        var loaded = new SettingsService(_storageDirectory).Load();

        Assert.Empty(loaded.Accounts);
        Assert.Empty(loaded.RecentExperiences);
        Assert.Equal(UiSoundService.DefaultStartupSound, loaded.StartupSound);
        Assert.Null(loaded.CustomStartupSoundFileName);
    }

    [Theory]
    [InlineData(@"..\outside")]
    [InlineData(@"Profiles\0123456789abcdef0123456789abcdef\..\..\..\outside")]
    [InlineData(@"C:\outside")]
    public void GetSessionDataDirectory_PathOutsideStorageRoot_IsRejected(
        string sessionFolder)
    {
        var service = new SettingsService(_storageDirectory);
        var profile = new AccountProfile { SessionFolder = sessionFolder };

        Assert.Throws<InvalidOperationException>(() =>
            service.GetSessionDataDirectory(profile));
    }

    [Theory]
    [InlineData(@"..\outside.wav")]
    [InlineData(@"folder\sound.mp3")]
    [InlineData("folder/sound.m4a")]
    [InlineData(@"C:\sound.wma")]
    [InlineData("sound.exe")]
    [InlineData("sound.wav:stream")]
    [InlineData("")]
    public void IsValidImportedFileName_PathOrUnsupportedExtension_IsRejected(
        string fileName)
    {
        Assert.False(UiSoundService.IsValidImportedFileName(fileName));
    }

    [Theory]
    [InlineData("sound.wav")]
    [InlineData("sound.MP3")]
    [InlineData("sound.wma")]
    [InlineData("sound.m4a")]
    public void IsValidImportedFileName_SafeLeafName_IsAccepted(string fileName)
    {
        Assert.True(UiSoundService.IsValidImportedFileName(fileName));
    }

    [Fact]
    public void Load_OversizedSettingsFile_IsPreservedAndCleanupIsPaused()
    {
        Directory.CreateDirectory(_storageDirectory);
        File.WriteAllText(
            Path.Combine(_storageDirectory, "settings.json"),
            new string(' ', (4 * 1024 * 1024) + 1));

        var service = new SettingsService(_storageDirectory);
        var loaded = service.Load();

        Assert.Empty(loaded.Accounts);
        Assert.False(service.CanReconcileProfiles);
        Assert.NotNull(service.LoadNotice);
        Assert.Single(Directory.GetFiles(
            _storageDirectory,
            "settings.corrupt-*.json"));
    }

    [Fact]
    public void Save_ConcurrentCalls_LeaveOneValidSettingsFileAndNoTemporaryFiles()
    {
        var service = new SettingsService(_storageDirectory);

        Parallel.For(0, 16, index =>
            service.Save(CreateSettings($"Account {index}")));

        var loaded = new SettingsService(_storageDirectory).Load();
        var account = Assert.Single(loaded.Accounts);
        Assert.StartsWith("Account ", account.Label, StringComparison.Ordinal);
        Assert.Empty(Directory.GetFiles(_storageDirectory, "*.tmp"));
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
