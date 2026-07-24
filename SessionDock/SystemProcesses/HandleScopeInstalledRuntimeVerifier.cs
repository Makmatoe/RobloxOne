using System.IO;
using System.Security.Cryptography;

namespace SessionDock.SystemProcesses;

internal interface IHandleScopeInstalledRuntimeVerifier
{
    bool IsAuthorized(string executablePath);
}

internal sealed class HandleScopeInstalledRuntimeVerifier :
    IHandleScopeInstalledRuntimeVerifier
{
    private const int MaximumManifestBytes = 1024 * 1024;
    private const string AuthorizationDirectoryName =
        "HandleScopeAuthorization";
    private const string ManifestFileName = "CONTENTS.sha256";

    private readonly string _localAppDataRoot;
    private readonly string _authorizationRoot;
    private readonly string _descriptorPath;
    private readonly string _manifestPath;
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

            WriteAtomic(_descriptorPath, descriptorBytes.Span);
            WriteAtomic(_manifestPath, manifestBytes.Span);
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
                    _descriptorPath,
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

            var descriptorBytes = ReadBounded(
                _descriptorPath,
                HandleScopeReleaseAuthorizationPolicy.MaximumDescriptorBytes);
            var manifestBytes = ReadBounded(
                _manifestPath,
                MaximumManifestBytes);
            var verified = HandleScopeReleaseAuthorizationPolicy.VerifyInstalled(
                descriptorBytes,
                _keyProvider);
            var expectedApiHash = VerifyManifestBinding(
                verified.Descriptor,
                manifestBytes);

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
                // A temporary file contains only public signed metadata.
            }
        }
    }
}
