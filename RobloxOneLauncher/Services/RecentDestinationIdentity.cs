using RobloxOneLauncher.Models;

namespace RobloxOneLauncher.Services;

public static class RecentDestinationIdentity
{
    public static string CreateKey(RecentExperience recent)
    {
        ArgumentNullException.ThrowIfNull(recent);
        if (!DestinationParser.TryParse(recent.Destination, out var target, out _))
            return $"raw:{recent.Destination.Trim()}";

        if (target!.ShareCode is not null)
            return $"share:{target.ShareCode}";
        if (target.LinkCode is not null)
            return $"private:{target.PlaceId}:{target.LinkCode}";
        return $"public:{target.PlaceId}";
    }

    public static bool Matches(RecentExperience left, RecentExperience right) =>
        CreateKey(left).Equals(CreateKey(right), StringComparison.Ordinal);
}
