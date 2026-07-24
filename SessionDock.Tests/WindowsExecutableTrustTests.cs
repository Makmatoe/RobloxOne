using SessionDock.Services;

namespace SessionDock.Tests;

public sealed class WindowsExecutableTrustTests
{
    private static readonly TrustedWindowsSigner RobloxSigner = new(
        "CN=Roblox Corporation, O=Roblox Corporation, C=US",
        "Roblox Corporation");

    [Fact]
    public void SuccessfulOnlineTrust_IsReturnedAndBoundedCached()
    {
        var native = new FakeNativeVerifier(
            new(WindowsTrustStatus.Trusted, RobloxSigner, 0));
        var now = new DateTimeOffset(2026, 7, 24, 12, 0, 0, TimeSpan.Zero);
        var verifier = new WindowsExecutableTrustVerifier(
            native,
            _ => Identity("AA"),
            () => now);

        Assert.True(verifier.TryGetTrustedSigner(
            @"C:\Roblox\RobloxPlayerBeta.exe",
            forceRefresh: false,
            out var first));
        Assert.True(verifier.TryGetTrustedSigner(
            @"C:\Roblox\RobloxPlayerBeta.exe",
            forceRefresh: false,
            out var cached));

        Assert.Equal(RobloxSigner, first);
        Assert.Equal(RobloxSigner, cached);
        Assert.Equal(1, native.Requests);
    }

    [Theory]
    [InlineData((int)WindowsTrustStatus.Revoked)]
    [InlineData((int)WindowsTrustStatus.RevocationOffline)]
    [InlineData((int)WindowsTrustStatus.RevocationUnknown)]
    [InlineData((int)WindowsTrustStatus.Untrusted)]
    public void NonSuccessfulTrustStatus_FailsClosedAndIsNotCached(
        int statusValue)
    {
        var status = (WindowsTrustStatus)statusValue;
        var native = new FakeNativeVerifier(
            new(status, TrustedWindowsSigner.Empty, -1));
        var verifier = new WindowsExecutableTrustVerifier(
            native,
            _ => Identity("AA"));

        Assert.False(verifier.TryGetTrustedSigner(
            @"C:\Roblox\RobloxPlayerBeta.exe",
            forceRefresh: false,
            out _));
        Assert.False(verifier.TryGetTrustedSigner(
            @"C:\Roblox\RobloxPlayerBeta.exe",
            forceRefresh: false,
            out _));

        Assert.Equal(2, native.Requests);
    }

    [Fact]
    public void NativeApiFailure_FailsClosed()
    {
        var verifier = new WindowsExecutableTrustVerifier(
            new ThrowingNativeVerifier(),
            _ => Identity("AA"));

        Assert.False(verifier.TryGetTrustedSigner(
            @"C:\Roblox\RobloxPlayerBeta.exe",
            forceRefresh: false,
            out var signer));
        Assert.Equal(TrustedWindowsSigner.Empty, signer);
    }

    [Fact]
    public void ChangedFileIdentity_InvalidatesSuccessfulCache()
    {
        var hash = "AA";
        var native = new FakeNativeVerifier(
            new(WindowsTrustStatus.Trusted, RobloxSigner, 0));
        var verifier = new WindowsExecutableTrustVerifier(
            native,
            _ => Identity(hash));

        Assert.True(verifier.TryGetTrustedSigner(
            @"C:\Roblox\RobloxPlayerBeta.exe", false, out _));
        hash = "BB";
        Assert.True(verifier.TryGetTrustedSigner(
            @"C:\Roblox\RobloxPlayerBeta.exe", false, out _));

        Assert.Equal(2, native.Requests);
    }

    [Fact]
    public void ForceRefresh_RevalidatesUnchangedFileBeforeSensitiveAction()
    {
        var native = new FakeNativeVerifier(
            new(WindowsTrustStatus.Trusted, RobloxSigner, 0));
        var verifier = new WindowsExecutableTrustVerifier(
            native,
            _ => Identity("AA"));

        Assert.True(verifier.TryGetTrustedSigner(
            @"C:\Roblox\RobloxPlayerBeta.exe", false, out _));
        Assert.True(verifier.TryGetTrustedSigner(
            @"C:\Roblox\RobloxPlayerBeta.exe", true, out _));

        Assert.Equal(2, native.Requests);
    }

    [Fact]
    public void SuccessfulCache_Expires()
    {
        var now = new DateTimeOffset(2026, 7, 24, 12, 0, 0, TimeSpan.Zero);
        var native = new FakeNativeVerifier(
            new(WindowsTrustStatus.Trusted, RobloxSigner, 0));
        var verifier = new WindowsExecutableTrustVerifier(
            native,
            _ => Identity("AA"),
            () => now);

        Assert.True(verifier.TryGetTrustedSigner(
            @"C:\Roblox\RobloxPlayerBeta.exe", false, out _));
        now += WindowsExecutableTrustVerifier.SuccessfulValidationLifetime +
            TimeSpan.FromSeconds(1);
        Assert.True(verifier.TryGetTrustedSigner(
            @"C:\Roblox\RobloxPlayerBeta.exe", false, out _));

        Assert.Equal(2, native.Requests);
    }

    [Theory]
    [InlineData(unchecked((int)0x80092010), (int)WindowsTrustStatus.Revoked)]
    [InlineData(unchecked((int)0x80092012), (int)WindowsTrustStatus.RevocationUnknown)]
    [InlineData(unchecked((int)0x80092013), (int)WindowsTrustStatus.RevocationOffline)]
    [InlineData(unchecked((int)0x800B010E), (int)WindowsTrustStatus.RevocationUnknown)]
    public void WinTrustStatusMapping_RejectsRevocationFailures(
        int nativeStatus,
        int expectedValue)
    {
        var expected = (WindowsTrustStatus)expectedValue;
        Assert.Equal(expected, WinTrustNativeVerifier.MapStatus(nativeStatus));
    }

    [Fact]
    public void ProviderFlags_EnableChainRevocationWithoutUnsupportedOrCacheOnlyFlags()
    {
        var flags =
            WinTrustProviderFlags.RevocationCheckChainExcludeRoot |
            WinTrustProviderFlags.DisableMd2Md4;

        Assert.Equal((WinTrustProviderFlags)0x2080, flags);
        Assert.Equal((WinTrustProviderFlags)0, flags & (WinTrustProviderFlags)0x0100);
        Assert.Equal((WinTrustProviderFlags)0, flags & (WinTrustProviderFlags)0x1000);
        Assert.Equal(WinTrustRevocationChecks.WholeChain, (WinTrustRevocationChecks)1);
    }

    private static WindowsExecutableFileIdentity Identity(string hash) => new(
        @"C:\Roblox\RobloxPlayerBeta.exe",
        1024,
        638889552000000000,
        hash.PadRight(64, hash[0]));

    private sealed class FakeNativeVerifier(WindowsTrustNativeResult result) :
        IWindowsTrustNativeVerifier
    {
        public int Requests { get; private set; }

        public WindowsTrustNativeResult Verify(string path)
        {
            Requests++;
            return result;
        }
    }

    private sealed class ThrowingNativeVerifier : IWindowsTrustNativeVerifier
    {
        public WindowsTrustNativeResult Verify(string path) =>
            throw new InvalidOperationException("simulated native failure");
    }
}
