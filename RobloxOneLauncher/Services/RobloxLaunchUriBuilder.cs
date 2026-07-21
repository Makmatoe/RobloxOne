using System.Text.RegularExpressions;
using RobloxOneLauncher.Models;

namespace RobloxOneLauncher.Services;

public static class RobloxLaunchUriBuilder
{
    private const int MaximumAuthenticationTicketLength = 8 * 1024;
    private static readonly Regex LocalePattern = new(
        "^[a-z]{2,3}_[a-z]{2}$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public static string Build(
        LaunchTarget target,
        string authenticationTicket,
        string? serverJobId = null,
        string? locale = null)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentException.ThrowIfNullOrWhiteSpace(authenticationTicket);
        if (authenticationTicket.Length > MaximumAuthenticationTicketLength ||
            authenticationTicket.Any(char.IsControl))
        {
            throw new ArgumentException(
                "The authentication ticket is not valid.",
                nameof(authenticationTicket));
        }
        if (target.PlaceId <= 0)
            throw new ArgumentOutOfRangeException(nameof(target), "A valid Place ID is required.");

        string? normalizedServerJobId = null;
        if (serverJobId is not null)
        {
            if (!Guid.TryParse(serverJobId, out var parsedServerJobId))
                throw new ArgumentException("The server JobId is not valid.", nameof(serverJobId));
            normalizedServerJobId = parsedServerJobId.ToString("D");
        }

        var browserTrackerId = Random.Shared.NextInt64(1, long.MaxValue);
        var normalizedLocale = NormalizeLocale(locale);
        var joinAttemptId = Guid.NewGuid().ToString();
        var requestType = normalizedServerJobId is not null
            ? "RequestGameJob"
            : target.LinkCode is null
                ? "RequestGame"
                : "RequestPrivateGame";
        var destinationParameters = normalizedServerJobId is not null
            ? $"&gameId={Uri.EscapeDataString(normalizedServerJobId)}" +
              (target.LinkCode is null
                  ? "&isPlayTogetherGame=false"
                  : $"&accessCode=&linkCode={Uri.EscapeDataString(target.LinkCode)}")
            : target.LinkCode is null
                ? "&isPlayTogetherGame=false"
                : $"&accessCode=&linkCode={Uri.EscapeDataString(target.LinkCode)}";
        var placeLauncherUrl =
            "https://www.roblox.com/Game/PlaceLauncher.ashx" +
            $"?request={requestType}&browserTrackerId={browserTrackerId}" +
            $"&placeId={target.PlaceId}" +
            destinationParameters +
            $"&joinAttemptId={joinAttemptId}&joinAttemptOrigin=PlayButton";

        return "roblox-player:1" +
               "+launchmode:play" +
               $"+gameinfo:{Uri.EscapeDataString(authenticationTicket)}" +
               $"+launchtime:{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}" +
               $"+placelauncherurl:{Uri.EscapeDataString(placeLauncherUrl)}" +
               $"+browsertrackerid:{browserTrackerId}" +
               $"+robloxLocale:{normalizedLocale}" +
               $"+gameLocale:{normalizedLocale}" +
               "+channel:";
    }

    private static string NormalizeLocale(string? locale)
    {
        var normalized = locale?.Trim().Replace('-', '_').ToLowerInvariant();
        return normalized is not null && LocalePattern.IsMatch(normalized)
            ? normalized
            : "en_us";
    }
}
