using System.Diagnostics;
using System.IO;
using System.Text.Json;

namespace RobloxOneLauncher.SystemProcesses;

public sealed class HandleScopeConfigurationLoader
{
    internal const string RequiredProcessName = "RobloxPlayerBeta";
    internal const string RequiredHandleName =
        @"\Sessions\{SESSION_ID}\BaseNamedObjects\ROBLOX_singletonEvent";
    internal const string RequiredHandleType = "Event";
    internal const string RequiredAccess = "0x001F0003";
    private const int MaximumConfigurationBytes = 64 * 1024;
    private const string PlaceholderHandleName = "REPLACE_WITH_HANDLE_NAME";
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow,
        MaxDepth = 8
    };
    private readonly string _configurationPath;

    public HandleScopeConfigurationLoader(string? configurationPath = null)
    {
        _configurationPath = configurationPath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "RobloxOne",
            "handlescope.json");
    }

    public HandleScopeConfiguration? LoadEnabled()
    {
        try
        {
            var json = ReadConfiguration();
            if (json is null)
                return null;
            using var document = JsonDocument.Parse(
                json,
                new JsonDocumentOptions { MaxDepth = 8 });
            if (document.RootElement.ValueKind != JsonValueKind.Object ||
                !HasUniqueProperties(document.RootElement))
            {
                throw new JsonException(
                    "The HandleScope configuration must be one unambiguous object.");
            }
            var configuration = JsonSerializer.Deserialize<HandleScopeConfiguration>(
                json,
                JsonOptions);
            if (configuration is null || !configuration.Enabled)
                return null;

            return Normalize(configuration);
        }
        catch (Exception ex) when (
            ex is IOException or UnauthorizedAccessException or JsonException or
                InvalidDataException)
        {
            Trace.WriteLine(
                $"HandleScope configuration was disabled: {ex.GetType().Name}.");
            return null;
        }
    }

    private byte[]? ReadConfiguration()
    {
        if (!File.Exists(_configurationPath))
            return null;

        var attributes = File.GetAttributes(_configurationPath);
        if ((attributes & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException(
                "The HandleScope configuration cannot be a reparse point.");

        using var stream = new FileStream(
            _configurationPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read);
        using var buffer = new MemoryStream();
        var chunk = new byte[4096];
        while (true)
        {
            var read = stream.Read(chunk, 0, chunk.Length);
            if (read == 0)
                return buffer.ToArray();
            if (buffer.Length + read > MaximumConfigurationBytes)
            {
                throw new InvalidDataException(
                    "The HandleScope configuration is too large.");
            }
            buffer.Write(chunk, 0, read);
        }
    }

    private static HandleScopeConfiguration? Normalize(
        HandleScopeConfiguration configuration)
    {
        var processName = configuration.ProcessName?.Trim();
        var handleName = configuration.HandleName?.Trim();
        var handleType = configuration.HandleType?.Trim();
        var access = configuration.Access?.Trim();
        var match = configuration.Match?.Trim();

        if (!string.Equals(
                processName,
                RequiredProcessName,
                StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(
                handleName,
                RequiredHandleName,
                StringComparison.Ordinal) ||
            string.Equals(
                handleName,
                PlaceholderHandleName,
                StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(match, "exact", StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(
                handleType,
                RequiredHandleType,
                StringComparison.Ordinal) ||
            !string.Equals(
                access,
                RequiredAccess,
                StringComparison.OrdinalIgnoreCase) ||
            configuration.CloseAll ||
            !configuration.AllProcesses)
        {
            Trace.WriteLine(
                "HandleScope configuration was disabled: it does not match the fixed v1 Roblox policy.");
            return null;
        }

        configuration.ProcessName = RequiredProcessName;
        configuration.HandleName = RequiredHandleName;
        configuration.HandleType = RequiredHandleType;
        configuration.Access = RequiredAccess;
        configuration.Match = "exact";
        configuration.CloseAll = false;
        configuration.AllProcesses = true;
        configuration.RetryTimeoutSeconds =
            Math.Clamp(configuration.RetryTimeoutSeconds, 1, 30);
        configuration.RetryIntervalMilliseconds = Math.Min(
            Math.Clamp(configuration.RetryIntervalMilliseconds, 100, 2000),
            configuration.RetryTimeoutSeconds * 1000);
        return configuration;
    }

    private static bool HasUniqueProperties(JsonElement root)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        return root.EnumerateObject().All(property => names.Add(property.Name));
    }

}
