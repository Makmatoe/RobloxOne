using RobloxOneLauncher.Models;

namespace RobloxOneLauncher.Services;

public static class RecentServerJoinResolver
{
    public static bool TryResolve(
        string input,
        IEnumerable<RecentExperience> recentExperiences,
        out RecentExperience? trackedServer)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(recentExperiences);
        trackedServer = null;
        if (!Guid.TryParse(input.Trim(), out var requestedServerJobId))
            return false;

        trackedServer = recentExperiences
            .Where(item =>
                item.PlaceId > 0 &&
                Guid.TryParse(item.ServerJobId, out var savedServerJobId) &&
                savedServerJobId == requestedServerJobId)
            .OrderByDescending(item => item.LastLaunchedAt)
            .FirstOrDefault();
        return trackedServer is not null;
    }
}
