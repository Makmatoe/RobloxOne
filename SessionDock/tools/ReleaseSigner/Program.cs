using System.Globalization;
using System.Security.Cryptography;
using SessionDock.ReleaseTrust;

return await ReleaseSignerProgram.RunAsync(args);

internal static class ReleaseSignerProgram
{
    private static readonly HashSet<string> SignOptions = new(StringComparer.Ordinal)
    {
        "package", "notes", "output", "repository", "channel", "version", "tag",
        "private-key-base64-env", "identity"
    };

    private static readonly HashSet<string> VerifyOptions = new(StringComparer.Ordinal)
    {
        "manifest", "package", "public-key"
    };

    public static async Task<int> RunAsync(string[] arguments)
    {
        try
        {
            if (arguments.Length == 0)
                throw new ArgumentException("Specify either 'sign' or 'verify'.");

            var command = arguments[0];
            var options = ParseOptions(arguments[1..], command switch
            {
                "sign" => SignOptions,
                "verify" => VerifyOptions,
                _ => throw new ArgumentException("Specify either 'sign' or 'verify'.")
            });

            if (command == "sign")
                await SignAsync(options);
            else
                await VerifyAsync(options);

            return 0;
        }
        catch (Exception exception) when (
            exception is ArgumentException or IOException or
            UnauthorizedAccessException or CryptographicException or
            ReleaseTrustException)
        {
            Console.Error.WriteLine($"Release descriptor operation failed: {exception.Message}");
            return 1;
        }
    }

    private static async Task SignAsync(IReadOnlyDictionary<string, string> options)
    {
        var packagePath = RequireFile(options, "package");
        var notesPath = RequireFile(options, "notes");
        var outputPath = Require(options, "output");
        var repository = Require(options, "repository");
        var channel = Require(options, "channel");
        var versionText = Require(options, "version");
        var tag = Require(options, "tag");
        var privateKeyEnvironmentName = Require(options, "private-key-base64-env");
        var identityName = options.GetValueOrDefault("identity", "current");
        var identity = identityName switch
        {
            "current" => new ReleaseIdentity(
                ReleaseDescriptorPolicy.Product,
                ReleaseDescriptorPolicy.Repository,
                ReleaseDescriptorPolicy.Channel,
                ReleaseDescriptorPolicy.KeyId),
            "legacy" => new ReleaseIdentity(
                ReleaseDescriptorPolicy.LegacyProduct,
                ReleaseDescriptorPolicy.LegacyRepository,
                ReleaseDescriptorPolicy.LegacyChannel,
                ReleaseDescriptorPolicy.LegacyKeyId),
            _ => throw new ArgumentException(
                "The release identity must be either 'current' or 'legacy'.")
        };

        if (repository != identity.Repository || channel != identity.Channel)
        {
            throw new ArgumentException("Repository or channel does not match release policy.");
        }

        if (!Version.TryParse(versionText, out var version) ||
            version.Build < 0 || version.Revision >= 0 ||
            version.ToString(3) != versionText || tag != $"v{versionText}")
        {
            throw new ArgumentException("Version must be a three-part stable version matching the v* tag.");
        }

        var package = new FileInfo(packagePath);
        if (package.Length is < ReleaseDescriptorPolicy.MinimumPackageSize or
            > ReleaseDescriptorPolicy.MaximumPackageSize ||
            !package.Name.EndsWith(".nupkg", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("The release package has an invalid name or size.");
        }

        var releaseNotes = NormalizeReleaseNotes(
            await File.ReadAllTextAsync(notesPath));
        var packageHash = await ComputeSha256Async(packagePath);
        var publishedAt = DateTimeOffset.UtcNow.ToString(
            "O",
            CultureInfo.InvariantCulture);
        var unsignedDescriptor = new ReleaseDescriptor(
            ReleaseDescriptorPolicy.SchemaVersion,
            identity.Product,
            repository,
            channel,
            identity.KeyId,
            versionText,
            tag,
            publishedAt,
            package.Name,
            package.Length,
            packageHash,
            releaseNotes,
            string.Empty);

        var privateKeyBase64 = Environment.GetEnvironmentVariable(
            privateKeyEnvironmentName);
        if (string.IsNullOrWhiteSpace(privateKeyBase64))
            throw new ArgumentException("The configured release signing key is unavailable.");
        if (privateKeyBase64.Length > 1024 ||
            privateKeyBase64.Any(char.IsWhiteSpace))
        {
            throw new ArgumentException(
                "The configured release signing key must be one canonical base64 value.");
        }

        byte[] privateKeyBytes;
        try
        {
            privateKeyBytes = Convert.FromBase64String(privateKeyBase64);
        }
        catch (FormatException exception)
        {
            throw new ArgumentException(
                "The configured release signing key is not valid base64.",
                exception);
        }

        byte[] signature;
        try
        {
            using var privateKey = ECDsa.Create();
            privateKey.ImportPkcs8PrivateKey(privateKeyBytes, out var bytesRead);
            if (bytesRead != privateKeyBytes.Length || privateKey.KeySize != 256)
            {
                throw new CryptographicException(
                    "The release signing key must be one P-256 PKCS#8 key.");
            }

            signature = privateKey.SignData(
                ReleaseDescriptorPolicy.CreateCanonicalPayload(unsignedDescriptor),
                HashAlgorithmName.SHA256);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(privateKeyBytes);
        }
        var signedDescriptor = unsignedDescriptor with
        {
            Signature = Convert.ToBase64String(signature)
        };

        var outputDirectory = Path.GetDirectoryName(Path.GetFullPath(outputPath));
        if (!string.IsNullOrWhiteSpace(outputDirectory))
            Directory.CreateDirectory(outputDirectory);
        await File.WriteAllTextAsync(
            outputPath,
            ReleaseDescriptorPolicy.Serialize(signedDescriptor));
    }

    private static async Task VerifyAsync(IReadOnlyDictionary<string, string> options)
    {
        var manifestPath = RequireFile(options, "manifest");
        var packagePath = RequireFile(options, "package");
        var publicKeyPath = RequireFile(options, "public-key");
        var package = new FileInfo(packagePath);
        var identity = new ReleaseAssetIdentity(
            ReadDescriptorVersion(manifestPath),
            package.Name,
            package.Length,
            await ComputeSha256Async(packagePath));
        var verified = ReleaseDescriptorPolicy.Verify(
            await File.ReadAllTextAsync(manifestPath),
            identity,
            await File.ReadAllTextAsync(publicKeyPath));
        Console.WriteLine(
            $"Verified {verified.Descriptor.Tag} ({verified.Descriptor.PackageFile}).");
    }

    private static string ReadDescriptorVersion(string manifestPath) =>
        ReleaseDescriptorPolicy.Deserialize(File.ReadAllText(manifestPath)).Version;

    private static Dictionary<string, string> ParseOptions(
        string[] arguments,
        HashSet<string> allowed)
    {
        if (arguments.Length % 2 != 0)
            throw new ArgumentException("Every option must have a value.");

        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var index = 0; index < arguments.Length; index += 2)
        {
            var option = arguments[index];
            if (!option.StartsWith("--", StringComparison.Ordinal) || option.Length < 3)
                throw new ArgumentException($"Invalid option '{option}'.");
            var name = option[2..];
            if (!allowed.Contains(name) || !result.TryAdd(name, arguments[index + 1]))
                throw new ArgumentException($"Unsupported or duplicate option '--{name}'.");
        }

        return result;
    }

    private static string Require(
        IReadOnlyDictionary<string, string> options,
        string name)
    {
        if (!options.TryGetValue(name, out var value) || string.IsNullOrWhiteSpace(value))
            throw new ArgumentException($"Missing required option '--{name}'.");
        return value;
    }

    private static string RequireFile(
        IReadOnlyDictionary<string, string> options,
        string name)
    {
        var path = Path.GetFullPath(Require(options, name));
        if (!File.Exists(path))
            throw new ArgumentException($"The --{name} file does not exist.");
        return path;
    }

    private static string NormalizeReleaseNotes(string value)
    {
        var normalized = value.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Trim();
        if (string.IsNullOrWhiteSpace(normalized) ||
            normalized.Length > ReleaseDescriptorPolicy.MaximumReleaseNotesLength ||
            normalized.Any(character =>
                char.IsControl(character) && character is not ('\n' or '\t')))
        {
            throw new ArgumentException("Release notes are empty, too large, or contain control characters.");
        }

        return normalized;
    }

    private static async Task<string> ComputeSha256Async(string path)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 128 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream));
    }

    private sealed record ReleaseIdentity(
        string Product,
        string Repository,
        string Channel,
        string KeyId);
}
