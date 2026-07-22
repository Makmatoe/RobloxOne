using System.IO;
using System.Text.RegularExpressions;

namespace SessionDock.Services;

internal sealed class RuntimeSmokeTestOptions
{
    internal const string ArgumentName = "--isolated-runtime-smoke";
    internal const string ResultFileName = "runtime-smoke.success";
    internal const string RootNamePrefix = "SessionDock-runtime-smoke-";
    private static readonly Regex RootNamePattern = new(
        $"^{Regex.Escape(RootNamePrefix)}(?<suffix>[0-9a-fA-F]{{32}})$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private RuntimeSmokeTestOptions(string rootDirectory, string identifierSuffix)
    {
        RootDirectory = rootDirectory;
        ResultPath = Path.Combine(rootDirectory, ResultFileName);
        ApplicationId = $"SessionDockRuntimeSmoke{identifierSuffix}";
    }

    internal string RootDirectory { get; }

    internal string ResultPath { get; }

    internal string ApplicationId { get; }

    internal static bool IsRequested(IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);
        return args.Count > 0 &&
               string.Equals(args[0], ArgumentName, StringComparison.Ordinal);
    }

    internal static bool TryParse(
        IReadOnlyList<string> args,
        out RuntimeSmokeTestOptions? options,
        out string? error)
    {
        ArgumentNullException.ThrowIfNull(args);
        options = null;
        error = null;

        // Velopack owns its lifecycle arguments. Only claim an invocation when
        // the dedicated smoke-test switch is the first argument.
        if (!IsRequested(args))
            return true;

        if (args.Count != 2)
        {
            error = $"{ArgumentName} requires exactly one isolated data-root path.";
            return false;
        }

        if (!TryValidateRoot(
                args[1],
                out var rootDirectory,
                out var identifierSuffix,
                out error))
        {
            return false;
        }

        options = new RuntimeSmokeTestOptions(
            rootDirectory!,
            identifierSuffix!);
        return true;
    }

    internal static bool TryValidateRoot(
        string? rootDirectory,
        out string? validatedRoot,
        out string? identifierSuffix,
        out string? error) =>
        TryValidateRoot(
            rootDirectory,
            Path.GetTempPath(),
            File.GetAttributes,
            out validatedRoot,
            out identifierSuffix,
            out error);

    internal static bool TryValidateRoot(
        string? rootDirectory,
        string temporaryDirectory,
        Func<string, FileAttributes> getAttributes,
        out string? validatedRoot,
        out string? identifierSuffix,
        out string? error)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(temporaryDirectory);
        ArgumentNullException.ThrowIfNull(getAttributes);
        validatedRoot = null;
        identifierSuffix = null;
        error = null;

        if (string.IsNullOrWhiteSpace(rootDirectory))
        {
            error = "The isolated runtime smoke-test root is required.";
            return false;
        }

        string temporaryRoot;
        string candidate;
        try
        {
            temporaryRoot = Path.TrimEndingDirectorySeparator(
                Path.GetFullPath(temporaryDirectory));
            candidate = Path.TrimEndingDirectorySeparator(
                Path.GetFullPath(rootDirectory));
        }
        catch (Exception exception) when (
            exception is ArgumentException or IOException or NotSupportedException)
        {
            error = "The isolated runtime smoke-test root is not a valid path.";
            return false;
        }

        if (!Path.IsPathFullyQualified(rootDirectory) ||
            !string.Equals(
                Path.GetDirectoryName(candidate),
                temporaryRoot,
                StringComparison.OrdinalIgnoreCase))
        {
            error =
                "The isolated runtime smoke-test root must be a direct child of the Windows temporary directory.";
            return false;
        }

        var rootName = Path.GetFileName(candidate);
        var nameMatch = RootNamePattern.Match(rootName);
        if (!nameMatch.Success)
        {
            error =
                $"The isolated runtime smoke-test root name must match {RootNamePrefix}<32 hexadecimal characters>.";
            return false;
        }

        FileAttributes temporaryAttributes;
        try
        {
            temporaryAttributes = getAttributes(temporaryRoot);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            error = "The Windows temporary directory could not be safely inspected.";
            return false;
        }

        if ((temporaryAttributes & FileAttributes.Directory) == 0 ||
            (temporaryAttributes & FileAttributes.ReparsePoint) != 0)
        {
            error =
                "The Windows temporary directory must be a regular directory rather than a redirected path.";
            return false;
        }

        try
        {
            var targetAttributes = getAttributes(candidate);
            error = (targetAttributes & FileAttributes.ReparsePoint) != 0
                ? "The isolated runtime smoke-test root cannot be a redirected path."
                : "The isolated runtime smoke-test root must not already exist.";
            return false;
        }
        catch (Exception exception) when (
            exception is FileNotFoundException or DirectoryNotFoundException)
        {
            // A fresh, process-owned directory is required so startup cleanup
            // cannot operate on pre-existing data.
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            error = "The isolated runtime smoke-test root could not be safely inspected.";
            return false;
        }

        validatedRoot = candidate;
        identifierSuffix = nameMatch.Groups["suffix"].Value.ToLowerInvariant();
        return true;
    }
}
