namespace SessionDock.ReleaseTrust;

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

    private static readonly IReadOnlyDictionary<string, EntryLimits> LegacyEntries =
        CreateExpectedEntries(
            "RobloxOne.nuspec",
            "RobloxOne.exe",
            "RobloxOne_ExecutionStub.exe");
    private static readonly IReadOnlyDictionary<string, EntryLimits> CurrentEntries =
        CreateExpectedEntries(
            "SessionDockApp.nuspec",
            "SessionDock.exe",
            "SessionDock_ExecutionStub.exe");

    public static IReadOnlyList<string> ExecutableEntryNames =>
        GetExecutableEntryNames(useCurrentLayout: false);

    public static void ValidateEntries(
        IEnumerable<ReleasePackageEntryIdentity> entries,
        bool useCurrentLayout = false)
    {
        ArgumentNullException.ThrowIfNull(entries);
        var expectedEntries = useCurrentLayout ? CurrentEntries : LegacyEntries;
        var materialized = entries.ToArray();
        if (materialized.Length != expectedEntries.Count)
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
                !expectedEntries.TryGetValue(entry.FullName, out var limits))
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

    public static IReadOnlyList<string> GetExecutableEntryNames(
        bool useCurrentLayout) =>
        useCurrentLayout
            ? [
                "lib/app/SessionDock.exe",
                "lib/app/SessionDock_ExecutionStub.exe",
                "lib/app/Squirrel.exe"
            ]
            : [
                "lib/app/RobloxOne.exe",
                "lib/app/RobloxOne_ExecutionStub.exe",
                "lib/app/Squirrel.exe"
            ];

    private static Dictionary<string, EntryLimits> CreateExpectedEntries(
        string nuspecName,
        string mainExecutable,
        string executionStub)
    {
        return new Dictionary<string, EntryLimits>(StringComparer.Ordinal)
        {
            ["[Content_Types].xml"] = new(1, MaximumMetadataBytes),
            ["_rels/.rels"] = new(1, MaximumMetadataBytes),
            [nuspecName] = new(1, MaximumMetadataBytes),
            ["lib/app/LICENSE.md"] = new(1, MaximumNoticeBytes),
            [$"lib/app/{mainExecutable}"] = new(
                ReleaseDescriptorPolicy.MinimumPackageSize,
                ReleaseDescriptorPolicy.MaximumPackageSize),
            [$"lib/app/{executionStub}"] = new(
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
    }

    private sealed record EntryLimits(long Minimum, long Maximum);
}
