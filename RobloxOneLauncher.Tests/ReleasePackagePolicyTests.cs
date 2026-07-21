using RobloxOne.ReleaseTrust;

namespace RobloxOneLauncher.Tests;

public sealed class ReleasePackagePolicyTests
{
    [Fact]
    public void ValidateEntries_ExactPackageAllowlist_IsAcceptedInAnyOrder()
    {
        var entries = CreateValidEntries();
        entries.Reverse();

        ReleasePackagePolicy.ValidateEntries(entries);
    }

    [Fact]
    public void ExecutableEntryNames_ContainsEveryExecutableThatMustBeVerified()
    {
        Assert.Equal(
            new[]
            {
                "lib/app/RobloxOne.exe",
                "lib/app/RobloxOne_ExecutionStub.exe",
                "lib/app/Squirrel.exe"
            },
            ReleasePackagePolicy.ExecutableEntryNames);
    }

    [Theory]
    [InlineData("lib/app/Untrusted.exe")]
    [InlineData("../[Content_Types].xml")]
    [InlineData("/[Content_Types].xml")]
    [InlineData("C:/[Content_Types].xml")]
    [InlineData("[content_types].xml")]
    [InlineData(@"lib\app\RobloxOne.exe")]
    [InlineData(" ")]
    [InlineData(null)]
    public void ValidateEntries_UnsafeOrUnexpectedName_IsRejected(string? name)
    {
        var entries = CreateValidEntries();
        entries[0] = entries[0] with { FullName = name };

        Assert.Throws<ReleaseTrustException>(() =>
            ReleasePackagePolicy.ValidateEntries(entries));
    }

    [Fact]
    public void ValidateEntries_ExtraEntry_IsRejected()
    {
        var entries = CreateValidEntries();
        entries.Add(new ReleasePackageEntryIdentity(
            "lib/app/Untrusted.exe",
            64 * 1024,
            1024));

        Assert.Throws<ReleaseTrustException>(() =>
            ReleasePackagePolicy.ValidateEntries(entries));
    }

    [Fact]
    public void ValidateEntries_MissingEntry_IsRejected()
    {
        var entries = CreateValidEntries();
        entries.RemoveAt(0);

        Assert.Throws<ReleaseTrustException>(() =>
            ReleasePackagePolicy.ValidateEntries(entries));
    }

    [Fact]
    public void ValidateEntries_DuplicateEntry_IsRejected()
    {
        var entries = CreateValidEntries();
        entries[^1] = entries[^1] with { FullName = entries[0].FullName };

        Assert.Throws<ReleaseTrustException>(() =>
            ReleasePackagePolicy.ValidateEntries(entries));
    }

    [Fact]
    public void ValidateEntries_NullEntry_IsRejectedAsTrustFailure()
    {
        var entries = CreateValidEntries();
        entries[0] = null!;

        Assert.Throws<ReleaseTrustException>(() =>
            ReleasePackagePolicy.ValidateEntries(entries));
    }

    [Theory]
    [InlineData("[Content_Types].xml", 0)]
    [InlineData("lib/app/LICENSE.md", (2L * 1024 * 1024) + 1)]
    [InlineData("lib/app/RobloxOne.exe", (1024L * 1024) - 1)]
    [InlineData("lib/app/RobloxOne.exe", (1024L * 1024 * 1024) + 1)]
    [InlineData("lib/app/RobloxOne_ExecutionStub.exe", (128L * 1024 * 1024) + 1)]
    [InlineData("lib/app/Squirrel.exe", (256L * 1024 * 1024) + 1)]
    public void ValidateEntries_EntryOutsideItsUncompressedLimit_IsRejected(
        string name,
        long length)
    {
        var entries = CreateValidEntries();
        var index = entries.FindIndex(entry => entry.FullName == name);
        entries[index] = entries[index] with { Length = length };

        Assert.Throws<ReleaseTrustException>(() =>
            ReleasePackagePolicy.ValidateEntries(entries));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData((1024L * 1024 * 1024) + 1)]
    public void ValidateEntries_InvalidCompressedSize_IsRejected(long compressedLength)
    {
        var entries = CreateValidEntries();
        entries[0] = entries[0] with { CompressedLength = compressedLength };

        Assert.Throws<ReleaseTrustException>(() =>
            ReleasePackagePolicy.ValidateEntries(entries));
    }

    [Fact]
    public void ValidateEntries_AggregateExpansionOverLimit_IsRejected()
    {
        var entries = CreateValidEntries();
        SetLength(entries, "lib/app/RobloxOne.exe", 768L * 1024 * 1024);
        SetLength(
            entries,
            "lib/app/RobloxOne_ExecutionStub.exe",
            128L * 1024 * 1024);
        SetLength(entries, "lib/app/Squirrel.exe", 256L * 1024 * 1024);

        Assert.Throws<ReleaseTrustException>(() =>
            ReleasePackagePolicy.ValidateEntries(entries));
    }

    private static List<ReleasePackageEntryIdentity> CreateValidEntries() =>
    [
        Entry("[Content_Types].xml"),
        Entry("_rels/.rels"),
        Entry("RobloxOne.nuspec"),
        Entry("lib/app/LICENSE.md"),
        Entry(
            "lib/app/RobloxOne.exe",
            ReleaseDescriptorPolicy.MinimumPackageSize),
        Entry("lib/app/RobloxOne_ExecutionStub.exe", 64 * 1024),
        Entry("lib/app/Squirrel.exe", 64 * 1024),
        Entry("lib/app/sq.version"),
        Entry("lib/app/THIRD_PARTY_NOTICES.md"),
        Entry("lib/app/licenses/DotNet-LICENSE.txt"),
        Entry("lib/app/licenses/DotNet-THIRD-PARTY-NOTICES.txt"),
        Entry("lib/app/licenses/Microsoft.Web.WebView2-LICENSE.txt"),
        Entry("lib/app/licenses/Microsoft.Web.WebView2-NOTICE.txt"),
        Entry("lib/app/licenses/Microsoft.WindowsDesktop-LICENSE.txt"),
        Entry("lib/app/licenses/Velopack-LICENSE.txt")
    ];

    private static ReleasePackageEntryIdentity Entry(
        string fullName,
        long length = 1) =>
        new(fullName, length, 1);

    private static void SetLength(
        List<ReleasePackageEntryIdentity> entries,
        string name,
        long length)
    {
        var index = entries.FindIndex(entry => entry.FullName == name);
        entries[index] = entries[index] with { Length = length };
    }
}
