using SessionDock.Services;

namespace SessionDock.Tests;

public sealed class UiSoundImportCleanupTests : IDisposable
{
    private readonly string _soundsDirectory = Path.Combine(
        Path.GetTempPath(),
        $"SessionDock-sound-cleanup-{Guid.NewGuid():N}");

    [Fact]
    public void CleanupOrphanedImportedSounds_PreservesReferencedAndUnmanagedFiles()
    {
        Directory.CreateDirectory(_soundsDirectory);
        var referenced = $"startup-custom-{Guid.NewGuid():N}.wav";
        var orphan = $"startup-custom-{Guid.NewGuid():N}.mp3";
        var temporary =
            $"startup-custom-{Guid.NewGuid():N}.m4a.{Guid.NewGuid():N}.tmp";
        var builtIn = "startup-soft-v1.wav";
        WriteFile(referenced);
        WriteFile(orphan);
        WriteFile(temporary);
        WriteFile(builtIn);

        var removed = UiSoundService.CleanupOrphanedImportedSounds(
            _soundsDirectory,
            referenced,
            reconciliationIsSafe: true,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(2, removed);
        Assert.True(File.Exists(Path.Combine(_soundsDirectory, referenced)));
        Assert.False(File.Exists(Path.Combine(_soundsDirectory, orphan)));
        Assert.False(File.Exists(Path.Combine(_soundsDirectory, temporary)));
        Assert.True(File.Exists(Path.Combine(_soundsDirectory, builtIn)));
    }

    [Fact]
    public void CleanupOrphanedImportedSounds_InvalidReferencePreservesNothingManaged()
    {
        Directory.CreateDirectory(_soundsDirectory);
        var orphan = $"startup-custom-{Guid.NewGuid():N}.wma";
        WriteFile(orphan);

        var removed = UiSoundService.CleanupOrphanedImportedSounds(
            _soundsDirectory,
            "..\\outside.wav",
            reconciliationIsSafe: true,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(1, removed);
        Assert.False(File.Exists(Path.Combine(_soundsDirectory, orphan)));
    }

    [Fact]
    public void CleanupOrphanedImportedSounds_UncertainRecoveryDeletesNothing()
    {
        Directory.CreateDirectory(_soundsDirectory);
        var possiblyReferenced = $"startup-custom-{Guid.NewGuid():N}.wav";
        var temporary =
            $"startup-custom-{Guid.NewGuid():N}.mp3.{Guid.NewGuid():N}.tmp";
        WriteFile(possiblyReferenced);
        WriteFile(temporary);

        var removed = UiSoundService.CleanupOrphanedImportedSounds(
            _soundsDirectory,
            referencedFileName: null,
            reconciliationIsSafe: false,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(0, removed);
        Assert.True(File.Exists(
            Path.Combine(_soundsDirectory, possiblyReferenced)));
        Assert.True(File.Exists(Path.Combine(_soundsDirectory, temporary)));
    }

    public void Dispose()
    {
        if (Directory.Exists(_soundsDirectory))
            Directory.Delete(_soundsDirectory, recursive: true);
    }

    private void WriteFile(string fileName) =>
        File.WriteAllBytes(
            Path.Combine(_soundsDirectory, fileName),
            [1, 2, 3]);
}
