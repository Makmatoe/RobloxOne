using System.IO;

namespace SessionDock.Services;

internal static class AppDataPaths
{
    internal const string CurrentDirectoryName = "SessionDock";
    internal const string LegacyDirectoryName = "RobloxOne";
    internal const string MigrationConflictFileName = "migration-conflict.txt";
    internal const string MigrationInProgressFileName = "migration-in-progress.txt";
    private static readonly HashSet<string> ActiveMigrationConflicts =
        new(StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> ActiveIncompleteMigrations =
        new(StringComparer.OrdinalIgnoreCase);
    private static readonly object MigrationConflictLock = new();

    private static readonly Lazy<string> DefaultRoot = new(() =>
    {
        var localAppData = Environment.GetFolderPath(
            Environment.SpecialFolder.LocalApplicationData);
        return ResolveForDirectories(
            Path.Combine(localAppData, CurrentDirectoryName),
            Path.Combine(localAppData, LegacyDirectoryName));
    });

    public static string RootDirectory => DefaultRoot.Value;

    internal static bool HasMigrationConflict(string directory)
    {
        var root = Path.GetFullPath(directory);
        lock (MigrationConflictLock)
        {
            if (ActiveMigrationConflicts.Contains(root))
                return true;
        }

        var conflictPath = Path.Combine(root, MigrationConflictFileName);
        return File.Exists(conflictPath) || Directory.Exists(conflictPath);
    }

    internal static bool HasIncompleteMigration(string directory)
    {
        var root = Path.GetFullPath(directory);
        lock (MigrationConflictLock)
        {
            if (ActiveIncompleteMigrations.Contains(root))
                return true;
        }

        var markerPath = Path.Combine(root, MigrationInProgressFileName);
        return File.Exists(markerPath) || Directory.Exists(markerPath);
    }

    internal static string ResolveForDirectories(
        string preferredDirectory,
        string legacyDirectory)
    {
        var preferred = Path.GetFullPath(preferredDirectory);
        var legacy = Path.GetFullPath(legacyDirectory);
        if (preferred.Equals(legacy, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("The current and legacy data paths must differ.");

        if (Directory.Exists(preferred))
        {
            ThrowIfReparsePoint(preferred);
            if (IsSafeLegacyDirectory(legacy))
            {
                try
                {
                    if (!Directory.EnumerateFileSystemEntries(preferred).Any())
                    {
                        Directory.Delete(preferred, recursive: false);
                        Directory.Move(legacy, preferred);
                    }
                    else if (HasIncompleteMigration(preferred))
                    {
                        // A prior merge stopped after it may have moved data.
                        // Preserve both roots for explicit recovery.
                    }
                    else if (HasSettingsState(preferred) &&
                             HasSettingsState(legacy))
                    {
                        PreserveMigrationConflict(preferred);
                    }
                    else if (TryBeginMigration(preferred))
                    {
                        MergeWithoutOverwrite(legacy, preferred);
                        CompleteMigration(preferred, legacy);
                    }
                }
                catch (Exception exception) when (
                    exception is IOException or UnauthorizedAccessException)
                {
                    if (!Directory.Exists(preferred))
                        return legacy;
                }
            }

            return preferred;
        }

        if (IsSafeLegacyDirectory(legacy))
        {
            try
            {
                Directory.Move(legacy, preferred);
                return preferred;
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException)
            {
                if (!Directory.Exists(preferred))
                    return legacy;
            }
        }

        Directory.CreateDirectory(preferred);
        ThrowIfReparsePoint(preferred);
        return preferred;
    }

    private static bool HasSettingsState(string directory) =>
        File.Exists(Path.Combine(directory, "settings.json")) ||
        File.Exists(Path.Combine(directory, "settings.backup.json"));

    private static bool TryBeginMigration(string preferredDirectory)
    {
        lock (MigrationConflictLock)
            ActiveIncompleteMigrations.Add(preferredDirectory);

        var markerPath = Path.Combine(
            preferredDirectory,
            MigrationInProgressFileName);
        try
        {
            using var stream = new FileStream(
                markerPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.Read);
            using var writer = new StreamWriter(stream);
            writer.Write(
                "SessionDock began moving legacy RobloxOne data, but the migration did not finish cleanly. Some files may already be in the SessionDock directory. Automatic browser-profile cleanup is paused. Reconcile both data directories before removing this file.");
            writer.Flush();
            stream.Flush(flushToDisk: true);
            return true;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            // Without a durable guard, moving any legacy data is unsafe.
            return false;
        }
    }

    private static void CompleteMigration(
        string preferredDirectory,
        string legacyDirectory)
    {
        if (Directory.Exists(legacyDirectory))
            return;

        var markerPath = Path.Combine(
            preferredDirectory,
            MigrationInProgressFileName);
        try
        {
            File.Delete(markerPath);
            if (File.Exists(markerPath) || Directory.Exists(markerPath))
                return;

            lock (MigrationConflictLock)
                ActiveIncompleteMigrations.Remove(preferredDirectory);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            // A stale guard pauses cleanup safely until explicit recovery.
        }
    }

    private static void PreserveMigrationConflict(string preferredDirectory)
    {
        lock (MigrationConflictLock)
        {
            ActiveMigrationConflicts.Add(preferredDirectory);
        }

        var conflictPath = Path.Combine(
            preferredDirectory,
            MigrationConflictFileName);
        try
        {
            using var stream = new FileStream(
                conflictPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.Read);
            using var writer = new StreamWriter(stream);
            writer.Write(
                "SessionDock found independent settings in both the current SessionDock data directory and the legacy RobloxOne data directory. No legacy files were moved. Automatic browser-profile cleanup is paused until the legacy data has been recovered or intentionally removed. Remove this file only after resolving the legacy data directory.");
            writer.Flush();
            stream.Flush(flushToDisk: true);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            // The untouched legacy directory remains the authoritative fallback.
        }
    }

    private static void MergeWithoutOverwrite(string source, string destination)
    {
        ThrowIfReparsePoint(source);
        ThrowIfReparsePoint(destination);
        var entries = Directory.EnumerateFileSystemEntries(source)
            .OrderBy(path => Path.GetFileName(path).Equals(
                "settings.json",
                StringComparison.OrdinalIgnoreCase) ? 1 : 0)
            .ToArray();
        foreach (var sourceEntry in entries)
        {
            if ((File.GetAttributes(sourceEntry) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Legacy application data contains a reparse point.");

            var destinationEntry = Path.Combine(
                destination,
                Path.GetFileName(sourceEntry));
            if (Directory.Exists(sourceEntry))
            {
                if (!Directory.Exists(destinationEntry) &&
                    !File.Exists(destinationEntry))
                {
                    Directory.Move(sourceEntry, destinationEntry);
                }
                else if (Directory.Exists(destinationEntry))
                {
                    MergeWithoutOverwrite(sourceEntry, destinationEntry);
                }

                continue;
            }

            if (!File.Exists(destinationEntry) &&
                !Directory.Exists(destinationEntry))
            {
                File.Move(sourceEntry, destinationEntry);
            }
        }

        if (!Directory.EnumerateFileSystemEntries(source).Any())
            Directory.Delete(source, recursive: false);
    }

    private static bool IsSafeLegacyDirectory(string path)
    {
        if (!Directory.Exists(path))
            return false;
        ThrowIfReparsePoint(path);
        return true;
    }

    private static void ThrowIfReparsePoint(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("The application data directory cannot be a reparse point.");
    }
}
