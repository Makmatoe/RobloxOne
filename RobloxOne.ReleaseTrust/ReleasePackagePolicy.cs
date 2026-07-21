namespace RobloxOne.ReleaseTrust;

public sealed record ReleasePackageEntryIdentity(
    string? FullName,
    long Length,
    long CompressedLength);

public static class ReleasePackagePolicy
{
    public const long MaximumUncompressedBytes = 1024L * 1024 * 1024;

    private const long MaximumMetadataBytes = 256 * 1024;
    private const long MaximumNoticeBytes = 2 * 1024 * 1024;
    private const long MinimumExecutableBytes = 64 * 1024;

    private static readonly Dictionary<string, EntryLimits> ExpectedEntries =
        new Dictionary<string, EntryLimits>(StringComparer.Ordinal)
        {
            ["[Content_Types].xml"] = new(1, MaximumMetadataBytes),
            ["_rels/.rels"] = new(1, MaximumMetadataBytes),
            ["RobloxOne.nuspec"] = new(1, MaximumMetadataBytes),
            ["lib/app/LICENSE.md"] = new(1, MaximumNoticeBytes),
            ["lib/app/RobloxOne.exe"] = new(
                ReleaseDescriptorPolicy.MinimumPackageSize,
                ReleaseDescriptorPolicy.MaximumPackageSize),
            ["lib/app/RobloxOne_ExecutionStub.exe"] = new(
                MinimumExecutableBytes,
                128 * 1024 * 1024),
            ["lib/app/Squirrel.exe"] = new(
                MinimumExecutableBytes,
                256 * 1024 * 1024),
            ["lib/app/sq.version"] = new(1, MaximumMetadataBytes),
            ["lib/app/THIRD_PARTY_NOTICES.md"] = new(1, MaximumNoticeBytes),
            ["lib/app/licenses/DotNet-LICENSE.txt"] = new(1, MaximumNoticeBytes),
            ["lib/app/licenses/DotNet-THIRD-PARTY-NOTICES.txt"] = new(
                1,
                MaximumNoticeBytes),
            ["lib/app/licenses/Microsoft.Web.WebView2-LICENSE.txt"] = new(
                1,
                MaximumNoticeBytes),
            ["lib/app/licenses/Microsoft.Web.WebView2-NOTICE.txt"] = new(
                1,
                MaximumNoticeBytes),
            ["lib/app/licenses/Microsoft.WindowsDesktop-LICENSE.txt"] = new(
                1,
                MaximumNoticeBytes),
            ["lib/app/licenses/Velopack-LICENSE.txt"] = new(1, MaximumNoticeBytes)
        };

    private static readonly IReadOnlyList<string> Executables = Array.AsReadOnly(
        new[]
        {
            "lib/app/RobloxOne.exe",
            "lib/app/RobloxOne_ExecutionStub.exe",
            "lib/app/Squirrel.exe"
        });

    public static IReadOnlyList<string> ExecutableEntryNames => Executables;

    public static void ValidateEntries(
        IEnumerable<ReleasePackageEntryIdentity> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        var materialized = entries.ToArray();
        if (materialized.Length != ExpectedEntries.Count)
        {
            throw new ReleaseTrustException(
                "The update package contains missing or unexpected entries.");
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        long totalLength = 0;
        foreach (var entry in materialized)
        {
            if (entry is null ||
                string.IsNullOrWhiteSpace(entry.FullName) ||
                !seen.Add(entry.FullName) ||
                !ExpectedEntries.TryGetValue(entry.FullName, out var limits))
            {
                throw new ReleaseTrustException(
                    "The update package contains a duplicate, unsafe, or unexpected entry.");
            }

            if (entry.Length < limits.Minimum ||
                entry.Length > limits.Maximum ||
                entry.CompressedLength <= 0 ||
                entry.CompressedLength > ReleaseDescriptorPolicy.MaximumPackageSize)
            {
                throw new ReleaseTrustException(
                    $"The update package entry '{entry.FullName}' has an invalid size.");
            }

            try
            {
                totalLength = checked(totalLength + entry.Length);
            }
            catch (OverflowException exception)
            {
                throw new ReleaseTrustException(
                    "The update package expands beyond the permitted size.",
                    exception);
            }
        }

        if (totalLength > MaximumUncompressedBytes)
        {
            throw new ReleaseTrustException(
                "The update package expands beyond the permitted size.");
        }
    }

    private sealed record EntryLimits(long Minimum, long Maximum);
}
