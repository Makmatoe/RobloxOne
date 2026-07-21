using System.Security.Cryptography;
using System.Text.Json.Nodes;
using RobloxOne.ReleaseTrust;

namespace RobloxOneLauncher.Tests;

public sealed class ReleaseDescriptorPolicyTests
{
    private static readonly DateTimeOffset PublishedAt =
        new(2026, 7, 21, 12, 0, 0, TimeSpan.Zero);
    private const string PackageHash =
        "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";
    private const string PackageFile = "RobloxOne-2.1.0-full.nupkg";

    [Fact]
    public void Verify_ValidSignedDescriptor_ReturnsRelease()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var descriptor = CreateSignedDescriptor(key);

        var verified = ReleaseDescriptorPolicy.Verify(
            ReleaseDescriptorPolicy.Serialize(descriptor),
            CreateAsset(),
            key.ExportSubjectPublicKeyInfoPem(),
            PublishedAt.AddMinutes(1));

        Assert.Equal(new Version(2, 1, 0), verified.Version);
        Assert.Equal("Security and reliability improvements.",
            verified.Descriptor.ReleaseNotes);
    }

    [Fact]
    public void Verify_LowercaseFeedHashMatchingSignedHash_IsAccepted()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var descriptor = CreateSignedDescriptor(key);
        var asset = CreateAsset() with
        {
            Sha256 = PackageHash.ToLowerInvariant()
        };

        var verified = ReleaseDescriptorPolicy.Verify(
            ReleaseDescriptorPolicy.Serialize(descriptor),
            asset,
            key.ExportSubjectPublicKeyInfoPem(),
            PublishedAt.AddMinutes(1));

        Assert.Equal("2.1.0", verified.Descriptor.Version);
    }

    [Fact]
    public void Verify_ReleaseNotesChangedAfterSigning_IsRejected()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var descriptor = CreateSignedDescriptor(key) with
        {
            ReleaseNotes = "Untrusted replacement notes."
        };

        Assert.Throws<ReleaseTrustException>(() =>
            ReleaseDescriptorPolicy.Verify(
                ReleaseDescriptorPolicy.Serialize(descriptor),
                CreateAsset(),
                key.ExportSubjectPublicKeyInfoPem(),
                PublishedAt.AddMinutes(1)));
    }

    [Fact]
    public void Verify_FeedHashDoesNotMatchSignedHash_IsRejected()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var descriptor = CreateSignedDescriptor(key);
        var asset = CreateAsset() with
        {
            Sha256 = new string('B', 64)
        };

        Assert.Throws<ReleaseTrustException>(() =>
            ReleaseDescriptorPolicy.Verify(
                ReleaseDescriptorPolicy.Serialize(descriptor),
                asset,
                key.ExportSubjectPublicKeyInfoPem(),
                PublishedAt.AddMinutes(1)));
    }

    [Fact]
    public void Verify_UnknownJsonField_IsRejected()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var json = JsonNode.Parse(ReleaseDescriptorPolicy.Serialize(
            CreateSignedDescriptor(key)))!.AsObject();
        json["unexpected"] = true;

        Assert.Throws<ReleaseTrustException>(() =>
            ReleaseDescriptorPolicy.Verify(
                json.ToJsonString(),
                CreateAsset(),
                key.ExportSubjectPublicKeyInfoPem(),
                PublishedAt.AddMinutes(1)));
    }

    [Fact]
    public void Verify_DifferentSigningKey_IsRejected()
    {
        using var signingKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var otherKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var descriptor = CreateSignedDescriptor(signingKey);

        Assert.Throws<ReleaseTrustException>(() =>
            ReleaseDescriptorPolicy.Verify(
                ReleaseDescriptorPolicy.Serialize(descriptor),
                CreateAsset(),
                otherKey.ExportSubjectPublicKeyInfoPem(),
                PublishedAt.AddMinutes(1)));
    }

    private static ReleaseDescriptor CreateSignedDescriptor(ECDsa key)
    {
        var descriptor = new ReleaseDescriptor(
            ReleaseDescriptorPolicy.SchemaVersion,
            ReleaseDescriptorPolicy.Product,
            ReleaseDescriptorPolicy.Repository,
            ReleaseDescriptorPolicy.Channel,
            ReleaseDescriptorPolicy.KeyId,
            "2.1.0",
            "v2.1.0",
            PublishedAt.ToString("O"),
            PackageFile,
            ReleaseDescriptorPolicy.MinimumPackageSize,
            PackageHash,
            "Security and reliability improvements.",
            string.Empty);
        return descriptor with
        {
            Signature = Convert.ToBase64String(key.SignData(
                ReleaseDescriptorPolicy.CreateCanonicalPayload(descriptor),
                HashAlgorithmName.SHA256))
        };
    }

    private static ReleaseAssetIdentity CreateAsset() => new(
        "2.1.0",
        PackageFile,
        ReleaseDescriptorPolicy.MinimumPackageSize,
        PackageHash);
}
