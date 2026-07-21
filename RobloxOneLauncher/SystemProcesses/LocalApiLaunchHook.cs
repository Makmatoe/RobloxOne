using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;

namespace RobloxOneLauncher.SystemProcesses;

public sealed class LocalApiLaunchHook : ILaunchHook
{
    public const string UrlEnvironmentVariable = "ROBLOX_ONE_LAUNCH_HOOK_URL";
    public const string TokenEnvironmentVariable = "ROBLOX_ONE_LAUNCH_HOOK_BEARER_TOKEN";
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(5);
    private readonly HttpClient _client;

    public bool IsConfigured => TryGetEndpoint(out _);

    public LocalApiLaunchHook()
    {
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
        _client.DefaultRequestHeaders.UserAgent.ParseAdd("RobloxOne/1.0");
    }

    public async Task NotifyLaunchAsync(
        LaunchHookEvent launchEvent,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(launchEvent);
        if (!TryGetEndpoint(out var endpoint))
            return;

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
            {
                Content = JsonContent.Create(launchEvent)
            };
            var token = Environment.GetEnvironmentVariable(TokenEnvironmentVariable);
            if (token is not null)
            {
                if (!TryCreateAuthorization(token, out var authorization))
                {
                    Trace.WriteLine(
                        "Launch hook was not delivered: bearer token was invalid.");
                    return;
                }

                request.Headers.Authorization = authorization;
            }

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
        catch (Exception ex) when (
            ex is HttpRequestException or TaskCanceledException or OperationCanceledException)
        {
            // The external hook is optional and must never break a Roblox launch.
            Trace.WriteLine(
                $"Launch hook was not delivered: {ex.GetType().Name}.");
        }
    }

    public void Dispose() => _client.Dispose();

    private static bool TryGetEndpoint(out Uri? endpoint)
    {
        endpoint = null;
        var configured = Environment.GetEnvironmentVariable(UrlEnvironmentVariable);
        if (!Uri.TryCreate(configured, UriKind.Absolute, out var parsed) ||
            (parsed.Scheme != Uri.UriSchemeHttp &&
             parsed.Scheme != Uri.UriSchemeHttps) ||
            !IPAddress.TryParse(parsed.Host, out var address) ||
            !IPAddress.IsLoopback(address) ||
            string.IsNullOrWhiteSpace(parsed.Host) ||
            !string.IsNullOrEmpty(parsed.UserInfo) ||
            !string.IsNullOrEmpty(parsed.Query) ||
            !string.IsNullOrEmpty(parsed.Fragment))
        {
            return false;
        }

        endpoint = parsed;
        return true;
    }

    private static bool TryCreateAuthorization(
        string token,
        out AuthenticationHeaderValue? authorization)
    {
        authorization = null;
        if (token.Length is < 1 or > 4096 ||
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
}
