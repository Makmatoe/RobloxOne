using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Media;
using SessionDock.Services;
using SessionDock.SystemProcesses;

namespace SessionDock;

public partial class HandleScopeIntegrationDialog : Window
{
    private const string OfficialReleasesUrl =
        "https://github.com/Makmatoe/HandleScope/releases";

    private readonly HandleScopeIntegrationService _integrationService = new();
    private readonly CancellationTokenSource _lifetimeCancellation = new();
    private HandleScopeIntegrationState _state =
        HandleScopeIntegrationState.NotInstalled;
    private bool _canRepairConfiguration;
    private bool _repairEnablesIntegration = true;
    private bool _isBusy;
    private bool _isClosed;

    public HandleScopeIntegrationDialog()
    {
        InitializeComponent();
        WindowLayoutService.FitToWorkArea(this);
        Loaded += HandleScopeIntegrationDialog_Loaded;
        Closed += HandleScopeIntegrationDialog_Closed;
    }

    private async void HandleScopeIntegrationDialog_Loaded(
        object sender,
        RoutedEventArgs e)
    {
        Loaded -= HandleScopeIntegrationDialog_Loaded;
        WindowLayoutService.FitToWorkArea(this);
        await RunActionAsync(
            cancellationToken => _integrationService.InspectAsync(cancellationToken),
            "Local setup inspected. Use Test connection to contact the API.",
            repairEnablesIntegration: true);
    }

    private async void RefreshButton_Click(object sender, RoutedEventArgs e) =>
        await RunActionAsync(
            cancellationToken => _integrationService.InspectAsync(cancellationToken),
            "Local setup refreshed. No connection test was performed.",
            repairEnablesIntegration: true);

    private async void StartApiButton_Click(object sender, RoutedEventArgs e) =>
        await RunActionAsync(
            cancellationToken => _integrationService.StartAsync(cancellationToken),
            "Start requested. Select Test connection to confirm the local API is ready.",
            repairEnablesIntegration: true);

    private async void EnableButton_Click(object sender, RoutedEventArgs e) =>
        await RunActionAsync(
            cancellationToken => _integrationService.EnableAsync(
                repairExisting: false,
                cancellationToken),
            "Integration enabled locally. Start the API, then test the connection.",
            repairEnablesIntegration: true);

    private async void DisableButton_Click(object sender, RoutedEventArgs e) =>
        await RunActionAsync(
            cancellationToken => _integrationService.DisableAsync(
                repairExisting: false,
                cancellationToken),
            "Integration disabled. HandleScope may keep running, but SessionDock will not use it during launches.",
            repairEnablesIntegration: false);

    private async void TestConnectionButton_Click(object sender, RoutedEventArgs e) =>
        await RunActionAsync(
            cancellationToken =>
                _integrationService.TestConnectionAsync(cancellationToken),
            "Connection test completed without closing or inspecting any handles.",
            repairEnablesIntegration: true);

    private async void RepairButton_Click(object sender, RoutedEventArgs e)
    {
        if (_repairEnablesIntegration)
        {
            await RunActionAsync(
                cancellationToken => _integrationService.EnableAsync(
                    repairExisting: true,
                    cancellationToken),
                "Integration configuration repaired and enabled with the fixed minimal policy. Start the API, then test the connection.",
                repairEnablesIntegration: true);
            return;
        }

        await RunActionAsync(
            cancellationToken => _integrationService.DisableAsync(
                repairExisting: true,
                cancellationToken),
            "Integration configuration repaired and disabled. HandleScope may keep running, but SessionDock will not use it during launches.",
            repairEnablesIntegration: false);
    }

    private void GetHandleScopeButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = OfficialReleasesUrl,
                UseShellExecute = true
            });
            ActionStatusText.Text =
                "Opened the official HandleScope Releases page in your browser.";
        }
        catch (Exception ex) when (
            ex is Win32Exception or InvalidOperationException or NotSupportedException)
        {
            ActionStatusText.Text =
                "The official release page could not be opened. Visit github.com/Makmatoe/HandleScope/releases.";
        }
    }

    private async Task RunActionAsync(
        Func<CancellationToken, Task<HandleScopeIntegrationResult>> action,
        string completedMessage,
        bool repairEnablesIntegration)
    {
        if (_isBusy)
            return;

        SetBusy(true);
        ActionStatusText.Text = "Working...";
        try
        {
            var result = await action(_lifetimeCancellation.Token);
            _state = result.State;
            _canRepairConfiguration = result.CanRepairConfiguration;
            if (_canRepairConfiguration)
                _repairEnablesIntegration = repairEnablesIntegration;
            RenderState();
            ActionStatusText.Text = _state ==
                HandleScopeIntegrationState.ConfigurationError
                    ? _canRepairConfiguration
                        ? "The existing opt-in was preserved. Review the warning before choosing Repair."
                        : "The action was refused safely. Check the official installation, then refresh the status."
                    : completedMessage;
        }
        catch (OperationCanceledException)
        {
            ActionStatusText.Text = string.Empty;
        }
        finally
        {
            if (!_isClosed)
                SetBusy(false);
        }
    }

    private void RenderState()
    {
        RepairWarningPanel.Visibility = Visibility.Collapsed;
        RepairButton.Visibility = Visibility.Collapsed;

        switch (_state)
        {
            case HandleScopeIntegrationState.NotInstalled:
                SetStatePresentation(
                    "HandleScope is not installed",
                    "Install it separately from the official release page. SessionDock will not download or install it for you.",
                    "NOT INSTALLED",
                    "#8E99AD",
                    "#222938",
                    "IconUpdate");
                break;
            case HandleScopeIntegrationState.InstalledStopped:
                SetStatePresentation(
                    "Installed - connection not tested",
                    "HandleScope is installed locally. Start its API explicitly, enable the integration if needed, then test the connection.",
                    "NOT CONNECTED",
                    "#A99BFF",
                    "#2A2348",
                    "IconActivity");
                break;
            case HandleScopeIntegrationState.RunningDisabled:
                SetStatePresentation(
                    "API running - integration disabled",
                    "The API at the expected local path answered, but SessionDock's fixed Roblox policy is not enabled.",
                    "DISABLED",
                    "#E0A33A",
                    "#302617",
                    "IconLock");
                break;
            case HandleScopeIntegrationState.Ready:
                SetStatePresentation(
                    "Ready for SessionDock",
                    "The checked loopback API is running and the fixed Roblox policy is enabled.",
                    "READY",
                    "#5DD6A8",
                    "#18332B",
                    "IconCheck");
                break;
            case HandleScopeIntegrationState.UpdateRequired:
                SetStatePresentation(
                    "HandleScope update required",
                    "The installed API is not compatible with this SessionDock release. Install the latest official HandleScope release.",
                    "UPDATE REQUIRED",
                    "#E0A33A",
                    "#302617",
                    "IconUpdate");
                break;
            case HandleScopeIntegrationState.ConfigurationError:
                if (_canRepairConfiguration)
                {
                    SetStatePresentation(
                        "Configuration was preserved",
                        "The local opt-in is invalid or does not match SessionDock's fixed policy, so the integration remains unavailable.",
                        "ACTION REQUIRED",
                        "#FF7188",
                        "#3A1E27",
                        "IconWarning");
                    RepairWarningPanel.Visibility = Visibility.Visible;
                    RepairButton.Visibility = Visibility.Visible;
                    RepairWarningText.Text = _repairEnablesIntegration
                        ? "SessionDock preserved the existing configuration and will not use it. Repair replaces only the SessionDock opt-in with the fixed, minimal enabled policy; it does not reinstall or stop HandleScope."
                        : "SessionDock preserved the existing configuration. Repair replaces only the SessionDock opt-in with the fixed, minimal disabled policy; it does not reinstall or stop HandleScope.";
                    RepairButtonLabel.Text = _repairEnablesIntegration
                        ? "Repair and enable"
                        : "Repair and disable";
                }
                else
                {
                    SetStatePresentation(
                        "Local safety check failed",
                        "SessionDock refused the local installation, start request, or health response. Reinstall or update from the official release page, then refresh.",
                        "UNAVAILABLE",
                        "#FF7188",
                        "#3A1E27",
                        "IconError");
                }
                break;
            default:
                throw new InvalidOperationException("Unexpected integration state.");
        }

        UpdateActionAvailability();
    }

    private void SetStatePresentation(
        string title,
        string description,
        string badge,
        string accent,
        string background,
        string iconResource)
    {
        StateTitleText.Text = title;
        StateDescriptionText.Text = description;
        StateBadgeText.Text = badge;
        var accentBrush = new SolidColorBrush(
            (Color)ColorConverter.ConvertFromString(accent));
        var backgroundBrush = new SolidColorBrush(
            (Color)ColorConverter.ConvertFromString(background));
        StateIcon.Stroke = accentBrush;
        StateBadgeText.Foreground = accentBrush;
        StateIconShell.Background = backgroundBrush;
        StateBadge.Background = backgroundBrush;
        StateIcon.Data = (Geometry)FindResource(iconResource);
    }

    private void SetBusy(bool busy)
    {
        _isBusy = busy;
        UpdateActionAvailability();
    }

    private void UpdateActionAvailability()
    {
        GetHandleScopeButton.IsEnabled = !_isBusy;
        RefreshButton.IsEnabled = !_isBusy;
        StartApiButton.IsEnabled = !_isBusy && _state ==
            HandleScopeIntegrationState.InstalledStopped;
        EnableButton.IsEnabled = !_isBusy && _state is
            HandleScopeIntegrationState.InstalledStopped or
            HandleScopeIntegrationState.RunningDisabled;
        DisableButton.IsEnabled = !_isBusy && _state is
            HandleScopeIntegrationState.InstalledStopped or
            HandleScopeIntegrationState.Ready or
            HandleScopeIntegrationState.UpdateRequired;
        TestConnectionButton.IsEnabled = !_isBusy && _state is not
            HandleScopeIntegrationState.NotInstalled and not
            HandleScopeIntegrationState.ConfigurationError;
        RepairButton.IsEnabled = !_isBusy && _state ==
            HandleScopeIntegrationState.ConfigurationError &&
            _canRepairConfiguration;
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();

    private void HandleScopeIntegrationDialog_Closed(object? sender, EventArgs e)
    {
        _isClosed = true;
        _lifetimeCancellation.Cancel();
        _integrationService.Dispose();
        _lifetimeCancellation.Dispose();
    }
}
