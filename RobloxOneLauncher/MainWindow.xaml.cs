using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using RobloxOneLauncher.Models;
using RobloxOneLauncher.Services;
using RobloxOneLauncher.SystemProcesses;

namespace RobloxOneLauncher;

public partial class MainWindow : Window
{
    private readonly SettingsService _settingsService = new();
    private readonly RobloxClientService _robloxClient = new();
    private readonly RobloxServerTracker _serverTracker = new();
    private readonly RobloxWebSessionService _webSession = new();
    private readonly UiSoundService _soundService;
    private readonly ILaunchHook _launchHook = new CompositeLaunchHook(
        new HandleScopeLaunchHook(),
        new LocalApiLaunchHook());
    private readonly CancellationTokenSource _launchHookCancellation = new();
    private readonly SemaphoreSlim _accountCheckLock = new(1, 1);
    private readonly AppSettings _settings;
    private AccountProfile? _activeProfile;
    private AccountProfile? _pendingProfile;
    private RobloxUser? _currentUser;
    private CancellationTokenSource? _browserSwitchCancellation;
    private bool _launchInProgress;
    private bool _operationBusy;
    private bool _destinationTrackingEnabled;

    public MainWindow()
    {
        InitializeComponent();
        WindowLayoutService.FitToWorkArea(this);
        var app = (App)Application.Current;
        _soundService = app.SoundService;
        InstallUpdateButton.ToolTip =
            $"Check for signed updates (current {_updateService.CurrentVersion})";
        _settings = _settingsService.Load();
        app.UiSoundsEnabled = _settings.UiSoundsEnabled;
        _webSession.RobloxPageLoaded += WebSession_RobloxPageLoaded;
        _activeProfile = FindActiveSavedProfile();
        ShowDestinationForProfile(_activeProfile);
        _destinationTrackingEnabled = true;
        RenderAccountList();
        RenderRecentExperiences();
        Loaded += MainWindow_Loaded;
        Closed += MainWindow_Closed;
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        _soundService.PlayStartup(
            _settings.StartupSound,
            _settings.CustomStartupSoundFileName);
        if (_activeProfile is null)
        {
            SetSignedOutState();
            return;
        }

        await InitializeBrowserAsync(_activeProfile, showLogin: false);
    }

    private async Task InitializeBrowserAsync(AccountProfile profile, bool showLogin)
    {
        _browserSwitchCancellation?.Cancel();
        _browserSwitchCancellation?.Dispose();
        _browserSwitchCancellation = new CancellationTokenSource();
        var cancellationToken = _browserSwitchCancellation.Token;
        _currentUser = null;
        LaunchButton.IsEnabled = false;

        var browser = _webSession.BeginBrowserReplacement();
        BrowserHost.Children.Clear();
        BrowserHost.Children.Add(browser);

        try
        {
            await _webSession.InitializeAsync(
                browser,
                _settingsService.GetSessionDataDirectory(profile),
                showLogin,
                cancellationToken);
        }
        catch (OperationCanceledException)
        {
            // A newer account selection superseded this browser initialization.
        }
        catch (Exception ex)
        {
            SetStatus("Web sign-in could not start", ex.Message, "SESSION ERROR");
            SignInButton.Visibility = Visibility.Visible;
        }
    }

    private async void WebSession_RobloxPageLoaded(object? sender, EventArgs e) =>
        await CheckAuthenticatedAccountAsync(skipIfBusy: true);

    private async Task CheckAuthenticatedAccountAsync(bool skipIfBusy = false)
    {
        if (!_webSession.IsReady)
            return;

        if (skipIfBusy)
        {
            if (!await _accountCheckLock.WaitAsync(0))
                return;
        }
        else
        {
            await _accountCheckLock.WaitAsync();
        }

        try
        {
            _currentUser = await _webSession.GetAuthenticatedUserAsync();
            if (_currentUser is null)
            {
                SetSignedOutState();
                return;
            }

            if (_pendingProfile is not null)
            {
                var duplicate = _settings.Accounts.FirstOrDefault(
                    account => account.UserId == _currentUser.Id);
                if (duplicate is not null)
                {
                    SetStatus(
                        "Account already added",
                        $"@{_currentUser.Name} already has a saved account slot. Sign out on the Roblox page and use a different account.",
                        "DUPLICATE ACCOUNT");
                    LaunchButton.IsEnabled = false;
                    return;
                }

                _pendingProfile.UserId = _currentUser.Id;
                _pendingProfile.Username = _currentUser.Name;
                _settings.Accounts.Add(_pendingProfile);
                _settings.ActiveAccountKey = _pendingProfile.Key;
                _activeProfile = _pendingProfile;
                _pendingProfile = null;
                _settingsService.Save(_settings);
                RenderAccountList();
            }

            if (_activeProfile is null || _activeProfile.UserId != _currentUser.Id)
            {
                SetStatus(
                    "Different account detected",
                    $"This slot belongs to @{_activeProfile?.Username}. Sign out of @{_currentUser.Name} and reconnect the correct account.",
                    "ACCOUNT BLOCKED");
                LaunchButton.IsEnabled = false;
                SignInButton.Visibility = Visibility.Visible;
                SignInButtonLabel.Text = "Fix sign-in";
                AutomationProperties.SetName(SignInButton, "Fix Roblox sign-in");
                return;
            }

            BrowserPanel.Visibility = Visibility.Collapsed;
            LauncherPanel.Visibility = Visibility.Visible;
            SetReadyState();
        }
        catch
        {
            SetSignedOutState();
        }
        finally
        {
            _accountCheckLock.Release();
        }
    }

    private void SetSignedOutState()
    {
        var profile = _pendingProfile ?? _activeProfile;
        SetStatus(
            profile is null || _pendingProfile is not null
                ? "Connect a Roblox account"
                : $"Reconnect @{profile.Username}",
            _pendingProfile is not null
                ? "Sign in to the second Roblox account. It will keep its own isolated session."
                : profile is null
                    ? "Add an account to create the first isolated Roblox session."
                    : "This account slot's Roblox session has expired. Sign back into the same account.",
            "SIGN-IN NEEDED");
        LaunchButton.IsEnabled = false;
        SignInButton.Visibility = Visibility.Visible;
        SignInButtonLabel.Text = "Sign in";
        AutomationProperties.SetName(SignInButton, "Sign in to Roblox");
    }

    private void SetReadyState()
    {
        if (_currentUser is null || _activeProfile is null)
            return;

        SetStatus(
            $"Active account: @{_currentUser.Name}",
            _robloxClient.FindPlayerPath() is not null
                ? "Verified. Only this selected account will be used for the next launch."
                : "Install Roblox Player before launching an experience.",
            _launchInProgress ? "LAUNCHING" : "ACCOUNT VERIFIED");
        SignInButton.Visibility = Visibility.Collapsed;
        RefreshLaunchAvailability();
        LaunchButtonLabel.Text = _launchInProgress ? "Launching…" : "Launch";
    }

    private void SetStatus(string title, string detail, string badge)
    {
        StatusTitle.Text = title;
        StatusDetail.Text = detail;
        SessionBadge.Text = badge;

        var isError =
            badge.Contains("ERROR", StringComparison.OrdinalIgnoreCase) ||
            badge.Contains("BLOCKED", StringComparison.OrdinalIgnoreCase) ||
            badge.Contains("INVALID", StringComparison.OrdinalIgnoreCase) ||
            badge.Contains("REQUIRED", StringComparison.OrdinalIgnoreCase);
        var isSuccess =
            badge.Contains("VERIFIED", StringComparison.OrdinalIgnoreCase) ||
            badge.Contains("READY", StringComparison.OrdinalIgnoreCase) ||
            badge.Contains("STARTED", StringComparison.OrdinalIgnoreCase) ||
            badge.Contains("CLOSED", StringComparison.OrdinalIgnoreCase);

        var accent = ColorConverter.ConvertFromString(
            isError ? "#FF7188" : isSuccess ? "#57D9A3" : "#9A89FF");
        var surface = ColorConverter.ConvertFromString(
            isError ? "#2A171D" : isSuccess ? "#15271F" : "#211D39");
        var accentBrush = new SolidColorBrush((Color)accent);
        var surfaceBrush = new SolidColorBrush((Color)surface);
        SessionBadge.Foreground = accentBrush;
        SessionBadgeBorder.Background = surfaceBrush;
        StatusIconGlyph.Data = (Geometry)FindResource(
            isError ? "IconError" : isSuccess ? "IconCheck" : "IconActivity");
        StatusIconGlyph.Stroke = accentBrush;
        StatusIconBorder.Background = surfaceBrush;
    }

    private void RenderAccountList()
    {
        AccountsList.Children.Clear();
        foreach (var account in _settings.Accounts)
            AccountsList.Children.Add(CreateAccountButton(account, account.Key == _settings.ActiveAccountKey));

        if (_pendingProfile is not null)
            AccountsList.Children.Add(CreateAccountButton(_pendingProfile, selected: true, pending: true));

        AddAccountButton.IsEnabled = !_operationBusy && _pendingProfile is null;
        EditAccountButton.IsEnabled =
            !_operationBusy && _pendingProfile is null && _activeProfile is not null;
    }

    private Button CreateAccountButton(
        AccountProfile account,
        bool selected,
        bool pending = false)
    {
        var button = new Button
        {
            Tag = account.Key,
            Background = Brushes.Transparent,
            Padding = new Thickness(0),
            Margin = new Thickness(0, 0, 0, 7),
            IsEnabled = !pending,
            HorizontalContentAlignment = HorizontalAlignment.Stretch
        };
        button.Click += AccountButton_Click;
        AutomationProperties.SetName(
            button,
            pending
                ? "New account sign-in"
                : $"Select {account.Label ?? $"@{account.Username}"}");

        var accountColor = (Color)ColorConverter.ConvertFromString(
            account.ColorHex ?? "#7C5CFC");

        var border = new Border
        {
            Background = new SolidColorBrush(
                (Color)ColorConverter.ConvertFromString(selected ? "#211D39" : "#151A24")),
            BorderBrush = new SolidColorBrush(
                selected ? accountColor : (Color)ColorConverter.ConvertFromString("#151A24")),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(14),
            Padding = new Thickness(14),
            MinHeight = 64,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(38) });
        grid.ColumnDefinitions.Add(new ColumnDefinition());

        var dot = new Border
        {
            Width = 34,
            Height = 34,
            CornerRadius = new CornerRadius(17),
            Background = new SolidColorBrush(accountColor)
        };
        dot.Child = CreateAccountIndicator(pending, selected);

        var labels = new StackPanel { Margin = new Thickness(10, 0, 0, 0) };
        labels.Children.Add(new TextBlock
        {
            Text = pending
                ? "New account"
                : account.Label ?? $"@{account.Username}",
            Foreground = Brushes.White,
            FontWeight = FontWeights.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis
        });
        labels.Children.Add(new TextBlock
        {
            Text = pending
                ? "Finish sign-in"
                : account.Label is null
                    ? $"User ID {account.UserId}"
                    : $"@{account.Username}  •  User ID {account.UserId}",
            Foreground = (Brush)FindResource("MutedBrush"),
            FontSize = 12
        });
        Grid.SetColumn(labels, 1);
        grid.Children.Add(dot);
        grid.Children.Add(labels);
        border.Child = grid;
        button.Content = border;
        return button;
    }

    private void EditAccountButton_Click(object sender, RoutedEventArgs e)
    {
        if (_operationBusy || _pendingProfile is not null || _activeProfile is null)
            return;

        var dialog = new AccountAppearanceDialog(_activeProfile) { Owner = this };
        if (dialog.ShowDialog() != true)
            return;

        _activeProfile.Label = dialog.AccountLabel;
        _activeProfile.ColorHex = dialog.SelectedColor;
        try
        {
            _settingsService.Save(_settings);
        }
        catch (Exception ex)
        {
            SetStatus("Account appearance could not be saved", ex.Message, "SETTINGS ERROR");
        }
        RenderAccountList();
    }

    private void SoundSettingsButton_Click(object sender, RoutedEventArgs e)
    {
        if (_operationBusy)
            return;

        var dialog = new SoundSettingsDialog(
            _soundService,
            _settings.UiSoundsEnabled,
            _settings.StartupSound,
            _settings.CustomStartupSoundFileName)
        {
            Owner = this
        };
        if (dialog.ShowDialog() != true)
            return;

        try
        {
            var customFileName = _settings.CustomStartupSoundFileName;
            if (dialog.PendingCustomSourcePath is not null)
            {
                _soundService.StopPreview();
                customFileName = _soundService.ImportStartupSound(
                    dialog.PendingCustomSourcePath);
            }

            _settings.UiSoundsEnabled = dialog.UiSoundsEnabled;
            _settings.StartupSound = dialog.StartupSound;
            _settings.CustomStartupSoundFileName = customFileName;
            _settingsService.Save(_settings);
            ((App)Application.Current).UiSoundsEnabled = dialog.UiSoundsEnabled;
            SetStatus(
                "Sound settings saved",
                "Your interface and startup sound choices stay on this PC.",
                "SETTINGS SAVED");
        }
        catch (Exception ex) when (
            ex is System.IO.IOException or UnauthorizedAccessException or ArgumentException)
        {
            SetStatus(
                "Sound settings could not be saved",
                ex.Message,
                "SETTINGS ERROR");
        }
    }

    private UIElement CreateAccountIndicator(bool pending, bool selected)
    {
        if (pending)
        {
            return new Path
            {
                Data = (Geometry)FindResource("IconAdd"),
                Stroke = Brushes.White,
                StrokeThickness = 2,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
                Stretch = Stretch.Uniform,
                Margin = new Thickness(9)
            };
        }

        if (!selected)
        {
            return new Ellipse
            {
                Width = 8,
                Height = 8,
                Fill = Brushes.White,
                Opacity = 0.78,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
        }

        return new Path
        {
            Data = (Geometry)FindResource("IconCheck"),
            Stroke = new SolidColorBrush(Color.FromRgb(87, 217, 163)),
            StrokeThickness = 2.4,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            StrokeLineJoin = PenLineJoin.Round,
            Stretch = Stretch.Uniform,
            Margin = new Thickness(8)
        };
    }

    private async void AccountButton_Click(object sender, RoutedEventArgs e)
    {
        if (_operationBusy)
            return;
        if (sender is not Button { Tag: string key })
            return;
        var profile = _settings.Accounts.FirstOrDefault(account => account.Key == key);
        if (profile is null || profile == _activeProfile)
            return;

        _activeProfile = profile;
        _pendingProfile = null;
        _settings.ActiveAccountKey = profile.Key;
        ShowDestinationForProfile(profile);
        _settingsService.Save(_settings);
        RenderAccountList();
        SetStatus($"Switching to @{profile.Username}", "Loading its isolated Roblox session…", "SWITCHING");
        await InitializeBrowserAsync(profile, showLogin: false);
    }

    private async void AddAccountButton_Click(object sender, RoutedEventArgs e)
    {
        if (_operationBusy || _pendingProfile is not null)
            return;

        var key = Guid.NewGuid().ToString("N");
        _pendingProfile = new AccountProfile
        {
            Key = key,
            SessionFolder = $@"Profiles\{key}",
            Destination = GetMostRecentDestination()
        };
        _activeProfile = _pendingProfile;
        _currentUser = null;
        ShowDestinationForProfile(_pendingProfile);
        RenderAccountList();
        LauncherPanel.Visibility = Visibility.Collapsed;
        BrowserPanel.Visibility = Visibility.Visible;
        await InitializeBrowserAsync(_pendingProfile, showLogin: true);
    }

    private async void SignInButton_Click(object sender, RoutedEventArgs e)
    {
        if (_operationBusy)
            return;
        if (_activeProfile is null)
        {
            AddAccountButton_Click(sender, e);
            return;
        }

        LauncherPanel.Visibility = Visibility.Collapsed;
        BrowserPanel.Visibility = Visibility.Visible;
        if (!_webSession.IsReady)
            await InitializeBrowserAsync(_activeProfile, showLogin: true);
        else
            _webSession.NavigateToLogin();
    }

    private async void BrowserBackButton_Click(object sender, RoutedEventArgs e)
    {
        if (_pendingProfile is null)
        {
            BrowserPanel.Visibility = Visibility.Collapsed;
            LauncherPanel.Visibility = Visibility.Visible;
            return;
        }

        if (!await ClearCurrentBrowserProfileAsync())
        {
            SetStatus(
                "Temporary session could not be cleared",
                "Keep this window open and try Back again before closing Roblox One.",
                "CLEANUP ERROR");
            return;
        }

        BrowserPanel.Visibility = Visibility.Collapsed;
        LauncherPanel.Visibility = Visibility.Visible;
        _pendingProfile = null;
        _activeProfile = FindActiveSavedProfile();
        ShowDestinationForProfile(_activeProfile);
        RenderAccountList();
        if (_activeProfile is not null)
            await InitializeBrowserAsync(_activeProfile, showLogin: false);
        else
            SetSignedOutState();
    }

    private async void LaunchButton_Click(object sender, RoutedEventArgs e)
    {
        if (_operationBusy)
            return;
        SetOperationBusy(true);
        try
        {
            await LaunchAsync();
        }
        finally
        {
            SetOperationBusy(false);
        }
    }

    private async Task LaunchAsync()
    {
        var accountDestination = PlaceIdBox.Text.Trim();
        if (!TryResolveLaunchInput(
                accountDestination,
                out var destination,
                out var target,
                out var serverJobId,
                out var trackedPlaceId,
                out var parseError))
        {
            SetStatus("Destination is not valid", parseError, "INVALID DESTINATION");
            return;
        }

        await CheckAuthenticatedAccountAsync();
        var currentUser = _currentUser;
        var activeProfile = _activeProfile;
        if (currentUser is null || activeProfile is null ||
            currentUser.Id != activeProfile.UserId)
            return;

        activeProfile.Destination = accountDestination;
        _settingsService.Save(_settings);
        _launchInProgress = true;
        SetReadyState();

        if (target!.ShareCode is not null)
        {
            SetStatus(
                "Resolving private-server link",
                "Looking up the hidden experience and private-server link code…",
                "RESOLVING SERVER");
            LaunchButton.IsEnabled = false;
            target = await _webSession.ResolvePrivateServerAsync(target.ShareCode);
            if (target is null)
            {
                _launchInProgress = false;
                SetStatus(
                    "Private-server link could not be resolved",
                    "The code may be invalid, expired, or unavailable to the selected account.",
                    "SERVER LINK ERROR");
                LaunchButtonLabel.Text = "Launch";
                LaunchButton.IsEnabled = true;
                return;
            }
        }

        if (trackedPlaceId is not null && target.PlaceId != trackedPlaceId)
        {
            _launchInProgress = false;
            SetStatus(
                "Tracked server does not match its experience",
                "The saved server record is inconsistent and was not launched.",
                "SERVER RECORD ERROR");
            LaunchButtonLabel.Text = "Launch";
            return;
        }

        SetStatus(
            serverJobId is null
                ? $"Preparing @{currentUser.Name}"
                : $"Rejoining server as @{currentUser.Name}",
            serverJobId is null
                ? "Requesting a secure Roblox game-client ticket for the selected account…"
                : $"Targeting tracked server {serverJobId[..8]}… with a fresh account ticket.",
            "GETTING TICKET");
        LaunchButton.IsEnabled = false;

        var ticketTask = _webSession.GetAuthenticationTicketAsync();
        var nameTask = TryGetExperienceNameAsync(target.PlaceId);
        var ticket = await ticketTask;
        if (string.IsNullOrWhiteSpace(ticket))
        {
            _launchInProgress = false;
            SetStatus(
                "Roblox did not issue a launch ticket",
                "Refresh this account slot by signing in again, then retry.",
                "TICKET ERROR");
            SignInButton.Visibility = Visibility.Visible;
            SignInButtonLabel.Text = "Refresh sign-in";
            AutomationProperties.SetName(SignInButton, "Refresh Roblox sign-in");
            LaunchButtonLabel.Text = "Launch";
            return;
        }

        var recent = new RecentExperience
        {
            Destination = destination,
            PlaceId = target.PlaceId,
            Name = await nameTask,
            IsPrivateServer = target.IsPrivateServer,
            ServerJobId = serverJobId,
            AccountUserId = currentUser.Id,
            AccountUsername = currentUser.Name,
            LastLaunchedAt = DateTimeOffset.UtcNow
        };
        await LaunchClientAsync(
            RobloxLaunchUriBuilder.Build(target, ticket, serverJobId),
            recent);
    }

    private async Task<string?> TryGetExperienceNameAsync(long placeId)
    {
        try
        {
            return await _webSession.GetExperienceNameAsync(placeId);
        }
        catch
        {
            return null;
        }
    }

    private async Task LaunchClientAsync(
        string launchUri,
        RecentExperience recent)
    {
        SetStatus(
            $"Launching as @{_currentUser?.Name}",
            "Handing this destination directly to Roblox Player…",
            "STARTING CLIENT");
        LaunchButton.IsEnabled = false;

        var launchStartedAt = DateTimeOffset.UtcNow;
        var result = await _robloxClient.LaunchAsync(launchUri);
        _launchInProgress = false;
        if (result is { Success: true, ProcessId: int processId })
        {
            SaveRecentExperience(recent);
            BeginServerTracking(recent, launchStartedAt);
            SetReadyState();
            SessionBadge.Text = "CLIENT STARTED";
            var accountLabel = _activeProfile?.Label;
            _ = Task.Run(() => NotifyLaunchHookAsync(
                recent,
                processId,
                accountLabel,
                _launchHookCancellation.Token));
            return;
        }

        LaunchButtonLabel.Text = "Launch";
        LaunchButton.IsEnabled = true;
        SetStatus("Roblox Player is unavailable", result.Error!, "CLIENT ERROR");
    }

    private async Task NotifyLaunchHookAsync(
        RecentExperience recent,
        int processId,
        string? accountLabel,
        CancellationToken cancellationToken)
    {
        try
        {
            await _launchHook.NotifyLaunchAsync(new LaunchHookEvent(
                Guid.NewGuid().ToString("N"),
                DateTimeOffset.UtcNow,
                processId,
                recent.PlaceId,
                recent.CustomName ?? recent.Name,
                recent.IsPrivateServer,
                recent.AccountUserId,
                recent.AccountUsername ?? string.Empty,
                accountLabel),
                cancellationToken);
        }
        catch
        {
            // A custom local integration must never change launch success.
        }
    }

    private void BeginServerTracking(
        RecentExperience recent,
        DateTimeOffset launchStartedAt) =>
        _ = TrackJoinedServerAsync(recent, launchStartedAt);

    private async Task TrackJoinedServerAsync(
        RecentExperience recent,
        DateTimeOffset launchStartedAt)
    {
        try
        {
            var serverJobId = await _serverTracker.FindJoinedServerAsync(
                recent.AccountUserId,
                recent.PlaceId,
                launchStartedAt,
                _launchHookCancellation.Token);
            if (serverJobId is null ||
                !_settings.RecentExperiences.Contains(recent))
            {
                return;
            }

            recent.ServerJobId = serverJobId;
            SaveRecentMetadata(showError: false);
        }
        catch (OperationCanceledException)
        {
            // App shutdown cancels optional local server detection.
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.WriteLine(
                $"Roblox server detection failed: {ex.GetType().Name}.");
        }
    }

    private async void ResetButton_Click(object sender, RoutedEventArgs e)
    {
        if (_operationBusy)
            return;
        var profile = _activeProfile;
        if (profile is null || _pendingProfile is not null)
            return;

        var result = MessageBox.Show(
            $"Remove @{profile.Username} from Roblox One? Its isolated sign-in will be cleared.",
            "Remove account", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (result != MessageBoxResult.Yes)
            return;

        SetOperationBusy(true);
        try
        {
            if (!await ClearCurrentBrowserProfileAsync())
            {
                SetStatus(
                    "Account data could not be cleared",
                    "The account was not removed. Restart Roblox One and try again.",
                    "CLEANUP ERROR");
                return;
            }

            _settings.Accounts.RemoveAll(account => account.Key == profile.Key);
            _activeProfile = _settings.Accounts.FirstOrDefault();
            _settings.ActiveAccountKey = _activeProfile?.Key;
            ShowDestinationForProfile(_activeProfile);
            _settingsService.Save(_settings);
            _currentUser = null;
            RenderAccountList();
            if (_activeProfile is not null)
                await InitializeBrowserAsync(_activeProfile, showLogin: false);
            else
                SetSignedOutState();
        }
        finally
        {
            SetOperationBusy(false);
        }
    }

    private async Task<bool> ClearCurrentBrowserProfileAsync()
        => await _webSession.ClearProfileAsync();

    private void PlaceIdBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        TrackDestinationForActiveProfile();
        if (LaunchButton is not null)
            RefreshLaunchAvailability();
    }

    private void PlaceIdBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || !LaunchButton.IsEnabled)
            return;

        e.Handled = true;
        LaunchButton_Click(LaunchButton, new RoutedEventArgs());
    }

    private bool IsValidPlaceId() =>
        TryResolveLaunchInput(
            PlaceIdBox.Text,
            out _,
            out _,
            out _,
            out _,
            out _);

    private bool TryResolveLaunchInput(
        string input,
        out string destination,
        out LaunchTarget? target,
        out string? serverJobId,
        out long? trackedPlaceId,
        out string error)
    {
        destination = input.Trim();
        serverJobId = null;
        trackedPlaceId = null;

        if (RecentServerJoinResolver.TryResolve(
                destination,
                _settings.RecentExperiences,
                out var trackedServer))
        {
            destination = trackedServer!.Destination;
            serverJobId = Guid.Parse(trackedServer.ServerJobId!).ToString("D");
            trackedPlaceId = trackedServer.PlaceId;
        }

        return DestinationParser.TryParse(destination, out target, out error);
    }

    private void TrackDestinationForActiveProfile()
    {
        if (!_destinationTrackingEnabled ||
            _activeProfile is null ||
            _pendingProfile is not null)
        {
            return;
        }

        var destination = PlaceIdBox.Text.Trim();
        if (destination.Length == 0)
        {
            _activeProfile.Destination = null;
        }
        else if (DestinationParser.TryParse(destination, out _, out _))
        {
            _activeProfile.Destination = destination;
        }
    }

    private void ShowDestinationForProfile(AccountProfile? profile)
    {
        var trackingWasEnabled = _destinationTrackingEnabled;
        _destinationTrackingEnabled = false;
        PlaceIdBox.Text = profile?.Destination ?? string.Empty;
        _destinationTrackingEnabled = trackingWasEnabled;
        ResetDestinationViewport();
    }

    private void ResetDestinationViewport()
    {
        PlaceIdBox.CaretIndex = 0;
        PlaceIdBox.ScrollToHome();
        PlaceIdBox.Dispatcher.BeginInvoke(() =>
        {
            PlaceIdBox.CaretIndex = 0;
            PlaceIdBox.ScrollToHome();
        });
    }

    private string? GetMostRecentDestination() =>
        _settings.RecentExperiences
            .OrderByDescending(item => item.LastLaunchedAt)
            .Select(item => item.Destination)
            .FirstOrDefault();

    private void RefreshLaunchAvailability()
    {
        if (LaunchButton is null)
            return;

        LaunchButton.IsEnabled =
            !_operationBusy &&
            !_launchInProgress &&
            _currentUser?.Id == _activeProfile?.UserId &&
            IsValidPlaceId();
        BatchLaunchButton.IsEnabled =
            !_operationBusy &&
            !_launchInProgress &&
            _pendingProfile is null &&
            _settings.Accounts.Count >= 2 &&
            IsValidPlaceId();
    }

    private void SetOperationBusy(bool busy)
    {
        _operationBusy = busy;
        AccountsList.IsEnabled = !busy;
        AddAccountButton.IsEnabled = !busy && _pendingProfile is null;
        EditAccountButton.IsEnabled =
            !busy && _pendingProfile is null && _activeProfile is not null;
        CloseAllInstancesButton.IsEnabled = !busy;
        ResetButton.IsEnabled = !busy && _activeProfile is not null;
        SignInButton.IsEnabled = !busy;
        PlaceIdBox.IsEnabled = !busy;
        LaunchTabButton.IsEnabled = !busy;
        RecentTabButton.IsEnabled = !busy;
        RecentExperiencesList.IsEnabled = !busy;
        UpdateClearHistoryButton();
        BatchLaunchButton.IsEnabled = !busy;
        SoundSettingsButton.IsEnabled = !busy;
        InstallUpdateButton.IsEnabled = !busy;
        RefreshLaunchAvailability();
    }

    private AccountProfile? FindActiveSavedProfile() =>
        _settings.Accounts.FirstOrDefault(
            account => account.Key == _settings.ActiveAccountKey)
        ?? _settings.Accounts.FirstOrDefault();

    private void MainWindow_Closed(object? sender, EventArgs e)
    {
        TrackDestinationForActiveProfile();
        try
        {
            _settingsService.Save(_settings);
        }
        catch
        {
            // Closing must continue even if local settings are unavailable.
        }
        _browserSwitchCancellation?.Cancel();
        _browserSwitchCancellation?.Dispose();
        _webSession.RobloxPageLoaded -= WebSession_RobloxPageLoaded;
        _webSession.Dispose();
        _launchHookCancellation.Cancel();
        _launchHook.Dispose();
        _launchHookCancellation.Dispose();
        _updateService.Dispose();
    }

}
