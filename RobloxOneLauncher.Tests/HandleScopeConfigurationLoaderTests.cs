using RobloxOneLauncher.SystemProcesses;

namespace RobloxOneLauncher.Tests;

public sealed class HandleScopeConfigurationLoaderTests
{
    [Fact]
    public void LoadEnabled_MissingPerDeviceConfiguration_ReturnsNull()
    {
        var path = NewTemporaryPath();

        var configuration = new HandleScopeConfigurationLoader(path).LoadEnabled();

        Assert.Null(configuration);
    }

    [Fact]
    public void LoadEnabled_DisabledConfiguration_ReturnsNull()
    {
        const string json = """
            {
              "enabled": false,
              "processName": "RobloxPlayerBeta",
              "handleName": "valid-handle"
            }
            """;

        Assert.Null(Load(json));
    }

    [Fact]
    public void LoadEnabled_PlaceholderSelector_ReturnsNull()
    {
        const string json = """
            {
              "enabled": true,
              "processName": "RobloxPlayerBeta",
              "handleName": "REPLACE_WITH_HANDLE_NAME"
            }
            """;

        Assert.Null(Load(json));
    }

    [Fact]
    public void LoadEnabled_ValidConfiguration_NormalizesAndBoundsValues()
    {
        const string json = """
            {
              "enabled": true,
              "processName": " RobloxPlayerBeta ",
              "handleName": " valid-handle ",
              "handleType": " Event ",
              "access": "0x001F0003",
              "match": " EXACT ",
              "retryTimeoutSeconds": 99,
              "retryIntervalMilliseconds": 99999
            }
            """;

        var configuration = Load(json);

        Assert.NotNull(configuration);
        Assert.Equal("RobloxPlayerBeta", configuration.ProcessName);
        Assert.Equal("valid-handle", configuration.HandleName);
        Assert.Equal("Event", configuration.HandleType);
        Assert.Equal("0x001F0003", configuration.Access);
        Assert.Equal("exact", configuration.Match);
        Assert.Equal(30, configuration.RetryTimeoutSeconds);
        Assert.Equal(2000, configuration.RetryIntervalMilliseconds);
    }

    private static HandleScopeConfiguration? Load(string json)
    {
        var path = NewTemporaryPath();
        try
        {
            File.WriteAllText(path, json);
            return new HandleScopeConfigurationLoader(path).LoadEnabled();
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static string NewTemporaryPath()
    {
        return Path.Combine(
            Path.GetTempPath(),
            $"RobloxOne.HandleScope.{Guid.NewGuid():N}.json");
    }
}
