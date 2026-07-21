using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using RobloxOne.ReleaseTrust;
using Velopack;
using Velopack.Locators;
using Velopack.Sources;

namespace RobloxOneLauncher.Services;

internal sealed class RobloxUpdateService : IDisposable
{
    private const string RepositoryUrl = "https://github.com/Makmatoe/RobloxOne";
    private const string PublicKeyResourceName =
        "RobloxOneLauncher.Embedded.ReleasePublicKey.pem";
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private readonly UpdateManager _manager;
    private readonly HttpClient _httpClient;

    public RobloxUpdateService()
    {
        var source = new GithubSource(
            RepositoryUrl,
            accessToken: null,
            prerelease: false);
        _manager = new UpdateManager(
            source,
            new UpdateOptions
            {
                AllowVersionDowngrade = false,
                ExplicitChannel = ReleaseDescriptorPolicy.Channel,
                MaximumDeltasBeforeFallback = 10
            });

        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = true,
            MaxAutomaticRedirections = 3,
            AutomaticDecompression =
                DecompressionMethods.GZip | DecompressionMethods.Deflate,
            UseCookies = false
        };
        _httpClient = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(20)
        };
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("RobloxOne/2.1");
        _httpClient.DefaultRequestHeaders.Accept.ParseAdd("application/json");
    }

    public bool CanSelfUpdate => _manager.IsInstalled && !_manager.IsPortable;

    public string CurrentVersion =>
        _manager.CurrentVersion?.ToString() ??
        (Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "unknown");

    public VelopackAsset? PendingUpdate =>
        CanSelfUpdate ? _manager.UpdatePendingRestart : null;

    public async Task<AvailableRobloxUpdate?> CheckAsync(
        CancellationToken cancellationToken)
    {
        if (!CanSelfUpdate)
            return null;

        var update = await _manager.CheckForUpdatesAsync();
        if (update is null)
            return null;

        var verified = await FetchAndVerifyDescriptorAsync(
            update.TargetFullRelease,
            cancellationToken);
        return new AvailableRobloxUpdate(update, verified);
    }

    public async Task<VerifiedReleaseDescriptor> VerifyPendingAsync(
        VelopackAsset pending,
        CancellationToken cancellationToken)
    {
        var verified = await FetchAndVerifyDescriptorAsync(
            pending,
            cancellationToken);
        await VerifyPreparedPackageAsync(pending, verified, cancellationToken);
        return verified;
    }

    public async Task DownloadAsync(
        AvailableRobloxUpdate update,
        Action<int> progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(update);
        ArgumentNullException.ThrowIfNull(progress);
        await _manager.DownloadUpdatesAsync(
            update.UpdateInfo,
            progress,
            cancellationToken);
        await VerifyPreparedPackageAsync(
            update.UpdateInfo.TargetFullRelease,
            update.Release,
            cancellationToken);
    }

    public void ApplyAfterExit(VelopackAsset asset)
    {
        ArgumentNullException.ThrowIfNull(asset);
        _manager.WaitExitThenApplyUpdates(
            asset,
            silent: false,
            restart: true);
    }

    public void Dispose() => _httpClient.Dispose();

    private async Task<VerifiedReleaseDescriptor> FetchAndVerifyDescriptorAsync(
        VelopackAsset asset,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(asset);
        var version = asset.Version.ToString();
        var tag = $"v{version}";
        var descriptorUrl = new Uri(
            $"{RepositoryUrl}/releases/download/{Uri.EscapeDataString(tag)}/" +
            ReleaseDescriptorPolicy.DescriptorFileName);
        using var request = new HttpRequestMessage(HttpMethod.Get, descriptorUrl);
        using var response = await _httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new ReleaseTrustException(
                "The signed release details could not be retrieved from GitHub.");
        }

        if (response.Content.Headers.ContentLength is <= 0 or
            > ReleaseDescriptorPolicy.MaximumDescriptorBytes)
        {
            throw new ReleaseTrustException(
                "GitHub returned release details with an invalid size.");
        }

        var json = await ReadBoundedUtf8Async(response, cancellationToken);
        var identity = new ReleaseAssetIdentity(
            version,
            asset.FileName,
            asset.Size,
            asset.SHA256 ?? string.Empty);
        return ReleaseDescriptorPolicy.Verify(
            json,
            identity,
            ReadEmbeddedPublicKey());
    }

    private static async Task<string> ReadBoundedUtf8Async(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        await using var input = await response.Content.ReadAsStreamAsync(
            cancellationToken);
        using var output = new MemoryStream();
        var buffer = new byte[4096];
        while (true)
        {
            var read = await input.ReadAsync(buffer, cancellationToken);
            if (read == 0)
                break;
            if (output.Length + read > ReleaseDescriptorPolicy.MaximumDescriptorBytes)
                throw new ReleaseTrustException("The release details exceeded the size limit.");
            output.Write(buffer, 0, read);
        }

        try
        {
            return StrictUtf8.GetString(output.ToArray());
        }
        catch (DecoderFallbackException exception)
        {
            throw new ReleaseTrustException(
                "The release details were not valid UTF-8.",
                exception);
        }
    }

    private static string ReadEmbeddedPublicKey()
    {
        using var stream = typeof(RobloxUpdateService).Assembly
            .GetManifestResourceStream(PublicKeyResourceName)
            ?? throw new ReleaseTrustException(
                "The built-in Roblox One release key is unavailable.");
        using var reader = new StreamReader(stream, StrictUtf8);
        return reader.ReadToEnd();
    }

    private async Task VerifyPreparedPackageAsync(
        VelopackAsset asset,
        VerifiedReleaseDescriptor release,
        CancellationToken cancellationToken)
    {
        var currentExecutable = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(currentExecutable) ||
            !WindowsExecutableTrust.TryGetTrustedSigner(
                currentExecutable,
                out var currentSigner))
        {
            throw new ReleaseTrustException(
                "The installed Roblox One publisher signature could not be verified.");
        }

        var locator = VelopackLocator.Current;
        if (string.IsNullOrWhiteSpace(locator.PackagesDir))
        {
            throw new ReleaseTrustException(
                "The installed update package directory is unavailable.");
        }

        var packagesRoot = Path.GetFullPath(locator.PackagesDir)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) +
            Path.DirectorySeparatorChar;
        var packagePath = Path.GetFullPath(
            Path.Combine(locator.PackagesDir, asset.FileName));
        if (!packagePath.StartsWith(
                packagesRoot,
                StringComparison.OrdinalIgnoreCase) ||
            !File.Exists(packagePath))
        {
            throw new ReleaseTrustException(
                "The downloaded update package could not be located safely.");
        }

        var descriptor = release.Descriptor;
        var packageInfo = new FileInfo(packagePath);
        if (packageInfo.Length != descriptor.PackageSize)
            throw new ReleaseTrustException("The downloaded package size changed.");

        await using var packageStream = new FileStream(
            packagePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 128 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var actualHash = await SHA256.HashDataAsync(
            packageStream,
            cancellationToken);
        if (!CryptographicOperations.FixedTimeEquals(
                actualHash,
                Convert.FromHexString(descriptor.PackageSha256)))
        {
            throw new ReleaseTrustException(
                "The downloaded package failed its signed SHA-256 check.");
        }

        packageStream.Position = 0;
        using var archive = new ZipArchive(
            packageStream,
            ZipArchiveMode.Read,
            leaveOpen: true);
        var applicationEntries = archive.Entries
            .Where(entry => Path.GetFileName(entry.FullName).Equals(
                "RobloxOne.exe",
                StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (applicationEntries.Length != 1 ||
            applicationEntries[0].Length is < 1024 * 1024 or
            > ReleaseDescriptorPolicy.MaximumPackageSize)
        {
            throw new ReleaseTrustException(
                "The downloaded package does not contain one valid Roblox One executable.");
        }

        var verificationDirectory = Path.Combine(
            Path.GetTempPath(),
            $"RobloxOne-UpdateVerify-{Guid.NewGuid():N}");
        Directory.CreateDirectory(verificationDirectory);
        var verificationPath = Path.Combine(
            verificationDirectory,
            "RobloxOne.exe");
        try
        {
            await using var input = applicationEntries[0].Open();
            await using var output = new FileStream(
                verificationPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.Read,
                bufferSize: 128 * 1024,
                FileOptions.Asynchronous | FileOptions.WriteThrough);
            await input.CopyToAsync(output, cancellationToken);
            await output.FlushAsync(cancellationToken);

            var fileVersion = FileVersionInfo.GetVersionInfo(
                verificationPath).FileVersion;
            if (!Version.TryParse(fileVersion, out var executableVersion) ||
                executableVersion.Major != release.Version.Major ||
                executableVersion.Minor != release.Version.Minor ||
                executableVersion.Build != release.Version.Build)
            {
                throw new ReleaseTrustException(
                    "The downloaded executable version does not match the signed release.");
            }

            if (!WindowsExecutableTrust.TryGetTrustedSigner(
                    verificationPath,
                    out var updateSigner) ||
                !updateSigner.Subject.Equals(
                    currentSigner.Subject,
                    StringComparison.Ordinal))
            {
                throw new ReleaseTrustException(
                    "The downloaded executable is not signed by the installed Roblox One publisher.");
            }
        }
        finally
        {
            try
            {
                Directory.Delete(verificationDirectory, recursive: true);
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException)
            {
                Trace.WriteLine(
                    $"Update verification cleanup failed: {exception.GetType().Name}.");
            }
        }
    }
}

internal sealed record AvailableRobloxUpdate(
    UpdateInfo UpdateInfo,
    VerifiedReleaseDescriptor Release);
