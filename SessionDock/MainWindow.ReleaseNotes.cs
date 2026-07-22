using System.Diagnostics;
using System.IO;
using System.Windows;
using SessionDock.Services;

namespace SessionDock;

public partial class MainWindow
{
    private void ReleaseNotesButton_Click(object sender, RoutedEventArgs e)
    {
        if (_operationBusy)
            return;

        try
        {
            var dialog = new ReleaseNotesDialog(
                BundledReleaseNotesCatalog.CurrentAndPrevious)
            {
                Owner = this
            };
            _ = dialog.ShowDialog();
        }
        catch (Exception exception) when (
            IsExpectedReleaseNotesFailure(exception))
        {
            Trace.WriteLine(
                $"Bundled release notes are unavailable: {exception.Message}");
            SetStatus(
                "Release notes are unavailable",
                "This copy of SessionDock does not contain valid notes for its installed version.",
                "NOTES UNAVAILABLE");
        }
    }

    internal static bool IsExpectedReleaseNotesFailure(Exception exception) =>
        exception is InvalidDataException or IOException or
            UnauthorizedAccessException;
}
