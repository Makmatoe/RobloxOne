using System.Windows;
using SessionDock.ReleaseTrust;
using SessionDock.Services;

namespace SessionDock;

public partial class UpdateConfirmationDialog : Window
{
    public UpdateConfirmationDialog(
        VerifiedReleaseDescriptor update,
        bool alreadyDownloaded)
    {
        ArgumentNullException.ThrowIfNull(update);
        InitializeComponent();
        WindowLayoutService.FitToWorkArea(this);

        UpdateTitleText.Text = alreadyDownloaded
            ? $"Restart to install SessionDock {update.Version.ToString(3)}?"
            : $"Install SessionDock {update.Version.ToString(3)}?";
        PublishedText.Text = $"Published {update.PublishedAt.ToLocalTime():g}";
        SizeText.Text = $"{update.Descriptor.PackageSize / (1024d * 1024d):0.0} MB";
        ReleaseNotesBox.Text = ReleaseNotesTextFormatter.Format(
            update.Descriptor.ReleaseNotes);
        IntegrityText.Text = alreadyDownloaded
            ? $"SHA-256 {update.Descriptor.PackageSha256[..16]}…  •  Signed package bytes, contents, and version verified."
            : $"SHA-256 {update.Descriptor.PackageSha256[..16]}…  •  Signed package identity authorized; bytes, contents, and version are checked after download. Windows binaries are not code-signed.";
    }

    private void InstallButton_Click(object sender, RoutedEventArgs e) =>
        DialogResult = true;

    private void CancelButton_Click(object sender, RoutedEventArgs e) =>
        DialogResult = false;
}
