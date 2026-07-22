using System.IO;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using SessionDock.ReleaseTrust;

namespace SessionDock.Services;

internal sealed record BundledReleaseNote(
    Version Version,
    string DisplayText);

internal sealed record BundledReleaseNotes(
    BundledReleaseNote Current,
    BundledReleaseNote? Previous);

internal static partial class BundledReleaseNotesCatalog
{
    private const string ResourcePrefix =
        "SessionDock.Embedded.ReleaseNotes.";
    private const string ResourceSuffix = ".md";
    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);
    private static readonly Lazy<BundledReleaseNotes> CurrentNotes = new(
        LoadForCurrentAssembly,
        LazyThreadSafetyMode.ExecutionAndPublication);

    public static BundledReleaseNotes CurrentAndPrevious =>
        CurrentNotes.Value;

    internal static BundledReleaseNotes LoadForCurrentAssembly()
    {
        var assembly = typeof(BundledReleaseNotesCatalog).Assembly;
        var currentVersion = assembly.GetName().Version ??
            throw new InvalidDataException(
                "The application assembly has no release version.");
        return Load(assembly, currentVersion);
    }

    internal static BundledReleaseNotes Load(
        Assembly assembly,
        Version currentVersion)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        ArgumentNullException.ThrowIfNull(currentVersion);

        var notes = new List<BundledReleaseNote>();
        foreach (var resourceName in assembly.GetManifestResourceNames()
                     .Where(name => name.StartsWith(
                         ResourcePrefix,
                         StringComparison.Ordinal)))
        {
            if (!resourceName.EndsWith(
                    ResourceSuffix,
                    StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    $"Bundled release-note resource '{resourceName}' has an unsupported name.");
            }

            var versionText = resourceName[
                ResourcePrefix.Length..
                ^ResourceSuffix.Length];
            if (!ReleaseVersionPattern().IsMatch(versionText) ||
                !Version.TryParse(versionText, out var version) ||
                !version.ToString(3).Equals(
                    versionText,
                    StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    $"Bundled release-note resource '{resourceName}' has an invalid version.");
            }

            using var stream = assembly.GetManifestResourceStream(resourceName) ??
                throw new InvalidDataException(
                    $"Bundled release-note resource '{resourceName}' could not be opened.");
            if (stream.Length >
                ReleaseDescriptorPolicy.MaximumReleaseNotesLength * 4L + 3)
            {
                throw new InvalidDataException(
                    $"Bundled release notes for {versionText} exceed the supported size.");
            }

            string markdown;
            try
            {
                using var reader = new StreamReader(
                    stream,
                    StrictUtf8,
                    detectEncodingFromByteOrderMarks: false);
                markdown = reader.ReadToEnd();
                if (markdown.StartsWith('\uFEFF'))
                    markdown = markdown[1..];
            }
            catch (DecoderFallbackException exception)
            {
                throw new InvalidDataException(
                    $"Bundled release notes for {versionText} are not valid UTF-8.",
                    exception);
            }

            try
            {
                var displayText = ReleaseNotesTextFormatter.Format(markdown);
                if (string.IsNullOrWhiteSpace(displayText))
                {
                    throw new InvalidDataException(
                        $"Bundled release notes for {versionText} are empty.");
                }
                notes.Add(new BundledReleaseNote(version, displayText));
            }
            catch (ArgumentException exception)
            {
                throw new InvalidDataException(
                    $"Bundled release notes for {versionText} are invalid.",
                    exception);
            }
        }

        return Select(currentVersion, notes);
    }

    internal static BundledReleaseNotes Select(
        Version currentVersion,
        IEnumerable<BundledReleaseNote> notes)
    {
        ArgumentNullException.ThrowIfNull(currentVersion);
        ArgumentNullException.ThrowIfNull(notes);
        var installedVersion = Normalize(currentVersion);
        var ordered = notes
            .OrderBy(note => note.Version)
            .ToArray();
        if (ordered.GroupBy(note => note.Version).Any(group => group.Count() > 1))
        {
            throw new InvalidDataException(
                "Bundled release notes contain duplicate versions.");
        }

        var current = ordered.SingleOrDefault(note =>
            note.Version == installedVersion) ??
            throw new InvalidDataException(
                $"Bundled release notes for SessionDock {installedVersion.ToString(3)} are unavailable.");
        var previous = ordered
            .Where(note => note.Version < installedVersion)
            .LastOrDefault();
        return new BundledReleaseNotes(current, previous);
    }

    private static Version Normalize(Version version)
    {
        if (version.Build < 0)
        {
            throw new InvalidDataException(
                "The application release version must contain major, minor, and patch components.");
        }
        return new Version(version.Major, version.Minor, version.Build);
    }

    [GeneratedRegex("^[0-9]+\\.[0-9]+\\.[0-9]+$", RegexOptions.CultureInvariant)]
    private static partial Regex ReleaseVersionPattern();
}
