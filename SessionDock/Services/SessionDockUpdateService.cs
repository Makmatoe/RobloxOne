using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using SessionDock.ReleaseTrust;
using Velopack;
using Velopack.Locators;
using Velopack.Sources;

namespace SessionDock.Services;

internal sealed class SessionDockUpdateService : IDisposable
{
    private const string RepositoryUrl = "https://github.com/Makmatoe/SessionDock";
    private const string PublicKeyResourceName =
        "SessionDock.Embedded.ReleasePublicKey.pem";
    private const int MaximumManifestRedirects = 3;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private readonly UpdateManager _manager;
    private readonly HttpClient _httpClient;

    public SessionDockUpdateService()
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
            AllowAutoRedirect = false,
            AutomaticDecompression =
                DecompressionMethods.GZip | DecompressionMethods.Deflate,
            UseCookies = false
        };
        _httpClient = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(20)
        };
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("SessionDock/2.1");
        _httpClient.DefaultRequestHeaders.Accept.ParseAdd("application/json");
    }

    public bool CanSelfUpdate => _manager.IsInstalled && !_manager.IsPortable;

    public string CurrentVersion =>
        _manager.CurrentVersion?.ToString() ??
        (Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "unknown");

    public VelopackAsset? PendingUpdate =>
        CanSelfUpdate ? _manager.UpdatePendingRestart : null;

    public async Task<AvailableSessionDockUpdate?> CheckAsync(
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
        return new AvailableSessionDockUpdate(update, verified);
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
        AvailableSessionDockUpdate update,
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
        using var response = await SendManifestRequestAsync(
            descriptorUrl,
            cancellationToken);
        if (response.StatusCode != HttpStatusCode.OK)
        {
            throw new ReleaseTrustException(
                "The signed release descriptor could not be retrieved from GitHub.");
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

    private async Task<HttpResponseMessage> SendManifestRequestAsync(
        Uri initialUri,
        CancellationToken cancellationToken)
    {
        var currentUri = initialUri;
        for (var redirect = 0; redirect <= MaximumManifestRedirects; redirect++)
        {
            if (!IsAllowedManifestUri(currentUri, initialUri))
            {
                throw new ReleaseTrustException(
                    "GitHub redirected the release manifest to an untrusted address.");
            }

            using var request = new HttpRequestMessage(HttpMethod.Get, currentUri);
            var response = await _httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);
            if (response.StatusCode is not (
                    HttpStatusCode.MovedPermanently or
                    HttpStatusCode.Redirect or
                    HttpStatusCode.RedirectMethod or
                    HttpStatusCode.TemporaryRedirect or
                    HttpStatusCode.PermanentRedirect))
            {
                return response;
            }

            var location = response.Headers.Location;
            response.Dispose();
            if (location is null || redirect == MaximumManifestRedirects)
            {
                throw new ReleaseTrustException(
                    "GitHub returned an invalid release-manifest redirect.");
            }

            currentUri = location.IsAbsoluteUri
                ? location
                : new Uri(currentUri, location);
        }

        throw new ReleaseTrustException(
            "GitHub returned too many release-manifest redirects.");
    }

    internal static bool IsAllowedManifestUri(Uri value, Uri initialUri)
    {
        if (!value.IsAbsoluteUri ||
            value.Scheme != Uri.UriSchemeHttps ||
            !value.IsDefaultPort ||
            !string.IsNullOrEmpty(value.UserInfo) ||
            !string.IsNullOrEmpty(value.Fragment))
        {
            return false;
        }

        if (value.Equals(initialUri))
            return true;

        return value.Host.Equals(
                "release-assets.githubusercontent.com",
                StringComparison.OrdinalIgnoreCase) ||
            value.Host.Equals(
                "objects.githubusercontent.com",
                StringComparison.OrdinalIgnoreCase);
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
        using var stream = typeof(SessionDockUpdateService).Assembly
            .GetManifestResourceStream(PublicKeyResourceName)
            ?? throw new ReleaseTrustException(
                "The built-in SessionDock release key is unavailable.");
        using var reader = new StreamReader(stream, StrictUtf8);
        return reader.ReadToEnd();
    }

    private async Task VerifyPreparedPackageAsync(
        VelopackAsset asset,
        VerifiedReleaseDescriptor release,
        CancellationToken cancellationToken)
    {
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
        var useCurrentLayout = ReleaseDescriptorPolicy.IsCurrentIdentity(
            release.Descriptor);
        ReleasePackagePolicy.ValidateEntries(
            archive.Entries.Select(entry =>
                new ReleasePackageEntryIdentity(
                    entry.FullName,
                    entry.Length,
                    entry.CompressedLength)),
            useCurrentLayout);
        ValidatePackageMetadata(archive, release, useCurrentLayout);

        var verificationDirectory = Path.Combine(
            Path.GetTempPath(),
            $"SessionDock-UpdateVerify-{Guid.NewGuid():N}");
        Directory.CreateDirectory(verificationDirectory);
        try
        {
            foreach (var entryName in ReleasePackagePolicy.GetExecutableEntryNames(
                         useCurrentLayout))
            {
                var entry = archive.GetEntry(entryName) ??
                    throw new ReleaseTrustException(
                        "The downloaded package is missing an executable payload.");
                var verificationPath = Path.Combine(
                    verificationDirectory,
                    Path.GetFileName(entryName));
                await using (var input = entry.Open())
                await using (var output = new FileStream(
                    verificationPath,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.Read,
                    bufferSize: 128 * 1024,
                    FileOptions.Asynchronous | FileOptions.WriteThrough))
                {
                    await input.CopyToAsync(output, cancellationToken);
                    await output.FlushAsync(cancellationToken);
                }

                var mainExecutable = useCurrentLayout
                    ? "lib/app/SessionDock.exe"
                    : "lib/app/RobloxOne.exe";
                if (entryName.Equals(mainExecutable, StringComparison.Ordinal))
                {
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
                }

                ValidatePortableExecutable(verificationPath);
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

    private static void ValidatePortableExecutable(string path)
    {
        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 4096,
            FileOptions.SequentialScan);
        Span<byte> header = stackalloc byte[64];
        if (stream.Read(header) != header.Length ||
            header[0] != (byte)'M' ||
            header[1] != (byte)'Z')
        {
            throw new ReleaseTrustException(
                "The downloaded package contains an invalid executable payload.");
        }

        var peOffset = BitConverter.ToInt32(header[60..64]);
        if (peOffset < header.Length || peOffset > stream.Length - 4)
        {
            throw new ReleaseTrustException(
                "The downloaded package contains an invalid executable payload.");
        }

        stream.Position = peOffset;
        Span<byte> signature = stackalloc byte[4];
        if (stream.Read(signature) != signature.Length ||
            signature[0] != (byte)'P' ||
            signature[1] != (byte)'E' ||
            signature[2] != 0 ||
            signature[3] != 0)
        {
            throw new ReleaseTrustException(
                "The downloaded package contains an invalid executable payload.");
        }
    }

    private static void ValidatePackageMetadata(
        ZipArchive archive,
        VerifiedReleaseDescriptor release,
        bool useCurrentLayout)
    {
        var nuspecBytes = ReadArchiveEntry(archive, "RobloxOne.nuspec");
        var versionBytes = ReadArchiveEntry(archive, "lib/app/sq.version");
        if (!CryptographicOperations.FixedTimeEquals(
                SHA256.HashData(nuspecBytes),
                SHA256.HashData(versionBytes)))
        {
            throw new ReleaseTrustException(
                "The downloaded package contains inconsistent application metadata.");
        }

        XDocument document;
        try
        {
            using var input = new MemoryStream(nuspecBytes, writable: false);
            using var reader = XmlReader.Create(input, new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                MaxCharactersInDocument = 256 * 1024,
                XmlResolver = null
            });
            document = XDocument.Load(reader, LoadOptions.None);
        }
        catch (XmlException exception)
        {
            throw new ReleaseTrustException(
                "The downloaded package metadata is malformed.",
                exception);
        }

        var root = document.Root;
        var metadataElements = root?.Elements().Where(element =>
            element.Name.LocalName.Equals("metadata", StringComparison.Ordinal)).ToArray() ?? [];
        var metadata = metadataElements.Length == 1 ? metadataElements[0] : null;
        if (root is null ||
            !root.Name.LocalName.Equals("package", StringComparison.Ordinal) ||
            metadata is null)
        {
            throw new ReleaseTrustException(
                "The downloaded package metadata is incomplete.");
        }

        var elements = metadata.Elements().ToArray();
        var expectedNames = new HashSet<string>(StringComparer.Ordinal)
        {
            "id", "title", "description", "authors", "version", "channel",
            "mainExe", "os", "rid", "shortcutLocations", "shortcutAumid",
            "releaseNotes", "releaseNotesHtml", "machineArchitecture"
        };
        if (elements.Length != expectedNames.Count ||
            elements.Any(element =>
                element.Name.Namespace != metadata.Name.Namespace ||
                !expectedNames.Remove(element.Name.LocalName)) ||
            expectedNames.Count != 0)
        {
            throw new ReleaseTrustException(
                "The downloaded package metadata contains unsupported fields.");
        }

        var expectedValues = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["id"] = ReleaseDescriptorPolicy.VelopackPackageId,
            ["title"] = useCurrentLayout ? "SessionDock" : "Roblox One",
            ["description"] = useCurrentLayout ? "SessionDock" : "Roblox One",
            ["authors"] = "Makmatoe",
            ["version"] = release.Descriptor.Version,
            ["channel"] = useCurrentLayout
                ? ReleaseDescriptorPolicy.Channel
                : ReleaseDescriptorPolicy.LegacyChannel,
            ["mainExe"] = useCurrentLayout ? "SessionDock.exe" : "RobloxOne.exe",
            ["os"] = "win",
            ["rid"] = "win-x64",
            ["shortcutLocations"] = "Desktop,StartMenuRoot",
            ["shortcutAumid"] = "velopack.RobloxOne",
            ["machineArchitecture"] = "x64"
        };
        foreach (var expected in expectedValues)
        {
            var value = elements.Single(element =>
                element.Name.LocalName.Equals(expected.Key, StringComparison.Ordinal)).Value;
            if (!value.Equals(expected.Value, StringComparison.Ordinal))
            {
                throw new ReleaseTrustException(
                    "The downloaded package metadata does not match the signed release.");
            }
        }

        var releaseNotes = elements.Single(element =>
                element.Name.LocalName.Equals("releaseNotes", StringComparison.Ordinal))
            .Value
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Trim();
        if (!releaseNotes.Equals(
                release.Descriptor.ReleaseNotes,
                StringComparison.Ordinal))
        {
            throw new ReleaseTrustException(
                "The downloaded package release notes do not match the signed release.");
        }

        var releaseNotesHtml = elements.Single(element =>
            element.Name.LocalName.Equals("releaseNotesHtml", StringComparison.Ordinal)).Value;
        if (releaseNotesHtml.Length > ReleaseDescriptorPolicy.MaximumReleaseNotesLength * 2 ||
            releaseNotesHtml.Contains("<script", StringComparison.OrdinalIgnoreCase) ||
            releaseNotesHtml.Contains("javascript:", StringComparison.OrdinalIgnoreCase) ||
            releaseNotesHtml.Any(character =>
                char.IsControl(character) && character is not ('\r' or '\n' or '\t')))
        {
            throw new ReleaseTrustException(
                "The downloaded package contains unsafe rendered release notes.");
        }
    }

    private static byte[] ReadArchiveEntry(ZipArchive archive, string name)
    {
        var entry = archive.GetEntry(name) ??
            throw new ReleaseTrustException(
                "The downloaded package is missing required metadata.");
        using var input = entry.Open();
        using var output = new MemoryStream((int)entry.Length);
        input.CopyTo(output);
        return output.ToArray();
    }
}

internal sealed record AvailableSessionDockUpdate(
    UpdateInfo UpdateInfo,
    VerifiedReleaseDescriptor Release);
