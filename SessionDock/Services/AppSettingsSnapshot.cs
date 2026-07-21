using SessionDock.Models;

namespace SessionDock.Services;

internal static class AppSettingsSnapshot
{
    internal static AppSettings Create(AppSettings source)
    {
        ArgumentNullException.ThrowIfNull(source);
        return new AppSettings
        {
            Accounts = source.Accounts
                .Select(Clone)
                .ToList(),
            ActiveAccountKey = source.ActiveAccountKey,
            RecentExperiences = source.RecentExperiences
                .Select(Clone)
                .ToList(),
            UiSoundsEnabled = source.UiSoundsEnabled,
            StartupSound = source.StartupSound,
            CustomStartupSoundFileName = source.CustomStartupSoundFileName,
            LockedUserId = source.LockedUserId,
            LockedUsername = source.LockedUsername,
            PlaceId = source.PlaceId,
            Destination = source.Destination
        };
    }

    internal static AccountProfile Clone(AccountProfile source) => new()
    {
        Key = source.Key,
        UserId = source.UserId,
        Username = source.Username,
        SessionFolder = source.SessionFolder,
        Label = source.Label,
        ColorHex = source.ColorHex,
        Destination = source.Destination
    };

    internal static void Copy(AccountProfile source, AccountProfile target)
    {
        target.Key = source.Key;
        target.UserId = source.UserId;
        target.Username = source.Username;
        target.SessionFolder = source.SessionFolder;
        target.Label = source.Label;
        target.ColorHex = source.ColorHex;
        target.Destination = source.Destination;
    }

    internal static RecentExperience Clone(RecentExperience source) => new()
    {
        Destination = source.Destination,
        PlaceId = source.PlaceId,
        Name = source.Name,
        CustomName = source.CustomName,
        IsPrivateServer = source.IsPrivateServer,
        IsPinned = source.IsPinned,
        ServerJobId = source.ServerJobId,
        AccountUserId = source.AccountUserId,
        AccountUsername = source.AccountUsername,
        LastLaunchedAt = source.LastLaunchedAt
    };

    internal static void Copy(RecentExperience source, RecentExperience target)
    {
        target.Destination = source.Destination;
        target.PlaceId = source.PlaceId;
        target.Name = source.Name;
        target.CustomName = source.CustomName;
        target.IsPrivateServer = source.IsPrivateServer;
        target.IsPinned = source.IsPinned;
        target.ServerJobId = source.ServerJobId;
        target.AccountUserId = source.AccountUserId;
        target.AccountUsername = source.AccountUsername;
        target.LastLaunchedAt = source.LastLaunchedAt;
    }
}
