using RobloxOneLauncher.Models;

namespace RobloxOneLauncher.Services;

public static class RobloxLaunchUriBuilder
{
    public static string Build(
        LaunchTarget target,
        string authenticationTicket,
        string? serverJobId = null)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentException.ThrowIfNullOrWhiteSpace(authenticationTicket);
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
               "+robloxLocale:en_us" +
               "+gameLocale:en_us" +
               "+channel:";
    }
}
