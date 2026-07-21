using System.IO;
using System.Text.Json;
using RobloxOneLauncher.Models;

namespace RobloxOneLauncher.Services;

public sealed class SettingsService
{
    public static readonly IReadOnlyList<string> AccountColors =
        ["#7C5CFC", "#4D8DFF", "#27B58A", "#E0A33A", "#E36B8D", "#A56DE2"];
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly string _rootDirectory;
    private readonly string _backupPath;
    private readonly string _profileCleanupGuardPath;
    private readonly string _settingsPath;
    private bool _primaryIsUnreadable;

    public SettingsService(string? storageDirectory = null)
    {
        _rootDirectory = Path.GetFullPath(storageDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "RobloxOne"));
        Directory.CreateDirectory(_rootDirectory);
        _settingsPath = Path.Combine(_rootDirectory, "settings.json");
        _backupPath = Path.Combine(_rootDirectory, "settings.backup.json");
        _profileCleanupGuardPath = Path.Combine(
            _rootDirectory,
            "profile-cleanup-paused.txt");
        CanReconcileProfiles = !File.Exists(_profileCleanupGuardPath);
    }

    public string? LoadNotice { get; private set; }
    public bool CanReconcileProfiles { get; private set; } = true;

    public string GetSessionDataDirectory(AccountProfile profile)
    {
        var path = Path.GetFullPath(Path.Combine(_rootDirectory, profile.SessionFolder));
        if (!path.StartsWith(
                _rootDirectory + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("The account session path is outside Roblox One.");
        }
        return path;
    }

    public AppSettings Load()
    {
        LoadNotice = null;
        CanReconcileProfiles = !File.Exists(_profileCleanupGuardPath);
        _primaryIsUnreadable = false;
        var primaryExists = File.Exists(_settingsPath);
        if (!primaryExists && !File.Exists(_backupPath))
        {
            if (HasPreservedSettingsFiles())
            {
                PauseProfileCleanup();
                AddProfileCleanupNotice();
            }
            return new();
        }

        if (primaryExists &&
            TryLoadFile(_settingsPath, out var settings, out var migrated))
        {
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
            AddProfileCleanupNotice();
            return settings;
        }

        if (TryLoadFile(_backupPath, out settings, out migrated))
        {
            _primaryIsUnreadable = true;
            if (PreserveUnreadableFile(_settingsPath))
            {
                _primaryIsUnreadable = false;
                try
                {
                    WriteFresh(settings);
                    if (migrated)
                        Save(settings);
                }
                catch
                {
                    // The validated backup remains available if restoration is blocked.
                }
            }
            LoadNotice = primaryExists
                ? "Roblox One recovered your accounts and history from the local settings backup. The unreadable file was preserved for diagnosis."
                : "Roblox One recovered your accounts and history from the local settings backup after the primary file was missing.";
            AddProfileCleanupNotice();
            return settings;
        }

        _primaryIsUnreadable = !PreserveUnreadableFile(_settingsPath);
        PreserveUnreadableFile(_backupPath);
        PauseProfileCleanup();
        LoadNotice =
            "Roblox One could not read the local settings or its backup. The unreadable files were preserved, and browser profiles were left untouched.";
        AddProfileCleanupNotice();
        return new();
    }

    public void Save(AppSettings settings)
    {
        if (_primaryIsUnreadable)
        {
            if (!PreserveUnreadableFile(_settingsPath))
            {
                throw new IOException(
                    "The unreadable settings file is still in use and was not overwritten.");
            }
            _primaryIsUnreadable = false;
        }

        var temporaryPath = _settingsPath + ".tmp";
        try
        {
            File.WriteAllText(
                temporaryPath,
                JsonSerializer.Serialize(settings, JsonOptions));
            if (File.Exists(_settingsPath))
            {
                File.Replace(
                    temporaryPath,
                    _settingsPath,
                    _backupPath,
                    ignoreMetadataErrors: true);
            }
            else
            {
                File.Move(temporaryPath, _settingsPath);
            }
        }
        finally
        {
            if (File.Exists(temporaryPath))
                File.Delete(temporaryPath);
        }
    }

    public int CleanupOrphanedSessionDirectories(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (!CanReconcileProfiles)
            return 0;

        var profilesDirectory = Path.Combine(_rootDirectory, "Profiles");
        if (!Directory.Exists(profilesDirectory))
            return 0;

        var referencedKeys = settings.Accounts
            .Select(account => account.Key)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var removed = 0;
        foreach (var directory in Directory.EnumerateDirectories(
                     profilesDirectory,
                     "*",
                     SearchOption.TopDirectoryOnly))
        {
            var key = Path.GetFileName(directory);
            if (!IsValidKey(key) || key == "legacy" || referencedKeys.Contains(key))
                continue;
            try
            {
                Directory.Delete(directory, recursive: true);
                removed++;
            }
            catch (Exception ex) when (
                ex is IOException or UnauthorizedAccessException)
            {
                // A WebView2 process can briefly retain an interrupted profile.
            }
        }
        return removed;
    }

    public async Task<bool> DeleteSessionDataAsync(
        AccountProfile profile,
        CancellationToken cancellationToken = default)
    {
        var path = GetSessionDataDirectory(profile);
        for (var attempt = 0; attempt < 10; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                if (!Directory.Exists(path))
                    return true;
                Directory.Delete(path, recursive: true);
                return true;
            }
            catch (Exception ex) when (
                ex is IOException or UnauthorizedAccessException)
            {
                if (attempt == 9)
                    return false;
                await Task.Delay(TimeSpan.FromMilliseconds(150), cancellationToken);
            }
        }
        return false;
    }

    private bool TryLoadFile(
        string path,
        out AppSettings settings,
        out bool migrated)
    {
        settings = new();
        migrated = false;
        try
        {
            if (!File.Exists(path))
                return false;
            settings = JsonSerializer.Deserialize<AppSettings>(
                File.ReadAllText(path)) ?? new();
            migrated = MigrateLegacyAccount(settings);
            Normalize(settings);
            migrated |= MigrateLegacyDestinations(settings);
            return true;
        }
        catch (Exception ex) when (
            ex is IOException or UnauthorizedAccessException or JsonException or
                NotSupportedException or ArgumentException)
        {
            settings = new();
            migrated = false;
            return false;
        }
    }

    private void WriteFresh(AppSettings settings)
    {
        var temporaryPath = _settingsPath + ".restore.tmp";
        try
        {
            File.WriteAllText(
                temporaryPath,
                JsonSerializer.Serialize(settings, JsonOptions));
            File.Move(temporaryPath, _settingsPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
                File.Delete(temporaryPath);
        }
    }

    private static bool PreserveUnreadableFile(string path)
    {
        if (!File.Exists(path))
            return true;
        try
        {
            var preservedPath = Path.Combine(
                Path.GetDirectoryName(path)!,
                $"{Path.GetFileNameWithoutExtension(path)}.corrupt-{DateTimeOffset.UtcNow:yyyyMMddHHmmss}-{Guid.NewGuid():N}.json");
            File.Move(path, preservedPath);
            return true;
        }
        catch (Exception ex) when (
            ex is IOException or UnauthorizedAccessException)
        {
            // Never destroy an unreadable settings file just to make recovery tidy.
            return false;
        }
    }

    private bool HasPreservedSettingsFiles()
    {
        try
        {
            return Directory.EnumerateFiles(
                    _rootDirectory,
                    "settings.corrupt-*.json",
                    SearchOption.TopDirectoryOnly)
                .Any() ||
                Directory.EnumerateFiles(
                    _rootDirectory,
                    "settings.backup.corrupt-*.json",
                    SearchOption.TopDirectoryOnly)
                .Any();
        }
        catch (Exception ex) when (
            ex is IOException or UnauthorizedAccessException)
        {
            // If the recovery state cannot be inspected, preserve profiles.
            return true;
        }
    }

    private void PauseProfileCleanup()
    {
        CanReconcileProfiles = false;
        try
        {
            File.WriteAllText(
                _profileCleanupGuardPath,
                "Automatic browser-profile cleanup is paused because settings could not be recovered. Keep this file until account metadata has been recovered or all old profiles may be deleted.");
        }
        catch (Exception ex) when (
            ex is IOException or UnauthorizedAccessException)
        {
            // The preserved corrupt files provide a second persistent guard.
        }
    }

    private void AddProfileCleanupNotice()
    {
        if (CanReconcileProfiles)
            return;

        const string notice =
            "Automatic browser-profile cleanup is paused to protect sessions whose account metadata could not be recovered.";
        LoadNotice = string.IsNullOrWhiteSpace(LoadNotice)
            ? notice
            : $"{LoadNotice}{Environment.NewLine}{Environment.NewLine}{notice}";
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
