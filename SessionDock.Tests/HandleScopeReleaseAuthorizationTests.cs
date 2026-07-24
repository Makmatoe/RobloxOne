using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using SessionDock.SystemProcesses;

namespace SessionDock.Tests;

public sealed class HandleScopeReleaseAuthorizationTests : IDisposable
{
    private const string Version = "1.2.3";
    private const string Tag = "v1.2.3";
    private const string KeyId = "handlescope-release-2026-01";
    private const string PackageName = "HandleScope-1.2.3-win-x64.zip";
    private const string ChecksumName = "SHA256SUMS.txt";
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        $"SessionDock-HandleScopeAuthorization-{Guid.NewGuid():N}");

    [Fact]
    public void Verify_AcceptsExactSignedDescriptorAndReleaseAssets()
    {
        using var fixture = CreateFixture();

        var verified = HandleScopeReleaseAuthorizationPolicy.Verify(
            fixture.DescriptorBytes,
            fixture.Release,
            fixture.Provider,
            fixture.PublishedAt.AddMinutes(1));

        Assert.Equal(Version, verified.Descriptor.Version);
        Assert.Equal(KeyId, verified.Descriptor.KeyId);
    }

    [Theory]
    [InlineData("repository", "Other/Repository")]
    [InlineData("keyId", "handlescope-release-2026-02")]
    [InlineData("version", "1.2.4")]
    [InlineData("tag", "v1.2.4")]
    [InlineData("platform", "linux")]
    [InlineData("architecture", "arm64")]
    public void Verify_RejectsTamperedIdentityFields(string property, string value)
    {
        using var fixture = CreateFixture();
        var tampered = Mutate(fixture.DescriptorBytes, node => node[property] = value);

        Assert.Throws<HandleScopeInstallException>(() =>
            HandleScopeReleaseAuthorizationPolicy.Verify(
                tampered,
                fixture.Release,
                fixture.Provider,
                fixture.PublishedAt.AddMinutes(1)));
    }

    [Fact]
    public void Verify_RejectsTamperedSignature()
    {
        using var fixture = CreateFixture();
        var tampered = Mutate(
            fixture.DescriptorBytes,
            node => node["signature"] = Convert.ToBase64String(new byte[64]));

        Assert.Throws<HandleScopeInstallException>(() =>
            HandleScopeReleaseAuthorizationPolicy.Verify(
                tampered,
                fixture.Release,
                fixture.Provider));
    }

    [Fact]
    public void Verify_RejectsWrongPublicKey()
    {
        using var fixture = CreateFixture();
        using var wrongKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var wrongProvider = new TestKeyProvider(
            wrongKey.ExportSubjectPublicKeyInfoPem());

        Assert.Throws<HandleScopeInstallException>(() =>
            HandleScopeReleaseAuthorizationPolicy.Verify(
                fixture.DescriptorBytes,
                fixture.Release,
                wrongProvider));
    }

    [Fact]
    public void Verify_RejectsModifiedPackageOrChecksumMetadata()
    {
        using var fixture = CreateFixture();
        var modifiedPackage = fixture.Release with
        {
            Package = fixture.Release.Package with
            {
                Sha256 = SHA256.HashData("modified"u8)
            }
        };
        var modifiedChecksum = fixture.Release with
        {
            Checksums = fixture.Release.Checksums with
            {
                Size = fixture.Release.Checksums.Size + 1
            }
        };

        Assert.Throws<HandleScopeInstallException>(() =>
            HandleScopeReleaseAuthorizationPolicy.Verify(
                fixture.DescriptorBytes,
                modifiedPackage,
                fixture.Provider));
        Assert.Throws<HandleScopeInstallException>(() =>
            HandleScopeReleaseAuthorizationPolicy.Verify(
                fixture.DescriptorBytes,
                modifiedChecksum,
                fixture.Provider));
    }

    [Fact]
    public void Verify_RejectsDuplicateOrUnknownJsonProperties()
    {
        using var fixture = CreateFixture();
        var text = Encoding.UTF8.GetString(fixture.DescriptorBytes);
        var duplicate = Encoding.UTF8.GetBytes(
            text.Replace(
                "{",
                "{\"product\":\"HandleScope\",",
                StringComparison.Ordinal));
        var unknown = Encoding.UTF8.GetBytes(
            text[..^1] + ",\"unexpected\":true}");

        Assert.Throws<HandleScopeInstallException>(() =>
            HandleScopeReleaseAuthorizationPolicy.Verify(
                duplicate,
                fixture.Release,
                fixture.Provider));
        Assert.Throws<HandleScopeInstallException>(() =>
            HandleScopeReleaseAuthorizationPolicy.Verify(
                unknown,
                fixture.Release,
                fixture.Provider));
    }

    [Fact]
    public void Verify_RejectsOversizedDescriptor()
    {
        using var fixture = CreateFixture();
        var oversized = new byte[
            HandleScopeReleaseAuthorizationPolicy.MaximumDescriptorBytes + 1];

        Assert.Throws<HandleScopeInstallException>(() =>
            HandleScopeReleaseAuthorizationPolicy.Verify(
                oversized,
                fixture.Release,
                fixture.Provider));
    }

    [Fact]
    public void Verify_RejectsFutureTimestamp()
    {
        var now = new DateTimeOffset(2026, 7, 24, 12, 0, 0, TimeSpan.Zero);
        using var fixture = CreateFixture(now.AddHours(24).AddTicks(1));

        Assert.Throws<HandleScopeInstallException>(() =>
            HandleScopeReleaseAuthorizationPolicy.Verify(
                fixture.DescriptorBytes,
                fixture.Release,
                fixture.Provider,
                now));
    }

    [Fact]
    public void Verify_RejectsPrereleaseVersion()
    {
        using var fixture = CreateFixture();
        var prerelease = Mutate(
            fixture.DescriptorBytes,
            node => node["version"] = "1.2.3-beta");

        Assert.Throws<HandleScopeInstallException>(() =>
            HandleScopeReleaseAuthorizationPolicy.Verify(
                prerelease,
                fixture.Release,
                fixture.Provider));
    }

    [Fact]
    public void InstalledRuntimeVerifier_RejectsReplacedExecutableAndManifest()
    {
        Directory.CreateDirectory(_root);
        var dataRoot = Path.Combine(_root, "SessionDock");
        var installRoot = Path.Combine(_root, "Programs", "HandleScope", "Api");
        Directory.CreateDirectory(dataRoot);
        Directory.CreateDirectory(installRoot);
        using var fixture = CreateFixture();
        var executablePath = Path.Combine(installRoot, "HandleScope.Api.exe");
        File.WriteAllBytes(executablePath, fixture.ApiBytes);
        var verifier = new HandleScopeInstalledRuntimeVerifier(
            _root,
            dataRoot,
            fixture.Provider);
        verifier.PersistAuthorization(
            fixture.DescriptorBytes,
            fixture.ManifestBytes);

        Assert.True(verifier.IsAuthorized(executablePath));
        File.WriteAllText(executablePath, "replaced");
        Assert.False(verifier.IsAuthorized(executablePath));

        File.WriteAllBytes(executablePath, fixture.ApiBytes);
        var manifestPath = Path.Combine(
            dataRoot,
            "HandleScopeAuthorization",
            "CONTENTS.sha256");
        File.AppendAllText(manifestPath, "00  extra.txt\n");
        Assert.False(verifier.IsAuthorized(executablePath));
    }

    [Fact]
    public void InstalledRuntimeVerifier_AcceptsGitHubReceiptAndRejectsTampering()
    {
        Directory.CreateDirectory(_root);
        var dataRoot = Path.Combine(_root, "SessionDock");
        var installRoot = Path.Combine(_root, "Programs", "HandleScope", "Api");
        Directory.CreateDirectory(dataRoot);
        Directory.CreateDirectory(installRoot);
        using var fixture = CreateFixture();
        var executablePath = Path.Combine(installRoot, "HandleScope.Api.exe");
        File.WriteAllBytes(executablePath, fixture.ApiBytes);
        var verifier = new HandleScopeInstalledRuntimeVerifier(
            _root,
            dataRoot,
            fixture.Provider);
        verifier.PersistGitHubReleaseAuthorization(
            fixture.Release with { Descriptor = null },
            fixture.ManifestBytes);

        Assert.True(verifier.IsAuthorized(executablePath));
        File.WriteAllText(executablePath, "replaced");
        Assert.False(verifier.IsAuthorized(executablePath));

        File.WriteAllBytes(executablePath, fixture.ApiBytes);
        var receiptPath = Path.Combine(
            dataRoot,
            "HandleScopeAuthorization",
            "github-release-receipt.json");
        File.WriteAllText(receiptPath, "{}");
        Assert.False(verifier.IsAuthorized(executablePath));
    }

    [Fact]
    public void InstalledRuntimeVerifier_SwitchesAuthorizationModesFailClosed()
    {
        Directory.CreateDirectory(_root);
        var dataRoot = Path.Combine(_root, "SessionDock");
        var installRoot = Path.Combine(_root, "Programs", "HandleScope", "Api");
        Directory.CreateDirectory(dataRoot);
        Directory.CreateDirectory(installRoot);
        using var fixture = CreateFixture();
        var executablePath = Path.Combine(installRoot, "HandleScope.Api.exe");
        File.WriteAllBytes(executablePath, fixture.ApiBytes);
        var verifier = new HandleScopeInstalledRuntimeVerifier(
            _root,
            dataRoot,
            fixture.Provider);
        var authorizationRoot = Path.Combine(
            dataRoot,
            "HandleScopeAuthorization");
        var descriptorPath = Path.Combine(
            authorizationRoot,
            HandleScopeReleaseAuthorizationPolicy.DescriptorFileName);
        var receiptPath = Path.Combine(
            authorizationRoot,
            "github-release-receipt.json");

        verifier.PersistAuthorization(
            fixture.DescriptorBytes,
            fixture.ManifestBytes);
        Assert.True(verifier.IsAuthorized(executablePath));
        Assert.True(File.Exists(descriptorPath));
        Assert.False(File.Exists(receiptPath));

        verifier.PersistGitHubReleaseAuthorization(
            fixture.Release with { Descriptor = null },
            fixture.ManifestBytes);
        Assert.True(verifier.IsAuthorized(executablePath));
        Assert.False(File.Exists(descriptorPath));
        Assert.True(File.Exists(receiptPath));

        verifier.PersistAuthorization(
            fixture.DescriptorBytes,
            fixture.ManifestBytes);
        Assert.True(verifier.IsAuthorized(executablePath));
        Assert.True(File.Exists(descriptorPath));
        Assert.False(File.Exists(receiptPath));
    }

    private static Fixture CreateFixture(DateTimeOffset? publishedAt = null)
    {
        var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var provider = new TestKeyProvider(key.ExportSubjectPublicKeyInfoPem());
        var apiBytes = "verified api executable"u8.ToArray();
        var manifestBytes = Encoding.UTF8.GetBytes(
            $"{Convert.ToHexString(SHA256.HashData(apiBytes)).ToLowerInvariant()}  api/HandleScope.Api.exe\n");
        var packageBytes = "verified package"u8.ToArray();
        var checksumBytes = Encoding.UTF8.GetBytes(
            $"{Convert.ToHexString(SHA256.HashData(packageBytes)).ToLowerInvariant()}  {PackageName}\n");
        var publication = publishedAt ??
            new DateTimeOffset(2026, 7, 24, 12, 0, 0, TimeSpan.Zero);
        var unsigned = new HandleScopeReleaseDescriptor(
            HandleScopeReleaseAuthorizationPolicy.SchemaVersion,
            HandleScopeReleaseAuthorizationPolicy.Product,
            HandleScopeReleaseAuthorizationPolicy.Repository,
            HandleScopeReleaseAuthorizationPolicy.Channel,
            KeyId,
            Version,
            Tag,
            publication.ToString("O"),
            PackageName,
            packageBytes.LongLength,
            Convert.ToHexString(SHA256.HashData(packageBytes)),
            ChecksumName,
            checksumBytes.LongLength,
            Convert.ToHexString(SHA256.HashData(checksumBytes)),
            Convert.ToHexString(SHA256.HashData(manifestBytes)),
            HandleScopeReleaseAuthorizationPolicy.Platform,
            HandleScopeReleaseAuthorizationPolicy.Architecture,
            string.Empty);
        var signature = key.SignData(
            HandleScopeReleaseAuthorizationPolicy.CreateCanonicalPayload(unsigned),
            HashAlgorithmName.SHA256,
            DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        var descriptor = unsigned with
        {
            Signature = Convert.ToBase64String(signature)
        };
        var descriptorBytes = JsonSerializer.SerializeToUtf8Bytes(
            descriptor,
            new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase
            });
        var release = new HandleScopeReleaseIdentity(
            Version,
            Tag,
            Asset(PackageName, packageBytes),
            Asset(ChecksumName, checksumBytes),
            Asset(
                HandleScopeReleaseAuthorizationPolicy.DescriptorFileName,
                descriptorBytes));
        return new Fixture(
            key,
            provider,
            publication,
            descriptorBytes,
            manifestBytes,
            apiBytes,
            release);
    }

    private static HandleScopeReleaseAsset Asset(string name, byte[] contents) => new(
        name,
        contents.LongLength,
        SHA256.HashData(contents),
        new Uri(
            $"https://github.com/Makmatoe/HandleScope/releases/download/{Tag}/{name}"));

    private static byte[] Mutate(byte[] bytes, Action<JsonObject> mutation)
    {
        var node = JsonNode.Parse(bytes)?.AsObject()
            ?? throw new InvalidOperationException("Descriptor fixture invalid.");
        mutation(node);
        return Encoding.UTF8.GetBytes(node.ToJsonString());
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private sealed class TestKeyProvider(string publicKeyPem) :
        IHandleScopeReleaseKeyProvider
    {
        public bool TryGetPublicKeyPem(string keyId, out string value)
        {
            value = publicKeyPem;
            return keyId == KeyId;
        }
    }

    private sealed record Fixture(
        ECDsa Key,
        TestKeyProvider Provider,
        DateTimeOffset PublishedAt,
        byte[] DescriptorBytes,
        byte[] ManifestBytes,
        byte[] ApiBytes,
        HandleScopeReleaseIdentity Release) : IDisposable
    {
        public void Dispose() => Key.Dispose();
    }
}
