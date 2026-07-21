using SessionDock.Models;
using SessionDock.Services;

namespace SessionDock.Tests;

public sealed class BatchDestinationPlannerTests
{
    private const string ServerJobId = "a18c877e-4070-4a84-a5f7-36668b46a77d";

    [Fact]
    public void TryCreate_UsesEachAccountsOwnDestination()
    {
        var first = CreateAccount("First", "111");
        var second = CreateAccount("Second", "222");

        var success = BatchDestinationPlanner.TryCreate(
            [first, second],
            [],
            out var plans,
            out _);

        Assert.True(success);
        Assert.Equal(111, plans[0].LaunchInput.Target.PlaceId);
        Assert.Equal(222, plans[1].LaunchInput.Target.PlaceId);
        Assert.False(plans[0].UsesFirstDestination);
        Assert.False(plans[1].UsesFirstDestination);
    }

    [Fact]
    public void TryCreate_BlankDestinationUsesFirstSelectedDestination()
    {
        var first = CreateAccount("First", "111");
        var second = CreateAccount("Second", null);

        var success = BatchDestinationPlanner.TryCreate(
            [first, second],
            [],
            out var plans,
            out _);

        Assert.True(success);
        Assert.Equal("111", plans[1].LaunchInput.AccountDestination);
        Assert.Equal(111, plans[1].LaunchInput.Target.PlaceId);
        Assert.True(plans[1].UsesFirstDestination);
        Assert.Null(second.Destination);
    }

    [Fact]
    public void TryCreate_BlankFirstDestinationIsRejected()
    {
        var first = CreateAccount("First", null);
        var second = CreateAccount("Second", "222");

        var success = BatchDestinationPlanner.TryCreate(
            [first, second],
            [],
            out var plans,
            out var error);

        Assert.False(success);
        Assert.Empty(plans);
        Assert.Contains("first selected account", error, StringComparison.Ordinal);
    }

    [Fact]
    public void TryCreate_InvalidLaterDestinationReturnsNoPartialPlan()
    {
        var first = CreateAccount("First", "111");
        var second = CreateAccount("Second", "not a destination");

        var success = BatchDestinationPlanner.TryCreate(
            [first, second],
            [],
            out var plans,
            out var error);

        Assert.False(success);
        Assert.Empty(plans);
        Assert.StartsWith("@Second:", error, StringComparison.Ordinal);
    }

    [Fact]
    public void TryCreate_ResolvesTrackedJobIdsPerAccount()
    {
        var first = CreateAccount("First", "111");
        var second = CreateAccount("Second", ServerJobId);
        var recent = new RecentExperience
        {
            Destination = "222",
            PlaceId = 222,
            ServerJobId = ServerJobId,
            LastLaunchedAt = DateTimeOffset.UtcNow
        };

        var success = BatchDestinationPlanner.TryCreate(
            [first, second],
            [recent],
            out var plans,
            out _);

        Assert.True(success);
        Assert.Null(plans[0].LaunchInput.ServerJobId);
        Assert.Equal(111, plans[0].LaunchInput.Target.PlaceId);
        Assert.Equal(ServerJobId, plans[1].LaunchInput.ServerJobId);
        Assert.Equal(222, plans[1].LaunchInput.Target.PlaceId);
    }

    private static AccountProfile CreateAccount(
        string username,
        string? destination) =>
        new()
        {
            Username = username,
            UserId = username.Length,
            Destination = destination
        };
}
