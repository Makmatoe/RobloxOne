using System.IO;
using System.Text.Json;
using RobloxOneLauncher.Models;

namespace RobloxOneLauncher.Services;

public sealed class SettingsService
{
    private const int MaximumSettingsBytes = 4 * 1024 * 1024;
    public static readonly IReadOnlyList<string> AccountColors =
        ["#7C5CFC", "#4D8DFF", "#27B58A", "#E0A33A", "#E36B8D", "#A56DE2"];
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly string _rootDirectory;
    private readonly string _backupPath;
    private readonly string _profileCleanupGuardPath;
    private readonly string _settingsPath;
    private readonly object _saveLock = new();
    private bool _primaryIsUnreadable;

    public SettingsService(string? storageDirectory = null)
    {
        _rootDirectory = Path.GetFullPath(storageDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "RobloxOne"));
        Directory.CreateDirectory(_rootDirectory);
        ThrowIfReparsePoint(_rootDirectory);
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
        EnsureDirectoryPathHasNoReparsePoints(path);
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
        ArgumentNullException.ThrowIfNull(settings);
        lock (_saveLock)
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

            var temporaryPath = CreateTemporaryPath("save");
            try
            {
                WriteSettingsFile(temporaryPath, settings);
                ThrowIfFileIsReparsePoint(_settingsPath);
                ThrowIfFileIsReparsePoint(_backupPath);
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
    }

    public int CleanupOrphanedSessionDirectories(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (!CanReconcileProfiles)
            return 0;

        var profilesDirectory = Path.Combine(_rootDirectory, "Profiles");
        if (!Directory.Exists(profilesDirectory))
            return 0;
        if (IsReparsePoint(profilesDirectory))
        {
            PauseProfileCleanup();
            AddProfileCleanupNotice();
            return 0;
        }

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
            if (!IsValidKey(key) || key == "legacy" ||
                referencedKeys.Contains(key) || IsReparsePoint(directory))
                continue;
            try
            {
                if (TryDeleteDirectoryTree(directory))
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
        string path;
        try
        {
            path = GetSessionDataDirectory(profile);
        }
        catch (Exception ex) when (
            ex is IOException or UnauthorizedAccessException or
                InvalidOperationException)
        {
            return false;
        }

        for (var attempt = 0; attempt < 10; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                if (!Directory.Exists(path))
                    return true;
                return TryDeleteDirectoryTree(path);
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
                ReadBoundedFile(path, MaximumSettingsBytes)) ?? new();
            migrated = MigrateLegacyAccount(settings);
            Normalize(settings);
            migrated |= MigrateLegacyDestinations(settings);
            return true;
        }
        catch (Exception ex) when (
            ex is IOException or UnauthorizedAccessException or JsonException or
                NotSupportedException or ArgumentException or InvalidDataException)
        {
            settings = new();
            migrated = false;
            return false;
        }
    }

    private void WriteFresh(AppSettings settings)
    {
        var temporaryPath = CreateTemporaryPath("restore");
        try
        {
            WriteSettingsFile(temporaryPath, settings);
            ThrowIfFileIsReparsePoint(_settingsPath);
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
            using var stream = new FileStream(
                _profileCleanupGuardPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.Read);
            using var writer = new StreamWriter(stream);
            writer.Write(
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

    private string CreateTemporaryPath(string purpose) => Path.Combine(
        _rootDirectory,
        $"settings.{purpose}.{Guid.NewGuid():N}.tmp");

    private static void WriteSettingsFile(string path, AppSettings settings)
    {
        var contents = JsonSerializer.SerializeToUtf8Bytes(settings, JsonOptions);
        if (contents.Length > MaximumSettingsBytes)
            throw new InvalidDataException("The settings data is too large.");
        using var stream = new FileStream(
            path,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None);
        stream.Write(contents, 0, contents.Length);
        stream.Flush(flushToDisk: true);
    }

    private static byte[] ReadBoundedFile(string path, int maximumBytes)
    {
        ThrowIfFileIsReparsePoint(path);
        using var input = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read);
        using var output = new MemoryStream();
        var chunk = new byte[8192];
        while (true)
        {
            var read = input.Read(chunk, 0, chunk.Length);
            if (read == 0)
                return output.ToArray();
            if (output.Length + read > maximumBytes)
                throw new InvalidDataException("The settings file is too large.");
            output.Write(chunk, 0, read);
        }
    }

    private void EnsureDirectoryPathHasNoReparsePoints(string path)
    {
        ThrowIfReparsePoint(_rootDirectory);
        var relative = Path.GetRelativePath(_rootDirectory, path);
        var current = _rootDirectory;
        foreach (var component in relative.Split(
                     [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                     StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, component);
            if (Directory.Exists(current))
            {
                ThrowIfReparsePoint(current);
                continue;
            }

            Directory.CreateDirectory(current);
            ThrowIfReparsePoint(current);
        }
    }

    private static bool TryDeleteDirectoryTree(string root)
    {
        if (!Directory.Exists(root) || IsReparsePoint(root))
            return false;

        var pending = new Stack<(string Path, bool ChildrenVisited)>();
        pending.Push((root, false));
        while (pending.TryPop(out var item))
        {
            if (item.ChildrenVisited)
            {
                if (IsReparsePoint(item.Path))
                    return false;
                Directory.Delete(item.Path, recursive: false);
                continue;
            }

            if (IsReparsePoint(item.Path))
                return false;
            pending.Push((item.Path, true));
            foreach (var entry in Directory.EnumerateFileSystemEntries(
                         item.Path,
                         "*",
                         SearchOption.TopDirectoryOnly))
            {
                var attributes = File.GetAttributes(entry);
                if ((attributes & FileAttributes.Directory) != 0)
                {
                    if ((attributes & FileAttributes.ReparsePoint) != 0)
                        return false;
                    pending.Push((entry, false));
                }
                else
                {
                    File.Delete(entry);
                }
            }
        }

        return true;
    }

    private static void ThrowIfFileIsReparsePoint(string path)
    {
        if (File.Exists(path) && IsReparsePoint(path))
            throw new IOException("An app data file cannot be a reparse point.");
    }

    private static void ThrowIfReparsePoint(string path)
    {
        if (IsReparsePoint(path))
            throw new IOException("An app data directory cannot be a reparse point.");
    }

    private static bool IsReparsePoint(string path) =>
        (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
}
