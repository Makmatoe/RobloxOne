using System.Text.Json.Nodes;
using SessionDock.Models;
using SessionDock.Services;

namespace SessionDock.Tests;

public sealed class ThemeSettingsTests : IDisposable
{
    private readonly string _storageDirectory = Path.Combine(
        Path.GetTempPath(),
        $"SessionDock-theme-settings-tests-{Guid.NewGuid():N}");

    [Fact]
    public void NewSettings_DefaultToDarkTheme()
    {
        Assert.False(new AppSettings().UseLightTheme);
    }

    [Fact]
    public void Load_SettingsWithoutThemePreference_DefaultsToDarkTheme()
    {
        var service = new SettingsService(_storageDirectory);
        var settings = CreateSettings(useLightTheme: true);
        service.Save(settings);
        var settingsPath = Path.Combine(_storageDirectory, "settings.json");
        var document = JsonNode.Parse(File.ReadAllText(settingsPath))!
            .AsObject();
        Assert.True(document.Remove(nameof(AppSettings.UseLightTheme)));
        File.WriteAllText(settingsPath, document.ToJsonString());

        var loaded = new SettingsService(_storageDirectory).Load();

        Assert.False(loaded.UseLightTheme);
        Assert.Equal(settings.ActiveAccountKey, loaded.ActiveAccountKey);
        Assert.Equal("builder", Assert.Single(loaded.Accounts).Username);
    }

    [Fact]
    public void SaveLoad_LightThemePreferenceRoundTrips()
    {
        var service = new SettingsService(_storageDirectory);
        service.Save(CreateSettings(useLightTheme: true));

        var loaded = new SettingsService(_storageDirectory).Load();

        Assert.True(loaded.UseLightTheme);
    }

    [Theory]
    [MemberData(nameof(ExpectedPersistenceFailures))]
    public async Task CommitThemeChange_FailedWriteRollsBackWithoutApplyingTheme(
        Exception failure)
    {
        var settings = CreateSettings(useLightTheme: false);
        var coordinator = new SettingsMutationCoordinator(
            settings,
            new SerializedSettingsWriter(_ => throw failure));
        var visualThemeApplied = false;

        var result = await coordinator.CommitAsync(
            () => settings.UseLightTheme = true,
            () => visualThemeApplied = true);

        Assert.False(result.Committed);
        Assert.IsType(failure.GetType(), result.Failure);
        Assert.False(settings.UseLightTheme);
        Assert.False(visualThemeApplied);
    }

    public static TheoryData<Exception> ExpectedPersistenceFailures() => new()
    {
        new IOException("disk unavailable"),
        new UnauthorizedAccessException("write denied")
    };

    public void Dispose()
    {
        if (Directory.Exists(_storageDirectory))
            Directory.Delete(_storageDirectory, recursive: true);
    }

    private static AppSettings CreateSettings(bool useLightTheme)
    {
        var accountKey = Guid.NewGuid().ToString("N");
        return new AppSettings
        {
            Accounts =
            [
                new AccountProfile
                {
                    Key = accountKey,
                    UserId = 42,
                    Username = "builder",
                    SessionFolder = $@"Profiles\{accountKey}"
                }
            ],
            ActiveAccountKey = accountKey,
            UseLightTheme = useLightTheme
        };
    }
}
