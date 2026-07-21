using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace RobloxOne.ReleaseTrust;

public static class ReleaseDescriptorPolicy
{
    public const int SchemaVersion = 1;
    public const string Product = "RobloxOne";
    public const string Repository = "Makmatoe/RobloxOne";
    public const string Channel = "win-x64-stable";
    public const string KeyId = "robloxone-release-2026-01";
    public const string DescriptorFileName = "robloxone-release.json";
    public const int MaximumDescriptorBytes = 96 * 1024;
    public const int MaximumReleaseNotesLength = 64 * 1024;
    public const long MinimumPackageSize = 1024 * 1024;
    public const long MaximumPackageSize = 1024L * 1024 * 1024;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        MaxDepth = 8,
        WriteIndented = true
    };

    public static string Serialize(ReleaseDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        return JsonSerializer.Serialize(descriptor, JsonOptions) + "\n";
    }

    public static ReleaseDescriptor Deserialize(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);
        if (Encoding.UTF8.GetByteCount(json) > MaximumDescriptorBytes)
            throw new ReleaseTrustException("The release descriptor is too large.");

        try
        {
            return JsonSerializer.Deserialize<ReleaseDescriptor>(json, JsonOptions)
                ?? throw new ReleaseTrustException("The release descriptor is empty.");
        }
        catch (JsonException exception)
        {
            throw new ReleaseTrustException(
                "The release descriptor is malformed or contains unsupported fields.",
                exception);
        }
    }

    public static VerifiedReleaseDescriptor Verify(
        string json,
        ReleaseAssetIdentity asset,
        string publicKeyPem,
        DateTimeOffset? now = null)
    {
        ArgumentNullException.ThrowIfNull(asset);
        ArgumentException.ThrowIfNullOrWhiteSpace(publicKeyPem);
        var descriptor = Deserialize(json);
        var version = Validate(descriptor, asset, now ?? DateTimeOffset.UtcNow);

        byte[] signature;
        try
        {
            signature = Convert.FromBase64String(descriptor.Signature);
        }
        catch (FormatException exception)
        {
            throw new ReleaseTrustException("The release signature is malformed.", exception);
        }

        try
        {
            using var publicKey = ECDsa.Create();
            publicKey.ImportFromPem(publicKeyPem);
            if (publicKey.KeySize != 256 ||
                !publicKey.VerifyData(
                    CreateCanonicalPayload(descriptor),
                    signature,
                    HashAlgorithmName.SHA256))
            {
                throw new ReleaseTrustException(
                    "The update was not signed by the trusted Roblox One release key.");
            }
        }
        catch (Exception exception) when (
            exception is ArgumentException or CryptographicException)
        {
            throw new ReleaseTrustException(
                "The trusted Roblox One release key could not verify this update.",
                exception);
        }

        var publishedAt = DateTimeOffset.ParseExact(
            descriptor.PublishedAt,
            "O",
            CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind);
        return new VerifiedReleaseDescriptor(descriptor, version, publishedAt);
    }

    public static byte[] CreateCanonicalPayload(ReleaseDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        var payload = string.Join(
            '\n',
            descriptor.SchemaVersion.ToString(CultureInfo.InvariantCulture),
            descriptor.Product,
            descriptor.Repository,
            descriptor.Channel,
            descriptor.KeyId,
            descriptor.Version,
            descriptor.Tag,
            descriptor.PublishedAt,
            descriptor.PackageFile,
            descriptor.PackageSize.ToString(CultureInfo.InvariantCulture),
            descriptor.PackageSha256,
            descriptor.ReleaseNotes) + "\n";
        return Encoding.UTF8.GetBytes(payload);
    }

    private static Version Validate(
        ReleaseDescriptor descriptor,
        ReleaseAssetIdentity asset,
        DateTimeOffset now)
    {
        if (descriptor.SchemaVersion != SchemaVersion ||
            descriptor.Product != Product ||
            descriptor.Repository != Repository ||
            descriptor.Channel != Channel ||
            descriptor.KeyId != KeyId)
        {
            throw new ReleaseTrustException(
                "The release descriptor is not for this Roblox One update channel.");
        }

        ValidateMetadata(descriptor);
        if (!Version.TryParse(descriptor.Version, out var version) ||
            version.Build < 0 ||
            version.Revision >= 0 ||
            version.ToString(3) != descriptor.Version ||
            descriptor.Tag != $"v{descriptor.Version}" ||
            asset.Version != descriptor.Version)
        {
            throw new ReleaseTrustException("The signed release version is invalid.");
        }

        if (!DateTimeOffset.TryParseExact(
                descriptor.PublishedAt,
                "O",
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind,
                out var publishedAt) ||
            publishedAt.Offset != TimeSpan.Zero ||
            publishedAt > now.AddHours(24))
        {
            throw new ReleaseTrustException("The signed publication time is invalid.");
        }

        if (descriptor.PackageFile != asset.FileName ||
            Path.GetFileName(descriptor.PackageFile) != descriptor.PackageFile ||
            !descriptor.PackageFile.EndsWith(".nupkg", StringComparison.OrdinalIgnoreCase))
        {
            throw new ReleaseTrustException("The signed package name does not match the update.");
        }

        if (descriptor.PackageSize is < MinimumPackageSize or > MaximumPackageSize ||
            descriptor.PackageSize != asset.Size)
        {
            throw new ReleaseTrustException("The signed package size does not match the update.");
        }

        var signedHash = ParseSha256(
            descriptor.PackageSha256,
            "signed",
            requireUppercase: true);
        var assetHash = ParseSha256(
            asset.Sha256,
            "published",
            requireUppercase: false);
        if (!CryptographicOperations.FixedTimeEquals(signedHash, assetHash))
            throw new ReleaseTrustException("The signed package hash does not match the update.");

        if (string.IsNullOrWhiteSpace(descriptor.ReleaseNotes) ||
            descriptor.ReleaseNotes.Length > MaximumReleaseNotesLength ||
            descriptor.ReleaseNotes.Contains('\r') ||
            descriptor.ReleaseNotes.Any(character =>
                char.IsControl(character) && character is not ('\n' or '\t')))
        {
            throw new ReleaseTrustException("The signed release notes are invalid.");
        }

        if (string.IsNullOrWhiteSpace(descriptor.Signature) ||
            descriptor.Signature.Length > 1024)
        {
            throw new ReleaseTrustException("The release signature is missing or invalid.");
        }

        return version;
    }

    private static void ValidateMetadata(ReleaseDescriptor descriptor)
    {
        var values = new[]
        {
            descriptor.Product,
            descriptor.Repository,
            descriptor.Channel,
            descriptor.KeyId,
            descriptor.Version,
            descriptor.Tag,
            descriptor.PublishedAt,
            descriptor.PackageFile,
            descriptor.PackageSha256
        };
        if (values.Any(value =>
                string.IsNullOrWhiteSpace(value) ||
                value.Length > 256 ||
                value.Any(char.IsControl)))
        {
            throw new ReleaseTrustException("The release descriptor metadata is invalid.");
        }
    }

    private static byte[] ParseSha256(
        string? value,
        string source,
        bool requireUppercase)
    {
        if (value is null ||
            value.Length != 64 ||
            !value.All(Uri.IsHexDigit) ||
            (requireUppercase &&
             !value.Equals(value.ToUpperInvariant(), StringComparison.Ordinal)))
        {
            throw new ReleaseTrustException(
                $"The {source} package SHA-256 value is invalid.");
        }

        return Convert.FromHexString(value);
    }
}
