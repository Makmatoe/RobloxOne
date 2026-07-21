using RobloxOneLauncher.Models;
using RobloxOneLauncher.Services;

namespace RobloxOneLauncher.Tests;

public sealed class RecentHistoryScopeTests
{
    [Fact]
    public void CanClear_RespectsTypeAndAccountTogether()
    {
        var privateEntry = new RecentExperience
        {
            AccountUserId = 10,
            IsPrivateServer = true
        };

        Assert.True(RecentHistoryScope.CanClear(
            privateEntry,
            RecentServerType.Private,
            10));
        Assert.False(RecentHistoryScope.CanClear(
            privateEntry,
            RecentServerType.Public,
            10));
        Assert.False(RecentHistoryScope.CanClear(
            privateEntry,
            RecentServerType.Private,
            20));
    }

    [Fact]
    public void CanClear_NeverIncludesFavorites()
    {
        var favorite = new RecentExperience
        {
            AccountUserId = 10,
            IsPinned = true
        };

        Assert.False(RecentHistoryScope.CanClear(
            favorite,
            RecentServerType.All,
            0));
    }
}
