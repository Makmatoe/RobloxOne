using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace RobloxOneLauncher.SystemProcesses;

public sealed class HandleScopeConfigurationLoader
{
    private const string PlaceholderHandleName = "REPLACE_WITH_HANDLE_NAME";
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };
    private static readonly Regex ProcessNamePattern = new(
        @"^[A-Za-z0-9_.-]{1,128}$",
        RegexOptions.Compiled);
    private static readonly Regex AccessPattern = new(
        @"^(?:0[xX][0-9A-Fa-f]+|[0-9]+)$",
        RegexOptions.Compiled);
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
            var configuration = JsonSerializer.Deserialize<HandleScopeConfiguration>(
                json,
                JsonOptions);
            if (configuration is null || !configuration.Enabled)
                return null;

            return Normalize(configuration);
        }
        catch (Exception ex) when (
            ex is IOException or UnauthorizedAccessException or JsonException)
        {
            Trace.WriteLine(
                $"HandleScope configuration was disabled: {ex.GetType().Name}.");
            return null;
        }
    }

    private string? ReadConfiguration()
    {
        return File.Exists(_configurationPath)
            ? File.ReadAllText(_configurationPath)
            : null;
    }

    private static HandleScopeConfiguration? Normalize(
        HandleScopeConfiguration configuration)
    {
        var processName = string.IsNullOrWhiteSpace(configuration.ProcessName)
            ? "RobloxPlayerBeta"
            : configuration.ProcessName.Trim();
        var handleName = configuration.HandleName?.Trim();
        var handleType = NormalizeOptional(configuration.HandleType, 128);
        var access = NormalizeOptional(configuration.Access, 64);
        var match = string.IsNullOrWhiteSpace(configuration.Match)
            ? "exact"
            : configuration.Match.Trim().ToLowerInvariant();

        if (!ProcessNamePattern.IsMatch(processName) ||
            string.IsNullOrWhiteSpace(handleName) ||
            handleName.Length > 2048 ||
            handleName.Equals(
                PlaceholderHandleName,
                StringComparison.OrdinalIgnoreCase) ||
            match is not ("exact" or "contains") ||
            (configuration.HandleType is not null && handleType is null) ||
            (configuration.Access is not null &&
             (access is null || !AccessPattern.IsMatch(access))))
        {
            Trace.WriteLine("HandleScope configuration was disabled: invalid selector.");
            return null;
        }

        configuration.ProcessName = processName;
        configuration.HandleName = handleName;
        configuration.HandleType = handleType;
        configuration.Access = access;
        configuration.Match = match;
        configuration.RetryTimeoutSeconds =
            Math.Clamp(configuration.RetryTimeoutSeconds, 1, 30);
        configuration.RetryIntervalMilliseconds = Math.Min(
            Math.Clamp(configuration.RetryIntervalMilliseconds, 100, 2000),
            configuration.RetryTimeoutSeconds * 1000);
        return configuration;
    }

    private static string? NormalizeOptional(string? value, int maximumLength)
    {
        if (value is null)
            return null;
        var trimmed = value.Trim();
        return trimmed.Length is > 0 && trimmed.Length <= maximumLength
            ? trimmed
            : null;
    }
}
