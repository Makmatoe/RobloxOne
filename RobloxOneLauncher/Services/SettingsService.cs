using System.IO;
using System.Text.Json;
using RobloxOneLauncher.Models;

namespace RobloxOneLauncher.Services;

public sealed class SettingsService
{
    public static readonly IReadOnlyList<string> AccountColors =
        ["#7C5CFC", "#4D8DFF", "#27B58A", "#E0A33A", "#E36B8D", "#A56DE2"];
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly string _settingsPath;

    public SettingsService()
    {
        var directory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "RobloxOne");
        Directory.CreateDirectory(directory);
        _settingsPath = Path.Combine(directory, "settings.json");
    }

    public string GetSessionDataDirectory(AccountProfile profile)
    {
        var root = Path.GetFullPath(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "RobloxOne"));
        var path = Path.GetFullPath(Path.Combine(root, profile.SessionFolder));
        if (!path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The account session path is outside Roblox One.");
        return path;
    }

    public AppSettings Load()
    {
        try
        {
            var settings = File.Exists(_settingsPath)
                ? JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(_settingsPath)) ?? new()
                : new();
            var migrated = MigrateLegacyAccount(settings);
            Normalize(settings);
            migrated |= MigrateLegacyDestinations(settings);
            if (migrated)
            {
                try
                {
                    Save(settings);
                }
                catch
                {
                    // Keep the successfully loaded settings in memory for this run.
                }
            }
            return settings;
        }
        catch
        {
            return new();
        }
    }

    public void Save(AppSettings settings)
    {
        var temporaryPath = _settingsPath + ".tmp";
        File.WriteAllText(temporaryPath, JsonSerializer.Serialize(settings, JsonOptions));
        File.Move(temporaryPath, _settingsPath, overwrite: true);
    }

    private static void Normalize(AppSettings settings)
    {
        settings.Accounts ??= [];
        settings.RecentExperiences ??= [];
        settings.Accounts = settings.Accounts
            .Where(IsValidAccount)
            .Select(NormalizeAccountMetadata)
            .GroupBy(account => account.UserId)
            .Select(group => group.First())
            .GroupBy(account => account.Key, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToList();

        if (settings.ActiveAccountKey is not null &&
            !settings.Accounts.Any(account =>
                account.Key.Equals(
                    settings.ActiveAccountKey,
                    StringComparison.OrdinalIgnoreCase)))
        {
            settings.ActiveAccountKey = settings.Accounts.FirstOrDefault()?.Key;
        }

        if (!UiSoundService.IsValidStartupSound(settings.StartupSound))
            settings.StartupSound = UiSoundService.DefaultStartupSound;
        if (!UiSoundService.IsValidImportedFileName(
                settings.CustomStartupSoundFileName))
        {
            settings.CustomStartupSoundFileName = null;
        }
        if (settings.StartupSound.Equals(
                UiSoundService.StartupCustom,
                StringComparison.OrdinalIgnoreCase) &&
            settings.CustomStartupSoundFileName is null)
        {
            settings.StartupSound = UiSoundService.DefaultStartupSound;
        }

        var normalizedRecent = settings.RecentExperiences
            .Where(IsValidRecentExperience)
            .Select(NormalizeRecentMetadata)
            .OrderByDescending(item => item.IsPinned)
            .ThenByDescending(item => item.LastLaunchedAt)
            .GroupBy(
                item => $"{item.AccountUserId}:{RecentDestinationIdentity.CreateKey(item)}",
                StringComparer.Ordinal)
            .Select(group => group.First())
            .ToList();
        HarmonizeRecentCustomNames(normalizedRecent);
        settings.RecentExperiences = normalizedRecent
            .Where(item => item.IsPinned)
            .Take(50)
            .Concat(normalizedRecent.Where(item => !item.IsPinned).Take(50))
            .ToList();
    }

    private static bool MigrateLegacyAccount(AppSettings settings)
    {
        settings.Accounts ??= [];
        var changed = false;
        if (settings.Accounts.Count == 0 &&
            settings.LockedUserId is > 0 &&
            !string.IsNullOrWhiteSpace(settings.LockedUsername))
        {
            var migrated = new AccountProfile
            {
                Key = "legacy",
                UserId = settings.LockedUserId.Value,
                Username = settings.LockedUsername,
                SessionFolder = "WebSession"
            };
            settings.Accounts.Add(migrated);
            settings.ActiveAccountKey = migrated.Key;
            changed = true;
        }

        if (settings.LockedUserId is not null || settings.LockedUsername is not null)
        {
            settings.LockedUserId = null;
            settings.LockedUsername = null;
            changed = true;
        }

        return changed;
    }

    private static bool MigrateLegacyDestinations(AppSettings settings)
    {
        var legacyDestination = NormalizeDestination(settings.Destination)
            ?? (settings.PlaceId is > 0 ? settings.PlaceId.Value.ToString() : null);
        var changed = false;

        foreach (var account in settings.Accounts.Where(account =>
                     account.Destination is null && legacyDestination is not null))
        {
            account.Destination = legacyDestination;
            changed = true;
        }

        if (settings.Destination is not null || settings.PlaceId is not null)
        {
            settings.Destination = null;
            settings.PlaceId = null;
            changed = true;
        }

        return changed;
    }

    private static bool IsValidAccount(AccountProfile account)
    {
        if (account is null ||
            account.UserId <= 0 ||
            string.IsNullOrWhiteSpace(account.Username) ||
            account.Username.Length > 50 ||
            !IsValidKey(account.Key))
        {
            return false;
        }

        var expectedFolder = account.Key == "legacy"
            ? "WebSession"
            : $@"Profiles\{account.Key}";
        return account.SessionFolder?.Equals(
            expectedFolder,
            StringComparison.OrdinalIgnoreCase) == true;
    }

    private static bool IsValidRecentExperience(RecentExperience item) =>
        item is not null &&
        item.PlaceId > 0 &&
        !string.IsNullOrWhiteSpace(item.Destination) &&
        item.Destination.Length <= 4096 &&
        (item.Name is null || item.Name.Length <= 200) &&
        (item.CustomName is null || item.CustomName.Length <= 80) &&
        item.AccountUserId >= 0 &&
        (item.AccountUsername is null || item.AccountUsername.Length <= 50) &&
        item.LastLaunchedAt > DateTimeOffset.UnixEpoch &&
        DestinationParser.TryParse(item.Destination, out _, out _);

    private static AccountProfile NormalizeAccountMetadata(AccountProfile account)
    {
        var label = account.Label?.Trim();
        account.Label = string.IsNullOrWhiteSpace(label)
            ? null
            : label[..Math.Min(label.Length, 40)];
        account.ColorHex = AccountColors.Contains(
            account.ColorHex ?? string.Empty,
            StringComparer.OrdinalIgnoreCase)
            ? account.ColorHex!.ToUpperInvariant()
            : null;
        account.Destination = NormalizeDestination(account.Destination);
        return account;
    }

    private static RecentExperience NormalizeRecentMetadata(RecentExperience item)
    {
        var customName = item.CustomName?.Trim();
        item.CustomName = string.IsNullOrWhiteSpace(customName)
            ? null
            : customName[..Math.Min(customName.Length, 80)];
        item.ServerJobId = Guid.TryParse(item.ServerJobId, out var serverJobId)
            ? serverJobId.ToString("D")
            : null;
        return item;
    }

    private static void HarmonizeRecentCustomNames(
        IReadOnlyCollection<RecentExperience> recentExperiences)
    {
        foreach (var destinationGroup in recentExperiences.GroupBy(
                     RecentDestinationIdentity.CreateKey,
                     StringComparer.Ordinal))
        {
            var customName = destinationGroup
                .Where(item => item.CustomName is not null)
                .OrderByDescending(item => item.LastLaunchedAt)
                .Select(item => item.CustomName)
                .FirstOrDefault();
            foreach (var item in destinationGroup)
                item.CustomName = customName;
        }
    }

    private static string? NormalizeDestination(string? destination)
    {
        var value = destination?.Trim();
        return !string.IsNullOrWhiteSpace(value) &&
               value.Length <= 4096 &&
               DestinationParser.TryParse(value, out _, out _)
            ? value
            : null;
    }

    private static bool IsValidKey(string key) =>
        key == "legacy" ||
        key is { Length: 32 } && key.All(Uri.IsHexDigit);
}
