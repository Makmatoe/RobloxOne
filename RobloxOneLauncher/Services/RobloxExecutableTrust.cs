using System.Collections.Concurrent;
using System.IO;

namespace RobloxOneLauncher.Services;

internal static class RobloxExecutableTrust
{
    private static readonly ConcurrentDictionary<string, SignatureCacheEntry>
        SignatureCache = new(StringComparer.OrdinalIgnoreCase);

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
            var file = new FileInfo(path);
            if (SignatureCache.TryGetValue(file.FullName, out var cached) &&
                cached.Length == file.Length &&
                cached.LastWriteTimeUtc == file.LastWriteTimeUtc)
            {
                return cached.IsTrusted;
            }

            var isTrusted =
                WindowsExecutableTrust.TryGetTrustedSigner(
                    file.FullName,
                    out var signer) &&
                signer.SimpleName.Equals(
                    "Roblox Corporation",
                    StringComparison.OrdinalIgnoreCase);
            SignatureCache[file.FullName] = new SignatureCacheEntry(
                file.Length,
                file.LastWriteTimeUtc,
                isTrusted);
            return isTrusted;
        }
        catch
        {
            return false;
        }
    }

    private sealed record SignatureCacheEntry(
        long Length,
        DateTime LastWriteTimeUtc,
        bool IsTrusted);
}
