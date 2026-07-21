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
    public void LoadEnabled_MinimalOptIn_UsesFixedPolicyDefaults()
    {
        var configuration = Load("""
            { "enabled": true }
            """);

        Assert.NotNull(configuration);
        Assert.Equal("RobloxPlayerBeta", configuration.ProcessName);
        Assert.Equal(
            @"\Sessions\{SESSION_ID}\BaseNamedObjects\ROBLOX_singletonEvent",
            configuration.HandleName);
        Assert.Equal("Event", configuration.HandleType);
        Assert.Equal("0x001F0003", configuration.Access);
        Assert.Equal("exact", configuration.Match);
        Assert.False(configuration.CloseAll);
        Assert.True(configuration.AllProcesses);
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

    [Theory]
    [InlineData("{\"enabled\":true,\"unknownSelector\":\"anything\"}")]
    [InlineData("{\"enabled\":true,\"enabled\":true}")]
    [InlineData("{\"enabled\":true,\"ProcessName\":\"OtherPlayer\"}")]
    public void LoadEnabled_AmbiguousOrUnknownConfiguration_ReturnsNull(string json)
    {
        Assert.Null(Load(json));
    }

    [Fact]
    public void LoadEnabled_ValidConfiguration_NormalizesAndBoundsValues()
    {
        const string json = """
            {
              "enabled": true,
              "processName": " RobloxPlayerBeta ",
              "handleName": "\\Sessions\\{SESSION_ID}\\BaseNamedObjects\\ROBLOX_singletonEvent",
              "handleType": " Event ",
              "access": "0x001F0003",
              "match": " EXACT ",
              "closeAll": false,
              "allProcesses": true,
              "retryTimeoutSeconds": 99,
              "retryIntervalMilliseconds": 99999
            }
            """;

        var configuration = Load(json);

        Assert.NotNull(configuration);
        Assert.Equal("RobloxPlayerBeta", configuration.ProcessName);
        Assert.Equal(
            @"\Sessions\{SESSION_ID}\BaseNamedObjects\ROBLOX_singletonEvent",
            configuration.HandleName);
        Assert.Equal("Event", configuration.HandleType);
        Assert.Equal("0x001F0003", configuration.Access);
        Assert.Equal("exact", configuration.Match);
        Assert.Equal(30, configuration.RetryTimeoutSeconds);
        Assert.Equal(2000, configuration.RetryIntervalMilliseconds);
    }

    [Theory]
    [InlineData("OtherPlayer", @"\Sessions\{SESSION_ID}\BaseNamedObjects\ROBLOX_singletonEvent", "Event", "0x001F0003", "exact", false, true)]
    [InlineData("RobloxPlayerBeta", "other-handle", "Event", "0x001F0003", "exact", false, true)]
    [InlineData("RobloxPlayerBeta", @"\Sessions\{SESSION_ID}\BaseNamedObjects\ROBLOX_singletonEvent", "Mutant", "0x001F0003", "exact", false, true)]
    [InlineData("RobloxPlayerBeta", @"\Sessions\{SESSION_ID}\BaseNamedObjects\ROBLOX_singletonEvent", "Event", "1", "exact", false, true)]
    [InlineData("RobloxPlayerBeta", @"\Sessions\{SESSION_ID}\BaseNamedObjects\ROBLOX_singletonEvent", "Event", "0x001F0003", "contains", false, true)]
    [InlineData("RobloxPlayerBeta", @"\Sessions\{SESSION_ID}\BaseNamedObjects\ROBLOX_singletonEvent", "Event", "0x001F0003", "exact", true, true)]
    [InlineData("RobloxPlayerBeta", @"\Sessions\{SESSION_ID}\BaseNamedObjects\ROBLOX_singletonEvent", "Event", "0x001F0003", "exact", false, false)]
    public void LoadEnabled_NonPolicySelector_ReturnsNull(
        string processName,
        string handleName,
        string handleType,
        string access,
        string match,
        bool closeAll,
        bool allProcesses)
    {
        var json = $$"""
            {
              "enabled": true,
              "processName": {{System.Text.Json.JsonSerializer.Serialize(processName)}},
              "handleName": {{System.Text.Json.JsonSerializer.Serialize(handleName)}},
              "handleType": {{System.Text.Json.JsonSerializer.Serialize(handleType)}},
              "access": {{System.Text.Json.JsonSerializer.Serialize(access)}},
              "match": {{System.Text.Json.JsonSerializer.Serialize(match)}},
              "closeAll": {{closeAll.ToString().ToLowerInvariant()}},
              "allProcesses": {{allProcesses.ToString().ToLowerInvariant()}}
            }
            """;

        Assert.Null(Load(json));
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
