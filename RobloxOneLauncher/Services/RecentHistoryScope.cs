using RobloxOneLauncher.Models;

namespace RobloxOneLauncher.Services;

public enum RecentServerType
{
    All,
    Public,
    Private
}

public static class RecentHistoryScope
{
    public static bool MatchesType(
        RecentExperience item,
        RecentServerType serverType)
    {
        ArgumentNullException.ThrowIfNull(item);
        return serverType switch
        {
            RecentServerType.Public => !item.IsPrivateServer,
            RecentServerType.Private => item.IsPrivateServer,
            _ => true
        };
    }

    public static bool CanClear(
        RecentExperience item,
        RecentServerType serverType,
        long accountUserId) =>
        !item.IsPinned &&
        MatchesType(item, serverType) &&
        (accountUserId == 0 || item.AccountUserId == accountUserId);
}
