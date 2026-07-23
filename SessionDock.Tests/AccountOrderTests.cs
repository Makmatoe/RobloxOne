using SessionDock.Models;
using SessionDock.Services;

namespace SessionDock.Tests;

public sealed class AccountOrderTests : IDisposable
{
    private const string FirstKey = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string SecondKey = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    private const string ThirdKey = "cccccccccccccccccccccccccccccccc";
    private readonly string _storageDirectory = Path.Combine(
        Path.GetTempPath(),
        $"SessionDock-account-order-tests-{Guid.NewGuid():N}");

    [Fact]
    public void TryMoveBefore_FirstToEnd_PreservesObjectsAndActiveAccount()
    {
        var settings = CreateSettings();
        var first = settings.Accounts[0];

        var moved = AccountOrder.TryMoveBefore(
            settings.Accounts,
            FirstKey,
            beforeKey: null);

        Assert.True(moved);
        Assert.Equal([SecondKey, ThirdKey, FirstKey], GetKeys(settings));
        Assert.Same(first, settings.Accounts[2]);
        Assert.Equal(SecondKey, settings.ActiveAccountKey);
    }

    [Fact]
    public void TryMoveBefore_LastToFirst_UsesCaseInsensitiveKeys()
    {
        var settings = CreateSettings();
        var third = settings.Accounts[2];

        var moved = AccountOrder.TryMoveBefore(
            settings.Accounts,
            ThirdKey.ToUpperInvariant(),
            FirstKey.ToUpperInvariant());

        Assert.True(moved);
        Assert.Equal([ThirdKey, FirstKey, SecondKey], GetKeys(settings));
        Assert.Same(third, settings.Accounts[0]);
    }

    [Theory]
    [InlineData(FirstKey, ThirdKey, SecondKey, FirstKey, ThirdKey)]
    [InlineData(ThirdKey, SecondKey, FirstKey, ThirdKey, SecondKey)]
    public void TryMoveBefore_OnePositionMove_AdjustsForRemoval(
        string sourceKey,
        string beforeKey,
        string expectedFirst,
        string expectedSecond,
        string expectedThird)
    {
        var settings = CreateSettings();

        Assert.True(AccountOrder.TryMoveBefore(
            settings.Accounts,
            sourceKey,
            beforeKey));

        Assert.Equal(
            [expectedFirst, expectedSecond, expectedThird],
            GetKeys(settings));
    }

    [Theory]
    [InlineData(SecondKey, SecondKey)]
    [InlineData(SecondKey, ThirdKey)]
    [InlineData("dddddddddddddddddddddddddddddddd", FirstKey)]
    [InlineData(FirstKey, "dddddddddddddddddddddddddddddddd")]
    public void TryMoveBefore_SelfAdjacentOrStaleTarget_IsNoOp(
        string sourceKey,
        string? beforeKey)
    {
        var settings = CreateSettings();
        var original = settings.Accounts.ToArray();

        Assert.False(AccountOrder.WouldMoveBefore(
            settings.Accounts,
            sourceKey,
            beforeKey));
        Assert.False(AccountOrder.TryMoveBefore(
            settings.Accounts,
            sourceKey,
            beforeKey));
        Assert.Equal([FirstKey, SecondKey, ThirdKey], GetKeys(settings));
        Assert.True(original.SequenceEqual(settings.Accounts));
    }

    [Fact]
    public void TryMoveBefore_LastToEnd_IsNoOp()
    {
        var settings = CreateSettings();

        Assert.False(AccountOrder.WouldMoveBefore(
            settings.Accounts,
            ThirdKey,
            beforeKey: null));
        Assert.False(AccountOrder.TryMoveBefore(
            settings.Accounts,
            ThirdKey,
            beforeKey: null));
        Assert.Equal([FirstKey, SecondKey, ThirdKey], GetKeys(settings));
    }

    [Fact]
    public void TryMoveBefore_SingleAccount_IsNoOp()
    {
        var account = CreateAccount(FirstKey, 1, "first");
        var accounts = new List<AccountProfile> { account };

        Assert.False(AccountOrder.WouldMoveBefore(
            accounts,
            FirstKey,
            beforeKey: null));
        Assert.False(AccountOrder.TryMoveBefore(
            accounts,
            FirstKey,
            beforeKey: null));
        Assert.Same(account, Assert.Single(accounts));
    }

    [Fact]
    public async Task CoordinatorCommit_ReorderPersistsExactSnapshotOrder()
    {
        var settings = CreateSettings();
        AppSettings? saved = null;
        var coordinator = new SettingsMutationCoordinator(
            settings,
            new SerializedSettingsWriter(snapshot => saved = snapshot));

        var result = await coordinator.CommitAsync(() =>
            Assert.True(AccountOrder.TryMoveBefore(
                settings.Accounts,
                FirstKey,
                beforeKey: null)));

        Assert.True(result.Committed);
        Assert.NotNull(saved);
        Assert.Equal([SecondKey, ThirdKey, FirstKey], GetKeys(saved));
        Assert.Equal(SecondKey, saved.ActiveAccountKey);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CoordinatorCommit_ExpectedWriteFailureRestoresExactOrder(
        bool unauthorized)
    {
        var settings = CreateSettings();
        var original = settings.Accounts.ToArray();
        var coordinator = new SettingsMutationCoordinator(
            settings,
            new SerializedSettingsWriter(_ => throw (unauthorized
                ? new UnauthorizedAccessException("write denied")
                : new IOException("disk unavailable"))));

        var result = await coordinator.CommitAsync(() =>
            Assert.True(AccountOrder.TryMoveBefore(
                settings.Accounts,
                FirstKey,
                beforeKey: null)));

        Assert.False(result.Committed);
        if (unauthorized)
            Assert.IsType<UnauthorizedAccessException>(result.Failure);
        else
            Assert.IsType<IOException>(result.Failure);
        Assert.Equal([FirstKey, SecondKey, ThirdKey], GetKeys(settings));
        Assert.True(original.SequenceEqual(settings.Accounts));
    }

    [Fact]
    public void SettingsService_SaveAndLoad_PreservesReorderedAccounts()
    {
        var service = new SettingsService(_storageDirectory);
        var settings = CreateSettings();
        Assert.True(AccountOrder.TryMoveBefore(
            settings.Accounts,
            ThirdKey,
            FirstKey));

        service.Save(settings);
        var loaded = new SettingsService(_storageDirectory).Load();

        Assert.Equal([ThirdKey, FirstKey, SecondKey], GetKeys(loaded));
        Assert.Equal(SecondKey, loaded.ActiveAccountKey);
    }

    public void Dispose()
    {
        if (Directory.Exists(_storageDirectory))
            Directory.Delete(_storageDirectory, recursive: true);
    }

    private static AppSettings CreateSettings() => new()
    {
        Accounts =
        [
            CreateAccount(FirstKey, 1, "first"),
            CreateAccount(SecondKey, 2, "second"),
            CreateAccount(ThirdKey, 3, "third")
        ],
        ActiveAccountKey = SecondKey
    };

    private static AccountProfile CreateAccount(
        string key,
        long userId,
        string username) => new()
        {
            Key = key,
            UserId = userId,
            Username = username,
            SessionFolder = $@"Profiles\{key}"
        };

    private static string[] GetKeys(AppSettings settings) =>
        settings.Accounts.Select(account => account.Key).ToArray();
}
