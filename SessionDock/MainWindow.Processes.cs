using System.Windows;

namespace SessionDock;

public partial class MainWindow
{
    private async void CloseAllInstancesButton_Click(
        object sender,
        RoutedEventArgs e)
    {
        if (_operationBusy)
            return;

        var confirmation = MessageBox.Show(
            "Close every running Roblox Player instance, including windowless background processes? Active games will disconnect.",
            "Close all Roblox instances",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);
        if (confirmation != MessageBoxResult.Yes)
            return;

        SetOperationBusy(true);
        SetStatus(
            "Closing all Roblox instances",
            "Checking every RobloxPlayerBeta process before closing it…",
            "CLIENT CLEANUP");

        try
        {
            var result = await _robloxClient.CloseAllPlayersAsync(
                _launchHookCancellation.Token);
            if (result.Success)
            {
                var detail = result.Found == 0
                    ? "No running RobloxPlayerBeta processes were found."
                    : result.BackgroundFound == 0
                        ? $"Closed {result.Closed} Roblox Player instance(s)."
                        : $"Closed {result.Closed} Roblox Player instance(s), including {result.BackgroundFound} background process(es).";
                SetStatus(
                    result.Found == 0
                        ? "Roblox Player is already closed"
                        : "All Roblox instances closed",
                    detail,
                    "CLIENTS CLOSED");
                return;
            }

            var problems = new List<string>();
            if (result.Remaining > 0)
            {
                problems.Add(
                    $"{result.Remaining} verified instance(s) could not be closed");
            }
            if (result.Unverified > 0)
            {
                problems.Add(
                    $"{result.Unverified} Roblox-named process(es) could not be safely verified and were left running");
            }
            SetStatus(
                "Some Roblox instances remain",
                string.Join("; ", problems) + ".",
                "CLIENT CLEANUP ERROR");
        }
        catch (OperationCanceledException)
        {
            // Window shutdown cancels process cleanup.
        }
        catch (Exception ex)
        {
            SetStatus(
                "Roblox instances could not be closed",
                ex.Message,
                "CLIENT CLEANUP ERROR");
        }
        finally
        {
            SetOperationBusy(false);
        }
    }
}
