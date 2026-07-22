using System.IO;
using SessionDock.Services;

namespace SessionDock.Tests;

public sealed class BundledReleaseNotesCatalogTests
{
    [Fact]
    public void Select_UsesInstalledAndNearestLowerSemanticVersion()
    {
        var notes = new[]
        {
            CreateNote("2.11.0"),
            CreateNote("2.8.0"),
            CreateNote("2.10.0"),
            CreateNote("2.9.9")
        };

        var selected = BundledReleaseNotesCatalog.Select(
            new Version(2, 10, 0, 42),
            notes);

        Assert.Equal(new Version(2, 10, 0), selected.Current.Version);
        Assert.Equal(new Version(2, 9, 9), selected.Previous!.Version);
    }

    [Fact]
    public void Select_MissingCurrentVersionIsExplicitlyRejected()
    {
        var exception = Assert.Throws<InvalidDataException>(() =>
            BundledReleaseNotesCatalog.Select(
                new Version(2, 10, 0),
                [CreateNote("2.9.9"), CreateNote("2.11.0")]));

        Assert.Contains("2.10.0", exception.Message);
        Assert.Contains("unavailable", exception.Message);
    }

    [Fact]
    public void Select_FirstReleaseCanHaveNoPreviousNotes()
    {
        var selected = BundledReleaseNotesCatalog.Select(
            new Version(1, 0, 0),
            [CreateNote("1.0.0"), CreateNote("1.1.0")]);

        Assert.Equal(new Version(1, 0, 0), selected.Current.Version);
        Assert.Null(selected.Previous);
    }

    [Fact]
    public void Select_DuplicateVersionIsRejected()
    {
        Assert.Throws<InvalidDataException>(() =>
            BundledReleaseNotesCatalog.Select(
                new Version(2, 3, 1),
                [CreateNote("2.3.1"), CreateNote("2.3.1")]));
    }

    [Fact]
    public void LoadForCurrentAssembly_ContainsReadableCurrentAndPreviousNotes()
    {
        var assemblyVersion = typeof(MainWindow).Assembly.GetName().Version!;
        var expectedCurrent = new Version(
            assemblyVersion.Major,
            assemblyVersion.Minor,
            assemblyVersion.Build);

        var notes = BundledReleaseNotesCatalog.LoadForCurrentAssembly();

        Assert.Equal(expectedCurrent, notes.Current.Version);
        Assert.NotNull(notes.Previous);
        Assert.True(notes.Previous.Version < notes.Current.Version);
        Assert.Contains(
            $"SessionDock {notes.Current.Version.ToString(3)}",
            notes.Current.DisplayText);
        Assert.Contains(
            $"SessionDock {notes.Previous.Version.ToString(3)}",
            notes.Previous.DisplayText);
        Assert.DoesNotContain("# SessionDock", notes.Current.DisplayText);
        Assert.DoesNotContain("**", notes.Current.DisplayText);
        Assert.DoesNotContain("# SessionDock", notes.Previous.DisplayText);
        Assert.DoesNotContain("**", notes.Previous.DisplayText);
    }

    [Fact]
    public void ExpectedCatalogFailuresAreContainedWithoutHidingProgrammerFaults()
    {
        Assert.True(MainWindow.IsExpectedReleaseNotesFailure(
            new InvalidDataException()));
        Assert.True(MainWindow.IsExpectedReleaseNotesFailure(
            new IOException()));
        Assert.True(MainWindow.IsExpectedReleaseNotesFailure(
            new UnauthorizedAccessException()));
        Assert.False(MainWindow.IsExpectedReleaseNotesFailure(
            new InvalidOperationException()));
    }

    private static BundledReleaseNote CreateNote(string version) =>
        new(new Version(version), $"Notes for {version}");
}
