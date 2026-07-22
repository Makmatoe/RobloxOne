using System.Windows;
using SessionDock.Models;
using SessionDock.Services;

namespace SessionDock;

public partial class MainWindow
{
    private void RunningClientsButton_Click(
        object sender,
        RoutedEventArgs e)
    {
        if (_operationBusy)
            return;

        var dialog = new RunningClientsDialog(
            _robloxClient,
            _runningClients,
            () => _operationLifetime.IsShuttingDown)
        {
            Owner = this
        };
        _ = dialog.ShowDialog();
        if (dialog.ClosedClientCount == 0)
            return;

        SetStatus(
            dialog.ClosedClientCount == 1
                ? "Roblox client closed"
                : "Roblox clients closed",
            dialog.ClosedClientCount == 1
                ? "One verified Roblox Player process was closed."
                : $"{dialog.ClosedClientCount} verified Roblox Player processes were closed.",
            "CLIENTS CLOSED");
    }

    private void TrackLaunchedClient(
        RobloxClientProcessIdentity? identity,
        AccountProfile account,
        RecentExperience recent)
    {
        if (identity is null)
            return;

        _runningClients.Track(
            identity,
            new RunningClientAttribution(
                account.Key,
                account.UserId,
                account.Username,
                account.Label,
                account.ColorHex,
                recent.PlaceId,
                recent.CustomName ?? recent.Name,
                recent.LastLaunchedAt));
    }
}
