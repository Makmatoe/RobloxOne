using System.IO;

namespace SessionDock.Services;

internal static class RobloxExecutableTrust
{
    public static bool IsTrustedPlayerPath(
        string path,
        bool forceRefresh = false)
    {
        var localVersionsRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Roblox", "Versions");
        var programFilesRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            "Roblox");
        return IsTrustedPlayerPath(
            path,
            localVersionsRoot,
            programFilesRoot,
            forceRefresh,
            WindowsExecutableTrust.TryGetTrustedSigner);
    }

    internal static bool IsTrustedPlayerPath(
        string path,
        string localVersionsRoot,
        string programFilesRoot,
        bool forceRefresh,
        TryGetWindowsSigner tryGetSigner)
    {
        ArgumentNullException.ThrowIfNull(tryGetSigner);
        try
        {
            var fullPath = Path.GetFullPath(path);
            if (!Path.GetFileName(fullPath).Equals(
                    "RobloxPlayerBeta.exe",
                    StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            localVersionsRoot = Path.GetFullPath(localVersionsRoot) +
                Path.DirectorySeparatorChar;
            programFilesRoot = Path.GetFullPath(programFilesRoot) +
                Path.DirectorySeparatorChar;
            var trustedLocation =
                fullPath.StartsWith(localVersionsRoot, StringComparison.OrdinalIgnoreCase) ||
                fullPath.StartsWith(programFilesRoot, StringComparison.OrdinalIgnoreCase);
            return trustedLocation && HasValidRobloxSignature(
                fullPath,
                forceRefresh,
                tryGetSigner);
        }
        catch
        {
            return false;
        }
    }

    private static bool HasValidRobloxSignature(
        string path,
        bool forceRefresh,
        TryGetWindowsSigner tryGetSigner)
    {
        try
        {
            return tryGetSigner(
                    Path.GetFullPath(path),
                    out var signer,
                    forceRefresh) &&
                signer.SimpleName.Equals(
                    "Roblox Corporation",
                    StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    internal delegate bool TryGetWindowsSigner(
        string path,
        out TrustedWindowsSigner signer,
        bool forceRefresh);
}
