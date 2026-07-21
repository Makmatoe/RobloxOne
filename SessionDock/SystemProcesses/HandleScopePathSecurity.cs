using System.IO;

namespace SessionDock.SystemProcesses;

internal static class HandleScopePathSecurity
{
    internal static bool IsSafeExistingPath(
        string basePath,
        string targetPath,
        bool targetMustExist,
        Func<string, bool>? isReparsePoint = null)
    {
        try
        {
            var root = Path.TrimEndingDirectorySeparator(
                Path.GetFullPath(basePath));
            var target = Path.TrimEndingDirectorySeparator(
                Path.GetFullPath(targetPath));
            if (!target.Equals(root, StringComparison.OrdinalIgnoreCase) &&
                !target.StartsWith(
                    root + Path.DirectorySeparatorChar,
                    StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            isReparsePoint ??= static path =>
                (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;

            var current = root;
            if (!TryValidateExistingItem(
                    current,
                    isReparsePoint,
                    out var exists))
            {
                return false;
            }
            if (!exists)
                return false;

            var relative = Path.GetRelativePath(root, target);
            if (relative == ".")
                return true;

            foreach (var segment in relative.Split(
                         Path.DirectorySeparatorChar,
                         StringSplitOptions.RemoveEmptyEntries))
            {
                current = Path.Combine(current, segment);
                if (!TryValidateExistingItem(
                        current,
                        isReparsePoint,
                        out exists))
                {
                    return false;
                }
                if (!exists)
                {
                    return !targetMustExist;
                }
            }

            return true;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or
                ArgumentException or NotSupportedException)
        {
            return false;
        }
    }

    private static bool TryValidateExistingItem(
        string path,
        Func<string, bool> isReparsePoint,
        out bool exists)
    {
        try
        {
            _ = File.GetAttributes(path);
            exists = true;
            return !isReparsePoint(path);
        }
        catch (Exception exception) when (
            exception is FileNotFoundException or DirectoryNotFoundException)
        {
            exists = false;
            return true;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or
                ArgumentException or NotSupportedException)
        {
            exists = false;
            return false;
        }
    }
}
