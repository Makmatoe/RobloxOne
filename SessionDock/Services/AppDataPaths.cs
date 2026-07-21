using System.IO;

namespace SessionDock.Services;

internal static class AppDataPaths
{
    internal const string CurrentDirectoryName = "SessionDock";
    internal const string LegacyDirectoryName = "RobloxOne";
    internal const string MigrationConflictFileName = "migration-conflict.txt";
    private static readonly HashSet<string> ActiveMigrationConflicts =
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
                    else if (HasSettingsState(preferred) &&
                             HasSettingsState(legacy))
                    {
                        PreserveMigrationConflict(preferred);
                    }
                    else
                    {
                        MergeWithoutOverwrite(legacy, preferred);
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
