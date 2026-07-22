using SessionDock.Services;

namespace SessionDock.Tests;

public sealed class RunningClientRegistryTests
{
    [Fact]
    public void Track_PreservesMultipleClientsForOneAccount()
    {
        var registry = new RunningClientRegistry();
        var first = CreateIdentity(101, 0);
        var second = CreateIdentity(202, 1);
        var attribution = CreateAttribution("main");

        registry.Track(first, attribution);
        registry.Track(second, attribution);

        Assert.Equal(2, registry.Count);
        Assert.True(registry.TryGet(first, out var firstResult));
        Assert.True(registry.TryGet(second, out var secondResult));
        Assert.Equal("main", firstResult!.AccountLabel);
        Assert.Equal("main", secondResult!.AccountLabel);
    }

    [Fact]
    public void Track_ReplacesOnlyTheSameExactIdentity()
    {
        var registry = new RunningClientRegistry();
        var identity = CreateIdentity(101, 0);
        registry.Track(identity, CreateAttribution("old"));

        registry.Track(
            new RobloxClientProcessIdentity(
                identity.ProcessId,
                identity.StartTimeUtc,
                identity.ExecutablePath.ToUpperInvariant()),
            CreateAttribution("new"));

        Assert.Equal(1, registry.Count);
        Assert.True(registry.TryGet(identity, out var result));
        Assert.Equal("new", result!.AccountLabel);
    }

    [Fact]
    public void Prune_RemovesExitedIdentityWithoutGuessingReplacementPid()
    {
        var registry = new RunningClientRegistry();
        var exited = CreateIdentity(101, 0);
        var running = CreateIdentity(202, 1);
        registry.Track(exited, CreateAttribution("removed account"));
        registry.Track(running, CreateAttribution("current account"));

        registry.Prune([running]);

        Assert.Equal(1, registry.Count);
        Assert.False(registry.TryGet(exited, out _));
        Assert.True(registry.TryGet(running, out var result));
        Assert.Equal("current account", result!.AccountLabel);
    }

    [Fact]
    public void Reconcile_PreservesAttributionAcrossIncompleteScan()
    {
        var registry = new RunningClientRegistry();
        var identity = CreateIdentity(101, 0);
        registry.Track(identity, CreateAttribution("main"));

        registry.Reconcile([], scanIsComplete: false);

        Assert.True(registry.TryGet(identity, out var preserved));
        Assert.Equal("main", preserved!.AccountLabel);

        registry.Reconcile([identity], scanIsComplete: true);
        Assert.True(registry.TryGet(identity, out _));

        registry.Reconcile([], scanIsComplete: true);
        Assert.False(registry.TryGet(identity, out _));
    }

    private static RobloxClientProcessIdentity CreateIdentity(
        int processId,
        int addedMinutes) =>
        new(
            processId,
            new DateTime(2026, 7, 22, 12, 0, 0, DateTimeKind.Utc)
                .AddMinutes(addedMinutes),
            @"C:\TestData\Roblox\Versions\version-a\RobloxPlayerBeta.exe");

    private static RunningClientAttribution CreateAttribution(string label) =>
        new(
            "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
            123,
            "test_account",
            label,
            "#4D8DFF",
            920587237,
            "Test Experience",
            new DateTimeOffset(2026, 7, 22, 12, 0, 0, TimeSpan.Zero));
}
