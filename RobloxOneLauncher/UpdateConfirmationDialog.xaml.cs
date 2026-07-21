using System.Windows;
using RobloxOne.ReleaseTrust;
using RobloxOneLauncher.Services;

namespace RobloxOneLauncher;

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
            ? $"Restart to install Roblox One {update.Version.ToString(3)}?"
            : $"Install Roblox One {update.Version.ToString(3)}?";
        PublishedText.Text = $"Published {update.PublishedAt.ToLocalTime():g}";
        SizeText.Text = $"{update.Descriptor.PackageSize / (1024d * 1024d):0.0} MB";
        ReleaseNotesBox.Text = update.Descriptor.ReleaseNotes;
        IntegrityText.Text = alreadyDownloaded
            ? $"SHA-256 {update.Descriptor.PackageSha256[..16]}…  •  Package bytes and Windows publisher verified."
            : $"SHA-256 {update.Descriptor.PackageSha256[..16]}…  •  Package identity authorized; bytes and publisher are checked after download.";
    }

    private void InstallButton_Click(object sender, RoutedEventArgs e) =>
        DialogResult = true;

    private void CancelButton_Click(object sender, RoutedEventArgs e) =>
        DialogResult = false;
}
