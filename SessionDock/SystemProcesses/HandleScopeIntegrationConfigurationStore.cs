using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SessionDock.SystemProcesses;

internal sealed class HandleScopeIntegrationConfigurationStore
{
    private const int MaximumConfigurationBytes = 64 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        MaxDepth = 8
    };

    private readonly string _localAppDataRoot;
    private readonly string _configurationPath;
    private readonly Func<string, bool>? _isReparsePoint;

    internal HandleScopeIntegrationConfigurationStore(
        string localAppDataRoot,
        string configurationPath,
        Func<string, bool>? isReparsePoint = null)
    {
        _localAppDataRoot = Path.GetFullPath(localAppDataRoot);
        _configurationPath = Path.GetFullPath(configurationPath);
        _isReparsePoint = isReparsePoint;
    }

    internal HandleScopeConfigurationSnapshot Read()
    {
        try
        {
            var directory = Path.GetDirectoryName(_configurationPath);
            if (directory is null ||
                !HandleScopePathSecurity.IsSafeExistingPath(
                    _localAppDataRoot,
                    directory,
                    targetMustExist: true,
                    _isReparsePoint))
            {
                return HandleScopeConfigurationSnapshot.Invalid;
            }

            if (!TryGetAttributes(
                    _configurationPath,
                    out var configurationAttributes))
            {
                return HandleScopeConfigurationSnapshot.Missing;
            }
            if (!HandleScopePathSecurity.IsSafeExistingPath(
                    _localAppDataRoot,
                    _configurationPath,
                    targetMustExist: true,
                    _isReparsePoint) ||
                (configurationAttributes & FileAttributes.Directory) != 0)
            {
                return HandleScopeConfigurationSnapshot.Invalid;
            }

            var json = ReadBoundedFile(_configurationPath);
            try
            {
                using var document = JsonDocument.Parse(
                    json,
                    new JsonDocumentOptions { MaxDepth = 8 });
                var root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object ||
                    !HasUniqueProperties(root) ||
                    !TryGetEnabled(root, out var enabled))
                {
                    return HandleScopeConfigurationSnapshot.InvalidFor(json);
                }

                var configuration =
                    JsonSerializer.Deserialize<HandleScopeConfiguration>(
                        json,
                        JsonOptions);
                if (configuration is null ||
                    (enabled &&
                     new HandleScopeConfigurationLoader(
                         _configurationPath).LoadEnabled() is null))
                {
                    return HandleScopeConfigurationSnapshot.InvalidFor(json);
                }

                var isMinimal = root.GetPropertyCount() == 1 &&
                    root.EnumerateObject().Single().NameEquals("enabled");
                return new HandleScopeConfigurationSnapshot(
                    Exists: true,
                    IsValid: true,
                    IsEnabled: enabled,
                    IsMinimal: isMinimal,
                    CanRepair: false,
                    Fingerprint: SHA256.HashData(json));
            }
            catch (JsonException)
            {
                return HandleScopeConfigurationSnapshot.InvalidFor(json);
            }
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or
                JsonException or InvalidDataException or
                ArgumentException or NotSupportedException)
        {
            return HandleScopeConfigurationSnapshot.Invalid;
        }
    }

    internal HandleScopeConfigurationWriteResult TrySetEnabled(
        bool enabled,
        bool repairExisting)
    {
        var snapshot = Read();
        if (!snapshot.IsValid)
        {
            if (!snapshot.CanRepair)
                return HandleScopeConfigurationWriteResult.Failed;
            if (!repairExisting)
                return HandleScopeConfigurationWriteResult.RepairRequired;
        }
        if (snapshot.Exists && !snapshot.IsMinimal)
        {
            if (snapshot.IsValid && snapshot.IsEnabled == enabled)
                return HandleScopeConfigurationWriteResult.Succeeded;
            if (!repairExisting)
                return HandleScopeConfigurationWriteResult.RepairRequired;
        }

        var directory = Path.GetDirectoryName(_configurationPath);
        if (directory is null ||
            !HandleScopePathSecurity.IsSafeExistingPath(
                _localAppDataRoot,
                directory,
                targetMustExist: true,
                _isReparsePoint))
        {
            return HandleScopeConfigurationWriteResult.Failed;
        }

        var contents = Encoding.UTF8.GetBytes(
            enabled ? "{\"enabled\":true}\n" : "{\"enabled\":false}\n");
        var temporaryPath = Path.Combine(
            directory,
            $".handlescope.{Convert.ToHexString(RandomNumberGenerator.GetBytes(16))}.tmp");
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

            if (snapshot.Exists)
            {
                if (!File.Exists(_configurationPath) ||
                    !HandleScopePathSecurity.IsSafeExistingPath(
                        _localAppDataRoot,
                        _configurationPath,
                        targetMustExist: true,
                        _isReparsePoint) ||
                    !CryptographicOperations.FixedTimeEquals(
                        snapshot.Fingerprint,
                        SHA256.HashData(ReadBoundedFile(_configurationPath))))
                {
                    return HandleScopeConfigurationWriteResult.Failed;
                }

                File.Move(temporaryPath, _configurationPath, overwrite: true);
            }
            else
            {
                if (File.Exists(_configurationPath) ||
                    Directory.Exists(_configurationPath))
                {
                    return HandleScopeConfigurationWriteResult.Failed;
                }

                File.Move(temporaryPath, _configurationPath);
            }

            return HandleScopeConfigurationWriteResult.Succeeded;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or
                InvalidDataException or ArgumentException or NotSupportedException)
        {
            return HandleScopeConfigurationWriteResult.Failed;
        }
        finally
        {
            try
            {
                File.Delete(temporaryPath);
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException)
            {
                // A failed cleanup contains only the same non-secret opt-in bit.
            }
        }
    }

    private static byte[] ReadBoundedFile(string path)
    {
        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read);
        if (stream.Length > MaximumConfigurationBytes)
            throw new InvalidDataException("The configuration is too large.");

        using var buffer = new MemoryStream();
        var chunk = new byte[4096];
        while (true)
        {
            var read = stream.Read(chunk, 0, chunk.Length);
            if (read == 0)
                return buffer.ToArray();
            if (buffer.Length + read > MaximumConfigurationBytes)
                throw new InvalidDataException("The configuration is too large.");
            buffer.Write(chunk, 0, read);
        }
    }

    private static bool TryGetAttributes(
        string path,
        out FileAttributes attributes)
    {
        try
        {
            attributes = File.GetAttributes(path);
            return true;
        }
        catch (Exception exception) when (
            exception is FileNotFoundException or DirectoryNotFoundException)
        {
            attributes = default;
            return false;
        }
    }

    private static bool HasUniqueProperties(JsonElement root)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        return root.EnumerateObject().All(property => names.Add(property.Name));
    }

    private static bool TryGetEnabled(JsonElement root, out bool enabled)
    {
        enabled = false;
        foreach (var property in root.EnumerateObject())
        {
            if (!property.Name.Equals(
                    "enabled",
                    StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            if (property.Value.ValueKind is not (
                    JsonValueKind.True or JsonValueKind.False))
            {
                return false;
            }

            enabled = property.Value.GetBoolean();
            return true;
        }

        return false;
    }
}

internal sealed record HandleScopeConfigurationSnapshot(
    bool Exists,
    bool IsValid,
    bool IsEnabled,
    bool IsMinimal,
    bool CanRepair,
    byte[] Fingerprint)
{
    internal static HandleScopeConfigurationSnapshot Missing { get; } =
        new(false, true, false, true, false, []);

    internal static HandleScopeConfigurationSnapshot Invalid { get; } =
        new(true, false, false, false, false, []);

    internal static HandleScopeConfigurationSnapshot InvalidFor(byte[] json) =>
        new(true, false, false, false, true, SHA256.HashData(json));
}

internal enum HandleScopeConfigurationWriteResult
{
    Succeeded,
    RepairRequired,
    Failed
}
