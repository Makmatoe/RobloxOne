using SessionDock.Models;
using SessionDock.Services;

namespace SessionDock.Tests;

public sealed class ImportedSoundRecoveryTests : IDisposable
{
    private readonly string _storageDirectory = Path.Combine(
        Path.GetTempPath(),
        $"SessionDock-sound-recovery-{Guid.NewGuid():N}");

    [Fact]
    public void CaptureRetention_PrimaryCorruptionCanRecoverPlayableBackupSound()
    {
        var service = new SettingsService(_storageDirectory);
        var first = CreateImportedName("wav");
        var second = CreateImportedName("mp3");
        var soundsDirectory = CreateSoundFiles(first, second);
        service.Save(CreateSettings(first));
        service.Save(CreateSettings(second));

        var retention = service.CaptureImportedSoundRetention(second);
        UiSoundService.CleanupOrphanedImportedSounds(
            soundsDirectory,
            retention.FileNames,
            retention.CanReconcile,
            cancellationToken: TestContext.Current.CancellationToken);
        File.WriteAllText(
            Path.Combine(_storageDirectory, "settings.json"),
            "{ unreadable");

        var recovered = new SettingsService(_storageDirectory).Load();

        Assert.Equal(first, recovered.CustomStartupSoundFileName);
        Assert.True(File.Exists(Path.Combine(soundsDirectory, first)));
        Assert.True(File.Exists(Path.Combine(soundsDirectory, second)));
    }

    [Fact]
    public void CaptureRetention_ThirdRevisionRetiresOnlyBeyondBackupWindow()
    {
        var service = new SettingsService(_storageDirectory);
        var first = CreateImportedName("wav");
        var second = CreateImportedName("mp3");
        var third = CreateImportedName("m4a");
        var soundsDirectory = CreateSoundFiles(first, second, third);
        service.Save(CreateSettings(first));
        service.Save(CreateSettings(second));
        service.Save(CreateSettings(third));

        var retention = service.CaptureImportedSoundRetention(third);
        UiSoundService.CleanupOrphanedImportedSounds(
            soundsDirectory,
            retention.FileNames,
            retention.CanReconcile,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.False(File.Exists(Path.Combine(soundsDirectory, first)));
        Assert.True(File.Exists(Path.Combine(soundsDirectory, second)));
        Assert.True(File.Exists(Path.Combine(soundsDirectory, third)));
    }

    [Fact]
    public void CaptureRetention_UnreadableBackupFailsClosed()
    {
        var service = new SettingsService(_storageDirectory);
        var current = CreateImportedName("wav");
        service.Save(CreateSettings(current));
        Directory.CreateDirectory(
            Path.Combine(_storageDirectory, "settings.backup.json"));

        var retention = service.CaptureImportedSoundRetention(current);

        Assert.False(retention.CanReconcile);
        Assert.Contains(current, retention.FileNames);
    }

    public void Dispose()
    {
        if (Directory.Exists(_storageDirectory))
            Directory.Delete(_storageDirectory, recursive: true);
    }

    private string CreateSoundFiles(params string[] names)
    {
        var directory = Path.Combine(_storageDirectory, "Sounds");
        Directory.CreateDirectory(directory);
        foreach (var name in names)
            File.WriteAllBytes(Path.Combine(directory, name), [1, 2, 3]);
        return directory;
    }

    private static string CreateImportedName(string extension) =>
        $"startup-custom-{Guid.NewGuid():N}.{extension}";

    private static AppSettings CreateSettings(string fileName) => new()
    {
        StartupSound = UiSoundService.StartupCustom,
        CustomStartupSoundFileName = fileName
    };
}
