using System.Globalization;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace SessionDock.SystemProcesses;

internal static partial class HandleScopeReleaseAuthorizationPolicy
{
    internal const int SchemaVersion = 1;
    internal const string Product = "HandleScope";
    internal const string Repository = "Makmatoe/HandleScope";
    internal const string Channel = "win-x64-stable";
    internal const string DescriptorFileName = "handlescope-release.json";
    internal const string ChecksumFileName = "SHA256SUMS.txt";
    internal const string Platform = "windows";
    internal const string Architecture = "x64";
    internal const int MaximumDescriptorBytes = 96 * 1024;

    private static readonly string[] RequiredProperties =
    [
        "schemaVersion", "product", "repository", "channel", "keyId",
        "version", "tag", "publishedAt", "packageFile", "packageSize",
        "packageSha256", "checksumFile", "checksumSize", "checksumSha256",
        "internalManifestSha256", "platform", "architecture", "signature"
    ];
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        MaxDepth = 4
    };

    internal static VerifiedHandleScopeReleaseDescriptor Verify(
        ReadOnlyMemory<byte> descriptorBytes,
        HandleScopeReleaseIdentity release,
        IHandleScopeReleaseKeyProvider keyProvider,
        DateTimeOffset? now = null)
    {
        ArgumentNullException.ThrowIfNull(release);
        var verified = VerifyInstalled(
            descriptorBytes,
            keyProvider,
            now);
        var descriptor = verified.Descriptor;
        if (descriptor.Version != release.Version ||
            descriptor.Tag != release.TagName ||
            descriptor.PackageFile != release.Package.Name ||
            descriptor.PackageSize != release.Package.Size ||
            descriptor.ChecksumFile != release.Checksums.Name ||
            descriptor.ChecksumSize != release.Checksums.Size ||
            !FixedTimeHexEquals(descriptor.PackageSha256, release.Package.Sha256) ||
            !FixedTimeHexEquals(descriptor.ChecksumSha256, release.Checksums.Sha256))
        {
            throw new HandleScopeInstallException(
                "The signed HandleScope authorization does not match the immutable GitHub release assets.");
        }
        return verified;
    }

    internal static VerifiedHandleScopeReleaseDescriptor VerifyInstalled(
        ReadOnlyMemory<byte> descriptorBytes,
        IHandleScopeReleaseKeyProvider keyProvider,
        DateTimeOffset? now = null)
    {
        ArgumentNullException.ThrowIfNull(keyProvider);
        var descriptor = DeserializeStrict(descriptorBytes);
        var publishedAt = Validate(descriptor, now ?? DateTimeOffset.UtcNow);
        if (!keyProvider.TryGetPublicKeyPem(descriptor.KeyId, out var publicKeyPem) ||
            string.IsNullOrWhiteSpace(publicKeyPem))
        {
            throw new HandleScopeInstallException(
                "HandleScope installation is unavailable because no genuine production HandleScope release key is configured in this SessionDock build.");
        }

        byte[] signature;
        try
        {
            if (descriptor.Signature.Any(char.IsWhiteSpace))
                throw new FormatException();
            signature = Convert.FromBase64String(descriptor.Signature);
        }
        catch (FormatException exception)
        {
            throw new HandleScopeInstallException(
                "The HandleScope release signature is malformed.",
                exception);
        }
        if (signature.Length != 64)
        {
            throw new HandleScopeInstallException(
                "The HandleScope release signature has an unsupported format.");
        }

        try
        {
            using var publicKey = ECDsa.Create();
            publicKey.ImportFromPem(publicKeyPem);
            if (publicKey.KeySize != 256 ||
                !publicKey.VerifyData(
                    CreateCanonicalPayload(descriptor),
                    signature,
                    HashAlgorithmName.SHA256,
                    DSASignatureFormat.IeeeP1363FixedFieldConcatenation))
            {
                throw new HandleScopeInstallException(
                    "The HandleScope release was not authorized by its pinned release key.");
            }
        }
        catch (Exception exception) when (
            exception is ArgumentException or CryptographicException)
        {
            throw new HandleScopeInstallException(
                "The pinned HandleScope release key could not verify this authorization.",
                exception);
        }

        return new(descriptor, publishedAt);
    }

    internal static byte[] CreateCanonicalPayload(
        HandleScopeReleaseDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        return Encoding.UTF8.GetBytes(string.Join(
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
            descriptor.ChecksumFile,
            descriptor.ChecksumSize.ToString(CultureInfo.InvariantCulture),
            descriptor.ChecksumSha256,
            descriptor.InternalManifestSha256,
            descriptor.Platform,
            descriptor.Architecture) + "\n");
    }

    internal static byte[] GetAuthorizedApiHash(ReadOnlySpan<byte> manifestBytes)
    {
        string text;
        try
        {
            text = new UTF8Encoding(false, true).GetString(manifestBytes);
        }
        catch (DecoderFallbackException exception)
        {
            throw new HandleScopeInstallException(
                "The authorized HandleScope inventory is not valid UTF-8.",
                exception);
        }

        byte[]? apiHash = null;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var rawLine in text.Split('\n'))
        {
            var line = rawLine.EndsWith('\r') ? rawLine[..^1] : rawLine;
            if (line.Length == 0)
                continue;
            var match = ManifestLinePattern().Match(line);
            if (!match.Success || !seen.Add(match.Groups["path"].Value))
            {
                throw new HandleScopeInstallException(
                    "The authorized HandleScope installed-file inventory is malformed.");
            }
            if (match.Groups["path"].Value == "api/HandleScope.Api.exe")
                apiHash = Convert.FromHexString(match.Groups["hash"].Value);
        }
        return apiHash ?? throw new HandleScopeInstallException(
            "The authorized HandleScope inventory does not include HandleScope.Api.exe.");
    }

    private static HandleScopeReleaseDescriptor DeserializeStrict(
        ReadOnlyMemory<byte> bytes)
    {
        if (bytes.Length is <= 0 or > MaximumDescriptorBytes ||
            (bytes.Length >= 3 && bytes.Span[0] == 0xEF &&
             bytes.Span[1] == 0xBB && bytes.Span[2] == 0xBF))
        {
            throw new HandleScopeInstallException(
                "The HandleScope release authorization is empty, oversized, or not canonical UTF-8.");
        }
        try
        {
            _ = new UTF8Encoding(false, true).GetString(bytes.Span);
            using var document = JsonDocument.Parse(
                bytes,
                new JsonDocumentOptions
                {
                    MaxDepth = 4,
                    AllowTrailingCommas = false,
                    CommentHandling = JsonCommentHandling.Disallow
                });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                root.GetPropertyCount() != RequiredProperties.Length)
            {
                throw InvalidDescriptor();
            }
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in root.EnumerateObject())
            {
                if (!names.Add(property.Name))
                    throw InvalidDescriptor();
            }
            if (!names.SetEquals(RequiredProperties))
                throw InvalidDescriptor();

            return JsonSerializer.Deserialize<HandleScopeReleaseDescriptor>(
                       bytes.Span,
                       JsonOptions) ?? throw InvalidDescriptor();
        }
        catch (DecoderFallbackException exception)
        {
            throw new HandleScopeInstallException(
                "The HandleScope release authorization is not valid UTF-8.",
                exception);
        }
        catch (JsonException exception)
        {
            throw new HandleScopeInstallException(
                "The HandleScope release authorization is malformed or contains unsupported fields.",
                exception);
        }
    }

    private static DateTimeOffset Validate(
        HandleScopeReleaseDescriptor descriptor,
        DateTimeOffset now)
    {
        var strings = new[]
        {
            descriptor.Product, descriptor.Repository, descriptor.Channel,
            descriptor.KeyId, descriptor.Version, descriptor.Tag,
            descriptor.PublishedAt, descriptor.PackageFile,
            descriptor.PackageSha256, descriptor.ChecksumFile,
            descriptor.ChecksumSha256, descriptor.InternalManifestSha256,
            descriptor.Platform, descriptor.Architecture, descriptor.Signature
        };
        if (strings.Any(value =>
                string.IsNullOrWhiteSpace(value) || value.Length > 512 ||
                value.Any(char.IsControl)) ||
            descriptor.SchemaVersion != SchemaVersion ||
            descriptor.Product != Product ||
            descriptor.Repository != Repository ||
            descriptor.Channel != Channel ||
            descriptor.Platform != Platform ||
            descriptor.Architecture != Architecture ||
            !KeyIdPattern().IsMatch(descriptor.KeyId))
        {
            throw InvalidDescriptor();
        }
        if (!Version.TryParse(descriptor.Version, out var version) ||
            version.Build < 0 || version.Revision >= 0 ||
            version.ToString(3) != descriptor.Version ||
            descriptor.Tag != $"v{descriptor.Version}")
        {
            throw new HandleScopeInstallException(
                "The signed HandleScope version or tag is not one stable semantic version.");
        }
        if (!DateTimeOffset.TryParseExact(
                descriptor.PublishedAt,
                "O",
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind,
                out var publishedAt) ||
            publishedAt.Offset != TimeSpan.Zero ||
            publishedAt > now.ToUniversalTime().AddHours(24))
        {
            throw new HandleScopeInstallException(
                "The signed HandleScope publication time is invalid.");
        }
        var expectedPackage = $"HandleScope-{descriptor.Version}-win-x64.zip";
        if (descriptor.PackageFile != expectedPackage ||
            Path.GetFileName(descriptor.PackageFile) != descriptor.PackageFile ||
            descriptor.PackageSize is <= 0 or > HandleScopeReleasePolicy.MaximumPackageBytes ||
            descriptor.ChecksumFile != ChecksumFileName ||
            descriptor.ChecksumSize is <= 0 or > HandleScopeReleasePolicy.MaximumChecksumBytes)
        {
            throw InvalidDescriptor();
        }
        _ = ParseSha256(descriptor.PackageSha256);
        _ = ParseSha256(descriptor.ChecksumSha256);
        _ = ParseSha256(descriptor.InternalManifestSha256);
        return publishedAt;
    }

    private static bool FixedTimeHexEquals(string value, ReadOnlySpan<byte> expected)
    {
        var parsed = ParseSha256(value);
        return CryptographicOperations.FixedTimeEquals(parsed, expected);
    }

    internal static byte[] ParseSha256(string value)
    {
        if (value.Length != 64 || !value.All(character =>
                character is (>= '0' and <= '9') or (>= 'A' and <= 'F')))
        {
            throw new HandleScopeInstallException(
                "A HandleScope SHA-256 value is not canonical uppercase hexadecimal.");
        }
        return Convert.FromHexString(value);
    }

    private static HandleScopeInstallException InvalidDescriptor() => new(
        "The HandleScope release authorization does not match the required trust identity.");

    [GeneratedRegex(
        "^handlescope-release-(?:0|[1-9][0-9]{3})-(?:0[1-9]|1[0-2])$",
        RegexOptions.CultureInvariant)]
    private static partial Regex KeyIdPattern();

    [GeneratedRegex(
        "^(?<hash>[0-9a-f]{64})  (?<path>[^\\\\\\r\\n]+)$",
        RegexOptions.CultureInvariant)]
    private static partial Regex ManifestLinePattern();
}

internal interface IHandleScopeReleaseKeyProvider
{
    bool TryGetPublicKeyPem(string keyId, out string publicKeyPem);
}

internal sealed class EmbeddedHandleScopeReleaseKeyProvider :
    IHandleScopeReleaseKeyProvider
{
    internal static EmbeddedHandleScopeReleaseKeyProvider Instance { get; } = new();
    private const string ResourceName =
        "SessionDock.Embedded.HandleScopeReleasePublicKeys.json";
    private static readonly JsonSerializerOptions PublicKeyJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        MaxDepth = 4
    };
    private readonly Lazy<IReadOnlyDictionary<string, string>> _keys =
        new(LoadKeys);

    public bool TryGetPublicKeyPem(string keyId, out string publicKeyPem) =>
        _keys.Value.TryGetValue(keyId, out publicKeyPem!);

    private static IReadOnlyDictionary<string, string> LoadKeys()
    {
        try
        {
            using var stream = Assembly.GetExecutingAssembly()
                .GetManifestResourceStream(ResourceName);
            if (stream is null || stream.Length is <= 0 or > 64 * 1024)
                return new Dictionary<string, string>(StringComparer.Ordinal);
            using var reader = new StreamReader(
                stream,
                new UTF8Encoding(false, true),
                detectEncodingFromByteOrderMarks: false,
                leaveOpen: false);
            var json = reader.ReadToEnd();
            var entries = JsonSerializer.Deserialize<HandleScopePublicKeyEntry[]>(
                json,
                PublicKeyJsonOptions)
                ?? [];
            var keys = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var entry in entries)
            {
                if (!keys.TryAdd(entry.KeyId, entry.PublicKeyPem))
                    return new Dictionary<string, string>(StringComparer.Ordinal);
            }
            return keys;
        }
        catch
        {
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }
    }

    private sealed record HandleScopePublicKeyEntry(
        string KeyId,
        string PublicKeyPem);
}

internal sealed record HandleScopeReleaseDescriptor(
    int SchemaVersion,
    string Product,
    string Repository,
    string Channel,
    string KeyId,
    string Version,
    string Tag,
    string PublishedAt,
    string PackageFile,
    long PackageSize,
    string PackageSha256,
    string ChecksumFile,
    long ChecksumSize,
    string ChecksumSha256,
    string InternalManifestSha256,
    string Platform,
    string Architecture,
    string Signature);

internal sealed record VerifiedHandleScopeReleaseDescriptor(
    HandleScopeReleaseDescriptor Descriptor,
    DateTimeOffset PublishedAt);
