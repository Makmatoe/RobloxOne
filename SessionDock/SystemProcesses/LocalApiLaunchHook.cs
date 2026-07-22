using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Runtime.ExceptionServices;

namespace SessionDock.SystemProcesses;

public sealed class LocalApiLaunchHook : ILaunchHook
{
    public const string UrlEnvironmentVariable = "SESSIONDOCK_LAUNCH_HOOK_URL";
    public const string TokenEnvironmentVariable = "SESSIONDOCK_LAUNCH_HOOK_BEARER_TOKEN";
    internal const string LegacyUrlEnvironmentVariable =
        "ROBLOX_ONE_LAUNCH_HOOK_URL";
    internal const string LegacyTokenEnvironmentVariable =
        "ROBLOX_ONE_LAUNCH_HOOK_BEARER_TOKEN";
    private static readonly string[] ConfigurationEnvironmentVariables =
    [
        UrlEnvironmentVariable,
        TokenEnvironmentVariable,
        LegacyUrlEnvironmentVariable,
        LegacyTokenEnvironmentVariable
    ];
    private static readonly Lazy<CapturedConfiguration> ProcessConfiguration =
        new(
            CaptureAndScrubProcessConfiguration,
            LazyThreadSafetyMode.ExecutionAndPublication);
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(5);
    private readonly CapturedConfiguration _configuration;
    private readonly HttpClient _client;

    public bool IsConfigured => TryCreateAuthenticatedConfiguration(
        _configuration.Endpoint,
        _configuration.Token,
        out _,
        out _);

    public LocalApiLaunchHook()
        : this(ProcessConfiguration.Value)
    {
    }

    internal LocalApiLaunchHook(CapturedConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        _configuration = configuration;
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            ConnectTimeout = RequestTimeout,
            Credentials = null,
            PreAuthenticate = false,
            UseCookies = false,
            UseProxy = false
        };
        _client = new HttpClient(handler)
        {
            Timeout = RequestTimeout
        };
        _client.DefaultRequestHeaders.UserAgent.ParseAdd("SessionDock/2.1");
    }

    public async Task NotifyLaunchAsync(
        LaunchHookEvent launchEvent,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(launchEvent);
        if (!TryCreateAuthenticatedRequest(
                launchEvent,
                _configuration.Endpoint,
                _configuration.Token,
                out var request))
        {
            Trace.WriteLine(
                "Launch hook was not delivered: configure a trusted HTTPS " +
                "loopback URL and a valid bearer token.");
            return;
        }

        try
        {
            using (request)
            {
                using var response = await _client.SendAsync(
                    request,
                    HttpCompletionOption.ResponseHeadersRead,
                    cancellationToken);
                if (!response.IsSuccessStatusCode)
                {
                    Trace.WriteLine(
                        $"Launch hook returned HTTP {(int)response.StatusCode}.");
                }
            }
        }
        catch (Exception ex) when (
            ex is HttpRequestException or TaskCanceledException or OperationCanceledException)
        {
            // The external hook is optional and must never break a Roblox launch.
            Trace.WriteLine(
                $"Launch hook was not delivered: {ex.GetType().Name}.");
        }
    }

    public void Dispose() => _client.Dispose();

    internal static void RemoveConfigurationFromChildEnvironment(
        ProcessStartInfo startInfo)
    {
        ArgumentNullException.ThrowIfNull(startInfo);
        if (startInfo.UseShellExecute)
        {
            throw new InvalidOperationException(
                "Child-process environment scrubbing requires direct process creation.");
        }

        foreach (var variableName in ConfigurationEnvironmentVariables)
        {
            foreach (var inheritedName in startInfo.Environment.Keys
                         .Where(name => string.Equals(
                             name,
                             variableName,
                             StringComparison.OrdinalIgnoreCase))
                         .ToArray())
            {
                startInfo.Environment.Remove(inheritedName);
            }
        }
    }

    internal static CapturedConfiguration CaptureAndScrubConfiguration(
        Func<string, string?> getEnvironmentVariable,
        Action<string, string?> setEnvironmentVariable)
    {
        ArgumentNullException.ThrowIfNull(getEnvironmentVariable);
        ArgumentNullException.ThrowIfNull(setEnvironmentVariable);

        string? currentEndpoint = null;
        string? currentToken = null;
        string? legacyEndpoint = null;
        string? legacyToken = null;
        try
        {
            currentEndpoint = getEnvironmentVariable(UrlEnvironmentVariable);
            currentToken = getEnvironmentVariable(TokenEnvironmentVariable);
            legacyEndpoint = getEnvironmentVariable(
                LegacyUrlEnvironmentVariable);
            legacyToken = getEnvironmentVariable(
                LegacyTokenEnvironmentVariable);
        }
        finally
        {
            ScrubConfigurationEnvironment(setEnvironmentVariable);
        }

        return currentEndpoint is not null || currentToken is not null
            ? new CapturedConfiguration(currentEndpoint, currentToken)
            : new CapturedConfiguration(legacyEndpoint, legacyToken);
    }

    internal static bool TryCreateAuthenticatedRequest(
        LaunchHookEvent launchEvent,
        string? configuredEndpoint,
        string? configuredToken,
        [NotNullWhen(true)] out HttpRequestMessage? request)
    {
        ArgumentNullException.ThrowIfNull(launchEvent);
        request = null;
        if (!TryCreateAuthenticatedConfiguration(
                configuredEndpoint,
                configuredToken,
                out var endpoint,
                out var authorization))
        {
            return false;
        }

        request = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = JsonContent.Create(launchEvent)
        };
        request.Headers.Authorization = authorization;
        return true;
    }

    private static bool TryCreateAuthenticatedConfiguration(
        string? configuredEndpoint,
        string? configuredToken,
        [NotNullWhen(true)] out Uri? endpoint,
        [NotNullWhen(true)] out AuthenticationHeaderValue? authorization)
    {
        endpoint = null;
        authorization = null;
        if (!Uri.TryCreate(
                configuredEndpoint,
                UriKind.Absolute,
                out var parsed) ||
            parsed.Scheme != Uri.UriSchemeHttps ||
            !IPAddress.TryParse(parsed.Host, out var address) ||
            !IPAddress.IsLoopback(address) ||
            string.IsNullOrWhiteSpace(parsed.Host) ||
            !string.IsNullOrEmpty(parsed.UserInfo) ||
            !string.IsNullOrEmpty(parsed.Query) ||
            !string.IsNullOrEmpty(parsed.Fragment))
        {
            return false;
        }

        if (!TryCreateAuthorization(
                configuredToken,
                out authorization))
        {
            return false;
        }

        endpoint = parsed;
        return true;
    }

    private static CapturedConfiguration CaptureAndScrubProcessConfiguration() =>
        CaptureAndScrubConfiguration(
            name => Environment.GetEnvironmentVariable(
                name,
                EnvironmentVariableTarget.Process),
            (name, value) => Environment.SetEnvironmentVariable(
                name,
                value,
                EnvironmentVariableTarget.Process));

    private static void ScrubConfigurationEnvironment(
        Action<string, string?> setEnvironmentVariable)
    {
        Exception? firstFailure = null;
        foreach (var variableName in ConfigurationEnvironmentVariables)
        {
            try
            {
                setEnvironmentVariable(variableName, null);
            }
            catch (Exception exception)
            {
                firstFailure ??= exception;
            }
        }

        if (firstFailure is not null)
            ExceptionDispatchInfo.Capture(firstFailure).Throw();
    }

    private static bool TryCreateAuthorization(
        string? token,
        [NotNullWhen(true)] out AuthenticationHeaderValue? authorization)
    {
        authorization = null;
        if (token is null ||
            token.Length is < 1 or > 4096 ||
            token != token.Trim() ||
            token.Any(char.IsControl))
        {
            return false;
        }

        return AuthenticationHeaderValue.TryParse(
                   $"Bearer {token}",
                   out authorization) &&
               authorization.Scheme.Equals(
                   "Bearer",
                   StringComparison.OrdinalIgnoreCase) &&
               !string.IsNullOrWhiteSpace(authorization.Parameter);
    }

    internal sealed class CapturedConfiguration
    {
        internal CapturedConfiguration(string? endpoint, string? token)
        {
            Endpoint = endpoint;
            Token = token;
        }

        internal string? Endpoint { get; }

        internal string? Token { get; }
    }
}
