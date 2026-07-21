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
}
