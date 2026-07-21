using System.IO;

namespace SessionDock.Services;

internal static class RobloxExecutableTrust
{
    public static bool IsTrustedPlayerPath(string path)
    {
        try
        {
            var fullPath = Path.GetFullPath(path);
            if (!Path.GetFileName(fullPath).Equals(
                    "RobloxPlayerBeta.exe",
                    StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            var localVersionsRoot = Path.GetFullPath(Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Roblox", "Versions")) + Path.DirectorySeparatorChar;
            var programFilesRoot = Path.GetFullPath(Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                "Roblox")) + Path.DirectorySeparatorChar;
            var trustedLocation =
                fullPath.StartsWith(localVersionsRoot, StringComparison.OrdinalIgnoreCase) ||
                fullPath.StartsWith(programFilesRoot, StringComparison.OrdinalIgnoreCase);
            return trustedLocation && HasValidRobloxSignature(fullPath);
        }
        catch
        {
            return false;
        }
    }

    private static bool HasValidRobloxSignature(string path)
    {
        try
        {
            return WindowsExecutableTrust.TryGetTrustedSigner(
                    Path.GetFullPath(path),
                    out var signer) &&
                signer.SimpleName.Equals(
                    "Roblox Corporation",
                    StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }
}
