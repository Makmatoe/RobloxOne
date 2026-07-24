using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace SessionDock.Services;

internal static class WindowsExecutableTrust
{
    private static readonly WindowsExecutableTrustVerifier Verifier =
        new(new WinTrustNativeVerifier());

    public static bool TryGetTrustedSigner(
        string path,
        out TrustedWindowsSigner signer,
        bool forceRefresh = false) =>
        Verifier.TryGetTrustedSigner(path, forceRefresh, out signer);
}

internal sealed class WindowsExecutableTrustVerifier
{
    internal static readonly TimeSpan SuccessfulValidationLifetime =
        TimeSpan.FromMinutes(10);
    private const int MaximumCacheEntries = 64;

    private readonly IWindowsTrustNativeVerifier _nativeVerifier;
    private readonly Func<string, WindowsExecutableFileIdentity?> _getFileIdentity;
    private readonly Func<DateTimeOffset> _getUtcNow;
    private readonly object _cacheLock = new();
    private readonly Dictionary<WindowsExecutableFileIdentity, CacheEntry> _cache = [];

    internal WindowsExecutableTrustVerifier(
        IWindowsTrustNativeVerifier nativeVerifier,
        Func<string, WindowsExecutableFileIdentity?>? getFileIdentity = null,
        Func<DateTimeOffset>? getUtcNow = null)
    {
        _nativeVerifier = nativeVerifier ??
            throw new ArgumentNullException(nameof(nativeVerifier));
        _getFileIdentity = getFileIdentity ?? GetFileIdentity;
        _getUtcNow = getUtcNow ?? (() => DateTimeOffset.UtcNow);
    }

    internal bool TryGetTrustedSigner(
        string path,
        bool forceRefresh,
        out TrustedWindowsSigner signer)
    {
        signer = TrustedWindowsSigner.Empty;
        WindowsExecutableFileIdentity? identity;
        try
        {
            identity = _getFileIdentity(Path.GetFullPath(path));
        }
        catch (Exception exception) when (
            exception is ArgumentException or IOException or
                NotSupportedException or UnauthorizedAccessException or
                CryptographicException)
        {
            return false;
        }
        if (identity is null)
            return false;

        var now = _getUtcNow();
        if (!forceRefresh)
        {
            lock (_cacheLock)
            {
                RemoveExpiredEntriesNoLock(now);
                if (_cache.TryGetValue(identity, out var cached))
                {
                    signer = cached.Signer;
                    return true;
                }
            }
        }

        WindowsTrustNativeResult result;
        try
        {
            result = _nativeVerifier.Verify(identity.CanonicalPath);
        }
        catch (Exception exception) when (
            exception is ArgumentException or IOException or
                UnauthorizedAccessException or InvalidOperationException or
                CryptographicException or ExternalException or
                DllNotFoundException or EntryPointNotFoundException)
        {
            return false;
        }
        if (result.Status != WindowsTrustStatus.Trusted ||
            string.IsNullOrWhiteSpace(result.Signer.Subject) ||
            string.IsNullOrWhiteSpace(result.Signer.SimpleName))
        {
            return false;
        }

        signer = result.Signer;
        lock (_cacheLock)
        {
            RemoveExpiredEntriesNoLock(now);
            if (_cache.Count >= MaximumCacheEntries)
            {
                var oldest = _cache.MinBy(pair => pair.Value.ValidatedAtUtc).Key;
                _cache.Remove(oldest);
            }
            _cache[identity] = new CacheEntry(signer, now);
        }
        return true;
    }

    private void RemoveExpiredEntriesNoLock(DateTimeOffset now)
    {
        foreach (var key in _cache
                     .Where(pair => now - pair.Value.ValidatedAtUtc >
                         SuccessfulValidationLifetime)
                     .Select(pair => pair.Key)
                     .ToArray())
        {
            _cache.Remove(key);
        }
    }

    private static WindowsExecutableFileIdentity? GetFileIdentity(string path)
    {
        var info = new FileInfo(path);
        if (!info.Exists || info.Length <= 0)
            return null;
        using var stream = new FileStream(
            info.FullName,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 128 * 1024,
            FileOptions.SequentialScan);
        var hash = Convert.ToHexString(SHA256.HashData(stream));
        return new WindowsExecutableFileIdentity(
            Path.GetFullPath(info.FullName),
            info.Length,
            info.LastWriteTimeUtc.Ticks,
            hash);
    }

    private sealed record CacheEntry(
        TrustedWindowsSigner Signer,
        DateTimeOffset ValidatedAtUtc);
}

internal interface IWindowsTrustNativeVerifier
{
    WindowsTrustNativeResult Verify(string path);
}

internal sealed class WinTrustNativeVerifier : IWindowsTrustNativeVerifier
{
    private static readonly Guid WinTrustActionGenericVerifyV2 =
        new("00AAC56B-CD44-11D0-8CC2-00C04FC295EE");

    public WindowsTrustNativeResult Verify(string path)
    {
        var statusCode = VerifyEmbeddedSignature(path);
        var status = MapStatus(statusCode);
        if (status != WindowsTrustStatus.Trusted)
            return new(status, TrustedWindowsSigner.Empty, statusCode);

#pragma warning disable SYSLIB0057 // Authenticode signer extraction requires this Windows API.
        using var signingCertificate = X509Certificate.CreateFromSignedFile(path);
#pragma warning restore SYSLIB0057
        using var certificate = X509CertificateLoader.LoadCertificate(
            signingCertificate.GetRawCertData());
        var signer = new TrustedWindowsSigner(
            certificate.Subject,
            certificate.GetNameInfo(X509NameType.SimpleName, forIssuer: false));
        return new(WindowsTrustStatus.Trusted, signer, statusCode);
    }

    internal static WindowsTrustStatus MapStatus(int statusCode) => statusCode switch
    {
        0 => WindowsTrustStatus.Trusted,
        unchecked((int)0x80092010) => WindowsTrustStatus.Revoked,
        unchecked((int)0x80092012) => WindowsTrustStatus.RevocationUnknown,
        unchecked((int)0x80092013) => WindowsTrustStatus.RevocationOffline,
        unchecked((int)0x800B010E) => WindowsTrustStatus.RevocationUnknown,
        _ => WindowsTrustStatus.Untrusted
    };

    private static int VerifyEmbeddedSignature(string path)
    {
        var fileInfo = new WinTrustFileInfo(path);
        var fileInfoPointer = Marshal.AllocHGlobal(
            Marshal.SizeOf<WinTrustFileInfo>());
        var trustDataPointer = IntPtr.Zero;
        try
        {
            Marshal.StructureToPtr(fileInfo, fileInfoPointer, fDeleteOld: false);
            var trustData = new WinTrustData(fileInfoPointer);
            trustDataPointer = Marshal.AllocHGlobal(
                Marshal.SizeOf<WinTrustData>());
            Marshal.StructureToPtr(trustData, trustDataPointer, fDeleteOld: false);
            return WinVerifyTrust(
                IntPtr.Zero,
                WinTrustActionGenericVerifyV2,
                trustDataPointer);
        }
        finally
        {
            if (trustDataPointer != IntPtr.Zero)
                Marshal.FreeHGlobal(trustDataPointer);
            Marshal.FreeHGlobal(fileInfoPointer);
        }
    }

    [DllImport("wintrust.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int WinVerifyTrust(
        IntPtr windowHandle,
        [MarshalAs(UnmanagedType.LPStruct)] Guid actionId,
        IntPtr trustData);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private readonly struct WinTrustFileInfo
    {
        private readonly uint StructSize;
        [MarshalAs(UnmanagedType.LPWStr)]
        private readonly string FilePath;
        private readonly IntPtr FileHandle;
        private readonly IntPtr KnownSubject;

        public WinTrustFileInfo(string filePath)
        {
            StructSize = (uint)Marshal.SizeOf<WinTrustFileInfo>();
            FilePath = filePath;
            FileHandle = IntPtr.Zero;
            KnownSubject = IntPtr.Zero;
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private readonly struct WinTrustData
    {
        private readonly uint StructSize;
        private readonly IntPtr PolicyCallbackData;
        private readonly IntPtr SipClientData;
        private readonly WinTrustUiChoice UiChoice;
        private readonly WinTrustRevocationChecks RevocationChecks;
        private readonly WinTrustUnionChoice UnionChoice;
        private readonly IntPtr FileInfo;
        private readonly WinTrustStateAction StateAction;
        private readonly IntPtr StateData;
        private readonly IntPtr UrlReference;
        private readonly WinTrustProviderFlags ProviderFlags;
        private readonly uint UiContext;
        private readonly IntPtr SignatureSettings;

        public WinTrustData(IntPtr fileInfo)
        {
            StructSize = (uint)Marshal.SizeOf<WinTrustData>();
            PolicyCallbackData = IntPtr.Zero;
            SipClientData = IntPtr.Zero;
            UiChoice = WinTrustUiChoice.None;
            RevocationChecks = WinTrustRevocationChecks.WholeChain;
            UnionChoice = WinTrustUnionChoice.File;
            FileInfo = fileInfo;
            StateAction = WinTrustStateAction.Ignore;
            StateData = IntPtr.Zero;
            UrlReference = IntPtr.Zero;
            ProviderFlags =
                WinTrustProviderFlags.RevocationCheckChainExcludeRoot |
                WinTrustProviderFlags.DisableMd2Md4;
            UiContext = 0;
            SignatureSettings = IntPtr.Zero;
        }
    }
}

internal enum WinTrustUiChoice : uint
{
    None = 2
}

internal enum WinTrustRevocationChecks : uint
{
    WholeChain = 1
}

internal enum WinTrustUnionChoice : uint
{
    File = 1
}

internal enum WinTrustStateAction : uint
{
    Ignore = 0
}

[Flags]
internal enum WinTrustProviderFlags : uint
{
    RevocationCheckChainExcludeRoot = 0x00000080,
    DisableMd2Md4 = 0x00002000
}

internal enum WindowsTrustStatus
{
    Trusted,
    Revoked,
    RevocationUnknown,
    RevocationOffline,
    Untrusted
}

internal sealed record WindowsTrustNativeResult(
    WindowsTrustStatus Status,
    TrustedWindowsSigner Signer,
    int NativeStatusCode);

internal sealed record WindowsExecutableFileIdentity(
    string CanonicalPath,
    long Length,
    long LastWriteTimeUtcTicks,
    string Sha256);

internal sealed record TrustedWindowsSigner(string Subject, string SimpleName)
{
    public static TrustedWindowsSigner Empty { get; } = new(string.Empty, string.Empty);
}
