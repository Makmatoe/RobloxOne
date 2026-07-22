using System.IO;
using System.Text.Json;
using SessionDock.Models;

namespace SessionDock.Services;

public sealed class SettingsService
{
    private enum PathProbeResult
    {
        Missing,
        File,
        Directory,
        Uncertain
    }

    private const int MaximumSettingsBytes = 4 * 1024 * 1024;
    public static readonly IReadOnlyList<string> AccountColors =
        ["#7C5CFC", "#4D8DFF", "#27B58A", "#E0A33A", "#E36B8D", "#A56DE2"];
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly string _rootDirectory;
    private readonly string _backupPath;
    private readonly string _profileCleanupGuardPath;
    private readonly string _settingsPath;
    private readonly object _saveLock = new();
    private readonly Func<string, FileAttributes> _getAttributes;
    private bool _primaryIsUnreadable;
    private bool _profileCleanupGuardRequiredButUnavailable;

    public SettingsService(string? storageDirectory = null)
        : this(storageDirectory, File.GetAttributes)
    {
    }

    internal SettingsService(
        string? storageDirectory,
        Func<string, FileAttributes> getAttributes)
    {
        _getAttributes = getAttributes ??
            throw new ArgumentNullException(nameof(getAttributes));
        _rootDirectory = Path.GetFullPath(
            storageDirectory ?? AppDataPaths.RootDirectory);
        Directory.CreateDirectory(_rootDirectory);
        ThrowIfReparsePoint(_rootDirectory);
        _settingsPath = Path.Combine(_rootDirectory, "settings.json");
        _backupPath = Path.Combine(_rootDirectory, "settings.backup.json");
        _profileCleanupGuardPath = Path.Combine(
            _rootDirectory,
            "profile-cleanup-paused.txt");
        RefreshProfileCleanupState();
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
            throw new InvalidOperationException("The account session path is outside SessionDock.");
        }
        EnsureDirectoryPathHasNoReparsePoints(path);
        return path;
    }

    public AppSettings Load()
    {
        LoadNotice = null;
        RefreshProfileCleanupState();
        _primaryIsUnreadable = false;
        var primaryState = ProbePath(_settingsPath);
        var backupState = ProbePath(_backupPath);
        var primaryExists = primaryState != PathProbeResult.Missing;
        if (primaryState == PathProbeResult.Missing &&
            backupState == PathProbeResult.Missing)
        {
            if (HasPreservedSettingsFiles() || HasPotentialAccountProfiles())
            {
                RequireProfileCleanupPause();
            }
            AddProfileCleanupNotice();
            return new();
        }

        if (primaryState != PathProbeResult.Missing &&
            TryLoadFile(
                _settingsPath,
                out var settings,
                out var migrated,
                out var accountMetadataWasDiscarded))
        {
            var cleanupPauseIsDurable =
                !accountMetadataWasDiscarded || RequireProfileCleanupPause();
            if (migrated && cleanupPauseIsDurable)
            {
                try
                {
                    Save(settings);
                }
                catch (Exception exception) when (
                    LocalDataException.IsExpectedPersistenceFailure(exception))
                {
                    // Keep the successfully loaded settings in memory for this run.
                    System.Diagnostics.Trace.WriteLine(
                        $"Migrated settings could not be persisted: {exception.GetType().Name}.");
                }
            }
            AddProfileCleanupNotice();
            return settings;
        }

        if (backupState != PathProbeResult.Missing &&
            TryLoadFile(
                _backupPath,
                out settings,
                out migrated,
                out _))
        {
            // The backup is intentionally the prior successful revision. A
            // newer account can therefore be absent even when it validates.
            var cleanupPauseIsDurable = RequireProfileCleanupPause();
            _primaryIsUnreadable = true;
            if (cleanupPauseIsDurable &&
                PreserveUnreadableFile(_settingsPath))
            {
                _primaryIsUnreadable = false;
                try
                {
                    WriteFresh(settings);
                    if (migrated)
                        Save(settings);
                }
                catch (Exception exception) when (
                    LocalDataException.IsExpectedPersistenceFailure(exception))
                {
                    // The validated backup remains available if restoration is blocked.
                    System.Diagnostics.Trace.WriteLine(
                        $"Recovered settings could not be restored: {exception.GetType().Name}.");
                }
            }
            LoadNotice = primaryExists
                ? "SessionDock recovered your accounts and history from the local settings backup. The unreadable file was preserved for diagnosis."
                : "SessionDock recovered your accounts and history from the local settings backup after the primary file was missing.";
            AddProfileCleanupNotice();
            return settings;
        }

        _primaryIsUnreadable = !PreserveUnreadableFile(_settingsPath);
        PreserveUnreadableFile(_backupPath);
        RequireProfileCleanupPause();
        LoadNotice =
            "SessionDock could not read the local settings or its backup. The unreadable files were preserved, and browser profiles were left untouched.";
        AddProfileCleanupNotice();
        return new();
    }

    public void Save(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        lock (_saveLock)
        {
            if (_profileCleanupGuardRequiredButUnavailable &&
                !RequireProfileCleanupPause())
            {
                throw new IOException(
                    "Profile cleanup could not be paused durably, so settings were not overwritten.");
            }
            _profileCleanupGuardRequiredButUnavailable = false;

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
                var primaryExists = ValidateFilePathAndCheckExists(
                    _settingsPath);
                _ = ValidateFilePathAndCheckExists(_backupPath);
                if (primaryExists)
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
                DeleteTemporaryFileBestEffort(temporaryPath);
            }
        }
    }

    public int CleanupOrphanedSessionDirectories(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (!CanReconcileProfiles)
            return 0;

        var removed = 0;
        try
        {
            var profilesDirectory = Path.Combine(_rootDirectory, "Profiles");
            FileAttributes profileAttributes;
            try
            {
                profileAttributes = File.GetAttributes(profilesDirectory);
            }
            catch (Exception exception) when (
                exception is FileNotFoundException or
                    DirectoryNotFoundException)
            {
                return 0;
            }
            if ((profileAttributes & FileAttributes.Directory) == 0 ||
                (profileAttributes & FileAttributes.ReparsePoint) != 0)
            {
                RequireProfileCleanupPause();
                AddProfileCleanupNotice();
                return 0;
            }

            var referencedKeys = settings.Accounts
                .Select(account => account.Key)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var directory in Directory.EnumerateDirectories(
                         profilesDirectory,
                         "*",
                         SearchOption.TopDirectoryOnly))
            {
                var key = Path.GetFileName(directory);
                if (!IsValidKey(key) || key == "legacy" ||
                    referencedKeys.Contains(key) || IsReparsePoint(directory))
                {
                    continue;
                }
                try
                {
                    if (TryDeleteDirectoryTree(
                            directory,
                            CancellationToken.None))
                        removed++;
                }
                catch (Exception exception) when (
                    LocalDataException.IsExpectedPersistenceFailure(exception))
                {
                    // A WebView2 process can briefly retain an interrupted profile.
                }
            }
        }
        catch (Exception exception) when (
            LocalDataException.IsExpectedPersistenceFailure(exception))
        {
            RequireProfileCleanupPause();
            AddProfileCleanupNotice();
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
                var pathState = ProbePath(path);
                if (pathState == PathProbeResult.Missing)
                    return true;
                if (pathState != PathProbeResult.Directory)
                {
                    if (pathState == PathProbeResult.Uncertain)
                        throw new IOException("The browser profile could not be inspected.");
                    return false;
                }
                return TryDeleteDirectoryTree(path, cancellationToken);
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
        out bool migrated,
        out bool accountMetadataWasDiscarded)
    {
        settings = new();
        migrated = false;
        accountMetadataWasDiscarded = false;
        try
        {
            if (ProbePath(path) == PathProbeResult.Missing)
                return false;
            var json = ReadBoundedFile(path, MaximumSettingsBytes);
            using var document = JsonDocument.Parse(json);
            var hasAccountCollection =
                document.RootElement.ValueKind == JsonValueKind.Object &&
                document.RootElement.TryGetProperty(
                    nameof(AppSettings.Accounts),
                    out var accountsElement) &&
                accountsElement.ValueKind == JsonValueKind.Array;
            settings = JsonSerializer.Deserialize<AppSettings>(
                json) ?? new();
            var hadMigratableLegacyAccount =
                settings.LockedUserId is > 0 &&
                !string.IsNullOrWhiteSpace(settings.LockedUsername);
            migrated = MigrateLegacyAccount(settings);
            accountMetadataWasDiscarded = Normalize(settings) ||
                !hasAccountCollection && !hadMigratableLegacyAccount;
            migrated |= MigrateLegacyDestinations(settings);
            return true;
        }
        catch (Exception ex) when (
            ex is IOException or UnauthorizedAccessException or JsonException or
                NotSupportedException or ArgumentException or InvalidDataException)
        {
            settings = new();
            migrated = false;
            accountMetadataWasDiscarded = false;
            return false;
        }
    }

    private void WriteFresh(AppSettings settings)
    {
        var temporaryPath = CreateTemporaryPath("restore");
        try
        {
            WriteSettingsFile(temporaryPath, settings);
            _ = ValidateFilePathAndCheckExists(_settingsPath);
            File.Move(temporaryPath, _settingsPath, overwrite: true);
        }
        finally
        {
            DeleteTemporaryFileBestEffort(temporaryPath);
        }
    }

    private static void DeleteTemporaryFileBestEffort(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception exception) when (
            LocalDataException.IsExpectedPersistenceFailure(exception))
        {
            System.Diagnostics.Trace.WriteLine(
                $"Temporary settings cleanup failed: {exception.GetType().Name}.");
        }
    }

    private bool PreserveUnreadableFile(string path)
    {
        var pathState = ProbePath(path);
        if (pathState == PathProbeResult.Missing)
            return true;
        if (pathState != PathProbeResult.File)
            return false;
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

    private bool HasPotentialAccountProfiles()
    {
        var profilesDirectory = Path.Combine(_rootDirectory, "Profiles");
        var pathState = ProbePath(profilesDirectory);
        if (pathState == PathProbeResult.Missing)
            return false;
        if (pathState != PathProbeResult.Directory)
            return true;

        try
        {
            return Directory.EnumerateDirectories(
                    profilesDirectory,
                    "*",
                    SearchOption.TopDirectoryOnly)
                .Any(path => IsValidKey(Path.GetFileName(path)));
        }
        catch (Exception exception) when (
            LocalDataException.IsExpectedPersistenceFailure(exception))
        {
            return true;
        }
    }

    private bool RequireProfileCleanupPause()
    {
        var durable = PauseProfileCleanup();
        _profileCleanupGuardRequiredButUnavailable = !durable;
        return durable;
    }

    private bool PauseProfileCleanup()
    {
        CanReconcileProfiles = false;
        if (HasKnownProfileCleanupBlocker())
            return true;

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
            writer.Flush();
            stream.Flush(flushToDisk: true);
            return true;
        }
        catch (Exception ex) when (
            ex is IOException or UnauthorizedAccessException)
        {
            return HasKnownProfileCleanupBlocker();
        }
    }

    private void RefreshProfileCleanupState()
    {
        var blockerState = InspectProfileCleanupBlockers();
        CanReconcileProfiles = !blockerState.HasPossibleBlocker;
        _profileCleanupGuardRequiredButUnavailable =
            blockerState.HasUncertainBlocker && !blockerState.HasKnownBlocker;
    }

    private bool HasKnownProfileCleanupBlocker() =>
        InspectProfileCleanupBlockers().HasKnownBlocker;

    private (bool HasPossibleBlocker, bool HasKnownBlocker, bool HasUncertainBlocker)
        InspectProfileCleanupBlockers()
    {
        var guardState = ProbePath(_profileCleanupGuardPath);
        var conflictState = ProbePath(Path.Combine(
            _rootDirectory,
            AppDataPaths.MigrationConflictFileName));
        var migrationState = ProbePath(Path.Combine(
            _rootDirectory,
            AppDataPaths.MigrationInProgressFileName));
        var hasKnownBlocker =
            guardState is PathProbeResult.File or PathProbeResult.Directory ||
            conflictState is PathProbeResult.File or PathProbeResult.Directory ||
            migrationState is PathProbeResult.File or PathProbeResult.Directory ||
            AppDataPaths.HasMigrationConflict(_rootDirectory) ||
            AppDataPaths.HasIncompleteMigration(_rootDirectory);
        var hasUncertainBlocker =
            guardState == PathProbeResult.Uncertain ||
            conflictState == PathProbeResult.Uncertain ||
            migrationState == PathProbeResult.Uncertain;
        return (
            hasKnownBlocker || hasUncertainBlocker,
            hasKnownBlocker,
            hasUncertainBlocker);
    }

    private void AddProfileCleanupNotice()
    {
        if (CanReconcileProfiles)
            return;

        var notice = ProbePath(Path.Combine(
                         _rootDirectory,
                         AppDataPaths.MigrationConflictFileName)) !=
                     PathProbeResult.Missing ||
                     AppDataPaths.HasMigrationConflict(_rootDirectory)
            ? "SessionDock found separate current and legacy RobloxOne account settings. The legacy data was left untouched, and automatic browser-profile cleanup is paused. Recover or intentionally remove the legacy RobloxOne data before deleting migration-conflict.txt."
            : ProbePath(Path.Combine(
                    _rootDirectory,
                    AppDataPaths.MigrationInProgressFileName)) !=
                PathProbeResult.Missing ||
                AppDataPaths.HasIncompleteMigration(_rootDirectory)
                ? "A legacy RobloxOne data migration did not finish cleanly. Some files may exist in either data directory, so automatic browser-profile cleanup is paused. Reconcile both directories before deleting migration-in-progress.txt."
                : "Automatic browser-profile cleanup is paused to protect sessions whose account metadata could not be recovered.";
        LoadNotice = string.IsNullOrWhiteSpace(LoadNotice)
            ? notice
            : $"{LoadNotice}{Environment.NewLine}{Environment.NewLine}{notice}";
    }

    private static bool Normalize(AppSettings settings)
    {
        settings.Accounts ??= [];
        settings.RecentExperiences ??= [];
        var originalAccountCount = settings.Accounts.Count;
        var validAccounts = settings.Accounts
            .Where(IsValidAccount)
            .Select(NormalizeAccountMetadata)
            .ToList();
        settings.Accounts = validAccounts
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

        return validAccounts.Count != originalAccountCount ||
            settings.Accounts.Count != validAccounts.Count;
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

    private byte[] ReadBoundedFile(string path, int maximumBytes)
    {
        if (!ValidateFilePathAndCheckExists(path))
            throw new FileNotFoundException("The settings file is missing.", path);
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

    private static bool TryDeleteDirectoryTree(
        string root,
        CancellationToken cancellationToken)
    {
        if (!Directory.Exists(root) || IsReparsePoint(root))
            return false;

        var pending = new Stack<(string Path, bool ChildrenVisited)>();
        pending.Push((root, false));
        while (pending.TryPop(out var item))
        {
            cancellationToken.ThrowIfCancellationRequested();
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
                cancellationToken.ThrowIfCancellationRequested();
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

    private bool ValidateFilePathAndCheckExists(string path)
    {
        FileAttributes attributes;
        try
        {
            attributes = _getAttributes(path);
        }
        catch (Exception exception) when (
            exception is FileNotFoundException or DirectoryNotFoundException)
        {
            return false;
        }

        if ((attributes & FileAttributes.Directory) != 0)
            throw new IOException("An app data file path cannot be a directory.");
        if ((attributes & FileAttributes.ReparsePoint) != 0)
            throw new IOException("An app data file cannot be a reparse point.");
        return true;
    }

    private PathProbeResult ProbePath(string path)
    {
        try
        {
            var attributes = _getAttributes(path);
            return (attributes & FileAttributes.Directory) != 0
                ? PathProbeResult.Directory
                : PathProbeResult.File;
        }
        catch (Exception exception) when (
            exception is FileNotFoundException or DirectoryNotFoundException)
        {
            return PathProbeResult.Missing;
        }
        catch (Exception exception) when (
            LocalDataException.IsExpectedPersistenceFailure(exception))
        {
            return PathProbeResult.Uncertain;
        }
    }

    private static void ThrowIfReparsePoint(string path)
    {
        if (IsReparsePoint(path))
            throw new IOException("An app data directory cannot be a reparse point.");
    }

    private static bool IsReparsePoint(string path) =>
        (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
}
