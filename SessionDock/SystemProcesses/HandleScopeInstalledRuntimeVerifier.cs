using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SessionDock.SystemProcesses;

internal interface IHandleScopeInstalledRuntimeVerifier
{
    bool IsAuthorized(string executablePath);
}

internal sealed class HandleScopeInstalledRuntimeVerifier :
    IHandleScopeInstalledRuntimeVerifier
{
    private const int MaximumManifestBytes = 1024 * 1024;
    private const int MaximumReceiptBytes = 16 * 1024;
    private const int ReceiptSchemaVersion = 1;
    private const string AuthorizationDirectoryName =
        "HandleScopeAuthorization";
    private const string ManifestFileName = "CONTENTS.sha256";
    private const string ReceiptFileName = "github-release-receipt.json";
    private static readonly string[] ReceiptProperties =
    [
        "schemaVersion", "repository", "version", "tag", "packageFile",
        "packageSize", "packageSha256", "checksumFile", "checksumSize",
        "checksumSha256", "manifestSha256"
    ];
    private static readonly JsonSerializerOptions ReceiptJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        MaxDepth = 4
    };

    private readonly string _localAppDataRoot;
    private readonly string _authorizationRoot;
    private readonly string _descriptorPath;
    private readonly string _manifestPath;
    private readonly string _receiptPath;
    private readonly IHandleScopeReleaseKeyProvider _keyProvider;
    private readonly Func<string, bool>? _isReparsePoint;

    internal HandleScopeInstalledRuntimeVerifier(
        string localAppDataRoot,
        string sessionDockDataRoot,
        IHandleScopeReleaseKeyProvider keyProvider,
        Func<string, bool>? isReparsePoint = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(localAppDataRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionDockDataRoot);
        _keyProvider = keyProvider ??
            throw new ArgumentNullException(nameof(keyProvider));
        _localAppDataRoot = Path.GetFullPath(localAppDataRoot);
        _authorizationRoot = Path.Combine(
            Path.GetFullPath(sessionDockDataRoot),
            AuthorizationDirectoryName);
        _descriptorPath = Path.Combine(
            _authorizationRoot,
            HandleScopeReleaseAuthorizationPolicy.DescriptorFileName);
        _manifestPath = Path.Combine(_authorizationRoot, ManifestFileName);
        _receiptPath = Path.Combine(_authorizationRoot, ReceiptFileName);
        _isReparsePoint = isReparsePoint;
    }

    internal void PersistAuthorization(
        ReadOnlyMemory<byte> descriptorBytes,
        ReadOnlyMemory<byte> manifestBytes)
    {
        try
        {
            var verified = HandleScopeReleaseAuthorizationPolicy.VerifyInstalled(
                descriptorBytes,
                _keyProvider);
            VerifyManifestBinding(verified.Descriptor, manifestBytes.Span);

            EnsureAuthorizationStorageIsSafe();
            WriteAtomic(_descriptorPath, descriptorBytes.Span);
            WriteAtomic(_manifestPath, manifestBytes.Span);
            File.Delete(_receiptPath);
        }
        catch (HandleScopeInstallException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or
                ArgumentException or NotSupportedException or
                CryptographicException)
        {
            throw new HandleScopeInstallException(
                "The verified HandleScope installed-file authorization could not be saved.",
                exception);
        }
    }

    internal void PersistGitHubReleaseAuthorization(
        HandleScopeReleaseIdentity release,
        ReadOnlyMemory<byte> manifestBytes)
    {
        ArgumentNullException.ThrowIfNull(release);
        try
        {
            ValidateGitHubRelease(release);
            _ = HandleScopeReleaseAuthorizationPolicy.GetAuthorizedApiHash(
                manifestBytes.Span);
            var receipt = new HandleScopeGitHubReleaseReceipt(
                ReceiptSchemaVersion,
                HandleScopeReleaseAuthorizationPolicy.Repository,
                release.Version,
                release.TagName,
                release.Package.Name,
                release.Package.Size,
                Convert.ToHexString(release.Package.Sha256),
                release.Checksums.Name,
                release.Checksums.Size,
                Convert.ToHexString(release.Checksums.Sha256),
                Convert.ToHexString(SHA256.HashData(manifestBytes.Span)));
            var receiptBytes = JsonSerializer.SerializeToUtf8Bytes(
                receipt,
                ReceiptJsonOptions);

            EnsureAuthorizationStorageIsSafe();
            WriteAtomic(_receiptPath, receiptBytes);
            WriteAtomic(_manifestPath, manifestBytes.Span);
            File.Delete(_descriptorPath);
        }
        catch (HandleScopeInstallException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or
                ArgumentException or NotSupportedException or
                CryptographicException or JsonException)
        {
            throw new HandleScopeInstallException(
                "The verified HandleScope GitHub release receipt could not be saved.",
                exception);
        }
    }

    public bool IsAuthorized(string executablePath)
    {
        try
        {
            if (!HandleScopePathSecurity.IsSafeExistingPath(
                    _localAppDataRoot,
                    _authorizationRoot,
                    targetMustExist: true,
                    _isReparsePoint) ||
                !HandleScopePathSecurity.IsSafeExistingPath(
                    _localAppDataRoot,
                    _manifestPath,
                    targetMustExist: true,
                    _isReparsePoint))
            {
                return false;
            }

            var manifestBytes = ReadBounded(
                _manifestPath,
                MaximumManifestBytes);
            var hasDescriptor = File.Exists(_descriptorPath);
            var hasReceipt = File.Exists(_receiptPath);
            if (hasDescriptor == hasReceipt)
                return false;

            byte[] expectedApiHash;
            if (hasDescriptor)
            {
                if (!HandleScopePathSecurity.IsSafeExistingPath(
                        _localAppDataRoot,
                        _descriptorPath,
                        targetMustExist: true,
                        _isReparsePoint))
                {
                    return false;
                }
                var descriptorBytes = ReadBounded(
                    _descriptorPath,
                    HandleScopeReleaseAuthorizationPolicy.MaximumDescriptorBytes);
                var verified =
                    HandleScopeReleaseAuthorizationPolicy.VerifyInstalled(
                        descriptorBytes,
                        _keyProvider);
                expectedApiHash = VerifyManifestBinding(
                    verified.Descriptor,
                    manifestBytes);
            }
            else
            {
                if (!HandleScopePathSecurity.IsSafeExistingPath(
                        _localAppDataRoot,
                        _receiptPath,
                        targetMustExist: true,
                        _isReparsePoint))
                {
                    return false;
                }
                var receiptBytes = ReadBounded(
                    _receiptPath,
                    MaximumReceiptBytes);
                expectedApiHash = VerifyGitHubReceiptBinding(
                    receiptBytes,
                    manifestBytes);
            }

            using var stream = new FileStream(
                executablePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 128 * 1024,
                FileOptions.SequentialScan);
            return CryptographicOperations.FixedTimeEquals(
                SHA256.HashData(stream),
                expectedApiHash);
        }
        catch (Exception exception) when (
            exception is HandleScopeInstallException or IOException or
                UnauthorizedAccessException or ArgumentException or
                NotSupportedException or CryptographicException)
        {
            return false;
        }
    }

    private void EnsureAuthorizationStorageIsSafe()
    {
        var parent = Path.GetDirectoryName(_authorizationRoot)
            ?? throw new HandleScopeInstallException(
                "The HandleScope authorization directory is invalid.");
        if (!HandleScopePathSecurity.IsSafeExistingPath(
                _localAppDataRoot,
                parent,
                targetMustExist: true,
                _isReparsePoint))
        {
            throw new HandleScopeInstallException(
                "The SessionDock data directory cannot safely store HandleScope authorization.");
        }
        Directory.CreateDirectory(_authorizationRoot);
        if (!HandleScopePathSecurity.IsSafeExistingPath(
                _localAppDataRoot,
                _authorizationRoot,
                targetMustExist: true,
                _isReparsePoint))
        {
            throw new HandleScopeInstallException(
                "The HandleScope authorization directory is linked or redirected.");
        }
    }

    private static byte[] VerifyManifestBinding(
        HandleScopeReleaseDescriptor descriptor,
        ReadOnlySpan<byte> manifestBytes)
    {
        var signedManifestHash =
            HandleScopeReleaseAuthorizationPolicy.ParseSha256(
                descriptor.InternalManifestSha256);
        if (!CryptographicOperations.FixedTimeEquals(
                SHA256.HashData(manifestBytes),
                signedManifestHash))
        {
            throw new HandleScopeInstallException(
                "The installed HandleScope inventory does not match its signed release authorization.");
        }
        return HandleScopeReleaseAuthorizationPolicy.GetAuthorizedApiHash(
            manifestBytes);
    }

    private static byte[] VerifyGitHubReceiptBinding(
        ReadOnlyMemory<byte> receiptBytes,
        ReadOnlySpan<byte> manifestBytes)
    {
        var receipt = DeserializeGitHubReceipt(receiptBytes);
        var expectedManifestHash =
            HandleScopeReleaseAuthorizationPolicy.ParseSha256(
                receipt.ManifestSha256);
        if (!CryptographicOperations.FixedTimeEquals(
                SHA256.HashData(manifestBytes),
                expectedManifestHash))
        {
            throw new HandleScopeInstallException(
                "The installed HandleScope inventory does not match its verified GitHub release receipt.");
        }
        return HandleScopeReleaseAuthorizationPolicy.GetAuthorizedApiHash(
            manifestBytes);
    }

    private static HandleScopeGitHubReleaseReceipt DeserializeGitHubReceipt(
        ReadOnlyMemory<byte> bytes)
    {
        if (bytes.Length is <= 0 or > MaximumReceiptBytes ||
            (bytes.Length >= 3 && bytes.Span[0] == 0xEF &&
             bytes.Span[1] == 0xBB && bytes.Span[2] == 0xBF))
        {
            throw new HandleScopeInstallException(
                "The HandleScope GitHub release receipt is invalid.");
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
                root.GetPropertyCount() != ReceiptProperties.Length)
            {
                throw new HandleScopeInstallException(
                    "The HandleScope GitHub release receipt is invalid.");
            }
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in root.EnumerateObject())
            {
                if (!names.Add(property.Name))
                {
                    throw new HandleScopeInstallException(
                        "The HandleScope GitHub release receipt is invalid.");
                }
            }
            if (!names.SetEquals(ReceiptProperties))
            {
                throw new HandleScopeInstallException(
                    "The HandleScope GitHub release receipt is invalid.");
            }

            var receipt = JsonSerializer.Deserialize<HandleScopeGitHubReleaseReceipt>(
                bytes.Span,
                ReceiptJsonOptions) ?? throw new HandleScopeInstallException(
                    "The HandleScope GitHub release receipt is invalid.");
            ValidateGitHubReceipt(receipt);
            return receipt;
        }
        catch (Exception exception) when (
            exception is DecoderFallbackException or JsonException)
        {
            throw new HandleScopeInstallException(
                "The HandleScope GitHub release receipt is invalid.",
                exception);
        }
    }

    private static void ValidateGitHubRelease(HandleScopeReleaseIdentity release)
    {
        var receipt = new HandleScopeGitHubReleaseReceipt(
            ReceiptSchemaVersion,
            HandleScopeReleaseAuthorizationPolicy.Repository,
            release.Version,
            release.TagName,
            release.Package.Name,
            release.Package.Size,
            Convert.ToHexString(release.Package.Sha256),
            release.Checksums.Name,
            release.Checksums.Size,
            Convert.ToHexString(release.Checksums.Sha256),
            new string('0', 64));
        ValidateGitHubReceipt(receipt);
    }

    private static void ValidateGitHubReceipt(
        HandleScopeGitHubReleaseReceipt receipt)
    {
        var strings = new[]
        {
            receipt.Repository, receipt.Version, receipt.Tag,
            receipt.PackageFile, receipt.PackageSha256,
            receipt.ChecksumFile, receipt.ChecksumSha256,
            receipt.ManifestSha256
        };
        if (receipt.SchemaVersion != ReceiptSchemaVersion ||
            strings.Any(value =>
                string.IsNullOrWhiteSpace(value) || value.Length > 512 ||
                value.Any(char.IsControl)) ||
            receipt.Repository != HandleScopeReleaseAuthorizationPolicy.Repository ||
            !Version.TryParse(receipt.Version, out var version) ||
            version.Build < 0 || version.Revision >= 0 ||
            version.ToString(3) != receipt.Version ||
            receipt.Tag != $"v{receipt.Version}" ||
            receipt.PackageFile !=
                $"HandleScope-{receipt.Version}-win-x64.zip" ||
            receipt.PackageSize is <= 0 or >
                HandleScopeReleasePolicy.MaximumPackageBytes ||
            receipt.ChecksumFile !=
                HandleScopeReleaseAuthorizationPolicy.ChecksumFileName ||
            receipt.ChecksumSize is <= 0 or >
                HandleScopeReleasePolicy.MaximumChecksumBytes)
        {
            throw new HandleScopeInstallException(
                "The HandleScope GitHub release receipt is invalid.");
        }
        _ = HandleScopeReleaseAuthorizationPolicy.ParseSha256(
            receipt.PackageSha256);
        _ = HandleScopeReleaseAuthorizationPolicy.ParseSha256(
            receipt.ChecksumSha256);
        _ = HandleScopeReleaseAuthorizationPolicy.ParseSha256(
            receipt.ManifestSha256);
    }

    private static byte[] ReadBounded(string path, int maximumBytes)
    {
        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read);
        if (stream.Length is <= 0 || stream.Length > maximumBytes)
            throw new InvalidDataException("A HandleScope authorization file is oversized.");
        using var output = new MemoryStream();
        stream.CopyTo(output);
        return output.ToArray();
    }

    private static void WriteAtomic(string targetPath, ReadOnlySpan<byte> contents)
    {
        var temporaryPath = targetPath + "." +
            Convert.ToHexString(RandomNumberGenerator.GetBytes(16)) + ".tmp";
        try
        {
            using (var stream = new FileStream(
                       temporaryPath,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None))
            {
                stream.Write(contents);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporaryPath, targetPath, overwrite: true);
        }
        finally
        {
            try { File.Delete(temporaryPath); }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException)
            {
                // A temporary file contains only public release metadata.
            }
        }
    }
}

internal sealed record HandleScopeGitHubReleaseReceipt(
    int SchemaVersion,
    string Repository,
    string Version,
    string Tag,
    string PackageFile,
    long PackageSize,
    string PackageSha256,
    string ChecksumFile,
    long ChecksumSize,
    string ChecksumSha256,
    string ManifestSha256);
