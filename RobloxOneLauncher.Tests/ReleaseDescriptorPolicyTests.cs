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
    private const string PackageFile = "RobloxOne-2.1.0-win-x64-stable-full.nupkg";

    [Fact]
    public void Verify_ValidSignedDescriptor_ReturnsRelease()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var verified = Verify(CreateSignedDescriptor(key), CreateAsset(), key);

        Assert.Equal(new Version(2, 1, 0), verified.Version);
        Assert.Equal(
            "Security and reliability improvements.",
            verified.Descriptor.ReleaseNotes);
    }

    [Fact]
    public void Verify_LowercaseFeedHashMatchingSignedHash_IsAccepted()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var asset = CreateAsset() with { Sha256 = PackageHash.ToLowerInvariant() };

        var verified = Verify(CreateSignedDescriptor(key), asset, key);

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
            Verify(descriptor, CreateAsset(), key));
    }

    [Fact]
    public void Verify_FeedHashDoesNotMatchSignedHash_IsRejected()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var asset = CreateAsset() with { Sha256 = new string('B', 64) };

        Assert.Throws<ReleaseTrustException>(() =>
            Verify(CreateSignedDescriptor(key), asset, key));
    }

    [Fact]
    public void Verify_UnknownJsonField_IsRejected()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var json = JsonNode.Parse(
            ReleaseDescriptorPolicy.Serialize(CreateSignedDescriptor(key)))!
            .AsObject();
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

        Assert.Throws<ReleaseTrustException>(() =>
            Verify(CreateSignedDescriptor(signingKey), CreateAsset(), otherKey));
    }

    [Fact]
    public void Verify_ChannelBindingMetadataChanged_IsRejectedEvenWhenSigned()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var descriptor = CreateUnsignedDescriptor();
        var variants = new[]
        {
            descriptor with { SchemaVersion = descriptor.SchemaVersion + 1 },
            descriptor with { Product = "OtherProduct" },
            descriptor with { Repository = "other/repository" },
            descriptor with { Channel = "other-channel" },
            descriptor with { KeyId = "other-key" }
        };

        foreach (var variant in variants)
        {
            var exception = Assert.Throws<ReleaseTrustException>(() =>
                Verify(SignDescriptor(variant, key), CreateAsset(), key));
            Assert.Contains(
                "update channel",
                exception.Message,
                StringComparison.OrdinalIgnoreCase);
        }
    }

    [Theory]
    [InlineData((1024L * 1024) - 1)]
    [InlineData((1024L * 1024 * 1024) + 1)]
    public void Verify_PackageSizeOutsidePolicyBounds_IsRejected(long packageSize)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var descriptor = SignDescriptor(
            CreateUnsignedDescriptor() with { PackageSize = packageSize },
            key);
        var asset = CreateAsset() with { Size = packageSize };

        var exception = Assert.Throws<ReleaseTrustException>(() =>
            Verify(descriptor, asset, key));

        Assert.Contains("package size", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Verify_MalformedSignature_IsRejectedAsTrustFailure()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var descriptor = CreateUnsignedDescriptor() with { Signature = "not-base64" };

        var exception = Assert.Throws<ReleaseTrustException>(() =>
            Verify(descriptor, CreateAsset(), key));

        Assert.Contains("signature", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Deserialize_DescriptorOverUtf8ByteLimit_IsRejected()
    {
        var json = $$"""
            { "value": "{{new string('\u00e9', ReleaseDescriptorPolicy.MaximumDescriptorBytes)}}" }
            """;

        var exception = Assert.Throws<ReleaseTrustException>(() =>
            ReleaseDescriptorPolicy.Deserialize(json));

        Assert.Contains("too large", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("../RobloxOne-2.1.0-win-x64-stable-full.nupkg")]
    [InlineData(@"..\RobloxOne-2.1.0-win-x64-stable-full.nupkg")]
    [InlineData("nested/RobloxOne-2.1.0-win-x64-stable-full.nupkg")]
    [InlineData(@"C:\RobloxOne-2.1.0-win-x64-stable-full.nupkg")]
    public void Verify_PathLikePackageName_IsRejected(string unsafeName)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var descriptor = SignDescriptor(
            CreateUnsignedDescriptor() with { PackageFile = unsafeName },
            key);
        var asset = CreateAsset() with { FileName = unsafeName };

        var exception = Assert.Throws<ReleaseTrustException>(() =>
            Verify(descriptor, asset, key));

        Assert.Contains("package name", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Verify_MissingFeedHash_IsRejectedAsTrustFailure()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var asset = CreateAsset() with { Sha256 = null! };

        var exception = Assert.Throws<ReleaseTrustException>(() =>
            Verify(CreateSignedDescriptor(key), asset, key));

        Assert.Contains("SHA-256", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Verify_MalformedPublicKey_IsRejectedAsTrustFailure()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);

        var exception = Assert.Throws<ReleaseTrustException>(() =>
            ReleaseDescriptorPolicy.Verify(
                ReleaseDescriptorPolicy.Serialize(CreateSignedDescriptor(key)),
                CreateAsset(),
                "not a PEM public key",
                PublishedAt.AddMinutes(1)));

        Assert.Contains("release key", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Verify_PublicationMoreThanOneDayInFuture_IsRejected()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var descriptor = SignDescriptor(
            CreateUnsignedDescriptor() with
            {
                PublishedAt = PublishedAt.AddHours(24).AddTicks(1).ToString("O")
            },
            key);

        var exception = Assert.Throws<ReleaseTrustException>(() =>
            ReleaseDescriptorPolicy.Verify(
                ReleaseDescriptorPolicy.Serialize(descriptor),
                CreateAsset(),
                key.ExportSubjectPublicKeyInfoPem(),
                PublishedAt));

        Assert.Contains("publication time", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("Line one\rLine two")]
    [InlineData("Line one\u0000Line two")]
    public void Verify_ReleaseNotesWithUnsupportedControlCharacter_IsRejected(
        string releaseNotes)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var descriptor = SignDescriptor(
            CreateUnsignedDescriptor() with { ReleaseNotes = releaseNotes },
            key);

        var exception = Assert.Throws<ReleaseTrustException>(() =>
            Verify(descriptor, CreateAsset(), key));

        Assert.Contains("release notes", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static VerifiedReleaseDescriptor Verify(
        ReleaseDescriptor descriptor,
        ReleaseAssetIdentity asset,
        ECDsa key) =>
        ReleaseDescriptorPolicy.Verify(
            ReleaseDescriptorPolicy.Serialize(descriptor),
            asset,
            key.ExportSubjectPublicKeyInfoPem(),
            PublishedAt.AddMinutes(1));

    private static ReleaseDescriptor CreateSignedDescriptor(ECDsa key) =>
        SignDescriptor(CreateUnsignedDescriptor(), key);

    private static ReleaseDescriptor CreateUnsignedDescriptor() =>
        new(
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

    private static ReleaseDescriptor SignDescriptor(
        ReleaseDescriptor descriptor,
        ECDsa key) =>
        descriptor with
        {
            Signature = Convert.ToBase64String(key.SignData(
                ReleaseDescriptorPolicy.CreateCanonicalPayload(descriptor),
                HashAlgorithmName.SHA256))
        };

    private static ReleaseAssetIdentity CreateAsset() => new(
        "2.1.0",
        PackageFile,
        ReleaseDescriptorPolicy.MinimumPackageSize,
        PackageHash);
}
