using System.Diagnostics;
using System.Windows;
using SessionDock.ReleaseTrust;
using SessionDock.Services;

namespace SessionDock;

public partial class MainWindow
{
    private readonly SessionDockUpdateService _updateService = new();

    private async void InstallUpdateButton_Click(object sender, RoutedEventArgs e) =>
        await _operationLifetime.RunAsync(InstallUpdateButtonClickAsync);

    private async Task InstallUpdateButtonClickAsync(
        CancellationToken cancellationToken)
    {
        if (_operationBusy)
            return;

        var applyingUpdate = false;
        SetOperationBusy(true);
        try
        {
            if (!_updateService.CanSelfUpdate)
            {
                SetStatus(
                    "Updates require the installed app",
                    "This debug or portable copy cannot replace itself. Install SessionDock with the official Setup executable, then check again.",
                    "UPDATES UNAVAILABLE");
                return;
            }

            var pending = _updateService.PendingUpdate;
            if (pending is not null)
            {
                SetStatus(
                    "Update ready to restart",
                    "Verifying the signed release descriptor and downloaded package before restarting…",
                    "VERIFYING UPDATE");
                var verifiedPending = await _updateService.VerifyPendingAsync(
                    pending,
                    cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                if (!ConfirmUpdate(verifiedPending, alreadyDownloaded: true))
                {
                    SetStatus(
                        "Restart postponed",
                        "The verified update remains downloaded until you choose to install it.",
                        "UPDATE READY");
                    return;
                }

                _updateService.ApplyAfterExit(pending);
                applyingUpdate = true;
                _ = Dispatcher.BeginInvoke(() => Close());
                return;
            }

            SetStatus(
                "Checking for updates",
                "Contacting the official SessionDock release feed…",
                "CHECKING UPDATE");
            var available = await _updateService.CheckAsync(
                cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (available is null)
            {
                SetStatus(
                    "SessionDock is up to date",
                    $"Version {_updateService.CurrentVersion} is the newest stable release.",
                    "UP TO DATE");
                return;
            }

            if (!ConfirmUpdate(available.Release, alreadyDownloaded: false))
            {
                SetStatus(
                    "Update not installed",
                    "The signed update is available, but no files were changed.",
                    "UPDATE CANCELLED");
                return;
            }

            SetStatus(
                $"Downloading SessionDock {available.Release.Descriptor.Version}",
                "Downloading the verified package from GitHub… 0%",
                "DOWNLOADING UPDATE");
            await _updateService.DownloadAsync(
                available,
                progress => Dispatcher.BeginInvoke(() => SetStatus(
                    $"Downloading SessionDock {available.Release.Descriptor.Version}",
                    $"Downloading the verified package from GitHub… {progress}%",
                    "DOWNLOADING UPDATE")),
                cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();

            SetStatus(
                "Update downloaded",
                "SessionDock will close, install the verified package, and restart.",
                "RESTARTING");
            _updateService.ApplyAfterExit(available.UpdateInfo.TargetFullRelease);
            applyingUpdate = true;
            _ = Dispatcher.BeginInvoke(() => Close());
        }
        catch (OperationCanceledException) when (
            _operationLifetime.IsShuttingDown)
        {
            // Window shutdown owns this cancellation.
        }
        catch (OperationCanceledException)
        {
            SetStatus(
                "Update cancelled",
                "The installed version was left unchanged.",
                "UPDATE CANCELLED");
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"Update failed safely: {ex.GetType().Name}.");
            SetStatus(
                "Update was rejected",
                ex is ReleaseTrustException
                    ? ex.Message
                    : "The update could not be checked or installed safely. The current version was left unchanged.",
                "UPDATE ERROR");
        }
        finally
        {
            if (!applyingUpdate && !_operationLifetime.IsShuttingDown)
                SetOperationBusy(false);
        }
    }

    private bool ConfirmUpdate(
        VerifiedReleaseDescriptor update,
        bool alreadyDownloaded)
    {
        var confirmation = new UpdateConfirmationDialog(update, alreadyDownloaded)
        {
            Owner = this
        };
        return confirmation.ShowDialog() == true;
    }
}
