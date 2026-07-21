using System.ComponentModel;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using SessionDock.Models;
using SessionDock.Services;
using SessionDock.SystemProcesses;

namespace SessionDock;

public partial class MainWindow : Window
{
    private static readonly TimeSpan OperationShutdownTimeout =
        TimeSpan.FromSeconds(2);
    private static readonly TimeSpan PendingProfileCleanupTimeout =
        TimeSpan.FromSeconds(2);
    private static readonly TimeSpan SettingsSaveShutdownTimeout =
        TimeSpan.FromSeconds(2);
    private readonly SettingsService _settingsService = new();
    private readonly RobloxClientService _robloxClient = new();
    private readonly RobloxServerTracker _serverTracker = new();
    private readonly RobloxWebSessionService _webSession = new();
    private readonly UiSoundService _soundService;
    private readonly CompositeLaunchHook _launchHook = new(
        new HandleScopeLaunchHook(),
        new LocalApiLaunchHook());
    private readonly WindowOperationLifetime _operationLifetime = new();
    private readonly SemaphoreSlim _accountCheckLock = new(1, 1);
    private readonly AppSettings _settings;
    private readonly string? _startupNotice;
    private AccountProfile? _activeProfile;
    private AccountProfile? _pendingProfile;
    private RobloxUser? _currentUser;
    private CancellationTokenSource? _browserSwitchCancellation;
    private CancellationTokenSource? _batchCancellation;
    private bool _launchInProgress;
    private bool _operationBusy;
    private bool _destinationTrackingEnabled;
    private bool _shutdownComplete;

    public MainWindow()
    {
        InitializeComponent();
        WindowLayoutService.FitToWorkArea(this);
        var app = (App)Application.Current;
        _soundService = app.SoundService;
        InstallUpdateButton.ToolTip =
            $"Check for signed updates (current {_updateService.CurrentVersion})";
        _settings = _settingsService.Load();
        var removedOrphanedProfiles =
            _settingsService.CleanupOrphanedSessionDirectories(_settings);
        _startupNotice = removedOrphanedProfiles > 0
            ? string.Join(
                Environment.NewLine + Environment.NewLine,
                new[]
                {
                    _settingsService.LoadNotice,
                    $"Removed {removedOrphanedProfiles} incomplete local account profile(s) left by an interrupted sign-in."
                }.Where(message => !string.IsNullOrWhiteSpace(message)))
            : _settingsService.LoadNotice;
        app.UiSoundsEnabled = _settings.UiSoundsEnabled;
        _webSession.RobloxPageLoaded += WebSession_RobloxPageLoaded;
        _activeProfile = FindActiveSavedProfile();
        ShowDestinationForProfile(_activeProfile);
        _destinationTrackingEnabled = true;
        RenderAccountList();
        RenderRecentExperiences();
        Loaded += MainWindow_Loaded;
        Closing += MainWindow_Closing;
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e) =>
        await RunWindowOperationAsync(MainWindowLoadedAsync);

    private async Task MainWindowLoadedAsync(CancellationToken cancellationToken)
    {
        _soundService.PlayStartup(
            _settings.StartupSound,
            _settings.CustomStartupSoundFileName);
        if (!string.IsNullOrWhiteSpace(_startupNotice))
        {
            MessageBox.Show(
                this,
                _startupNotice,
                "Local settings recovery",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
        if (_activeProfile is null)
        {
            SetSignedOutState();
            return;
        }

        await InitializeBrowserAsync(
            _activeProfile,
            showLogin: false,
            cancellationToken);
    }

    private async Task InitializeBrowserAsync(
        AccountProfile profile,
        bool showLogin,
        CancellationToken cancellationToken = default)
    {
        _browserSwitchCancellation?.Cancel();
        _browserSwitchCancellation?.Dispose();
        _browserSwitchCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            _operationLifetime.Token,
            cancellationToken);
        var browserCancellationToken = _browserSwitchCancellation.Token;
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
                browserCancellationToken);
        }
        catch (OperationCanceledException)
        {
            // A newer account selection superseded this browser initialization.
        }
        catch (Exception ex) when (!_operationLifetime.IsShuttingDown)
        {
            SetStatus("Web sign-in could not start", ex.Message, "SESSION ERROR");
            SignInButton.Visibility = Visibility.Visible;
        }
    }

    private async void WebSession_RobloxPageLoaded(object? sender, EventArgs e) =>
        await RunWindowOperationAsync(cancellationToken =>
            CheckAuthenticatedAccountAsync(
                skipIfBusy: true,
                cancellationToken));

    private async Task CheckAuthenticatedAccountAsync(
        bool skipIfBusy = false,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!_webSession.IsReady)
            return;

        if (skipIfBusy)
        {
            if (!await _accountCheckLock.WaitAsync(0, cancellationToken))
                return;
        }
        else
        {
            await _accountCheckLock.WaitAsync(cancellationToken);
        }

        try
        {
            try
            {
                _currentUser = await _webSession.GetAuthenticatedUserAsync(
                    cancellationToken);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception) when (
                IsExpectedWebSessionFailure(exception))
            {
                System.Diagnostics.Trace.WriteLine(
                    $"Account verification failed safely: {exception.GetType().Name}.");
                SetSignedOutState();
                return;
            }
            cancellationToken.ThrowIfCancellationRequested();
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

                var completedProfile = _pendingProfile;
                completedProfile.UserId = _currentUser.Id;
                completedProfile.Username = _currentUser.Name;
                if (!TryCommitSettingsMutation(
                        () =>
                        {
                            _settings.Accounts.Add(completedProfile);
                            _settings.ActiveAccountKey = completedProfile.Key;
                        },
                        "Account could not be saved",
                        "ACCOUNT SAVE ERROR",
                        "The Roblox sign-in succeeded, but SessionDock could not save this account slot. The temporary slot was kept so you can retry after making %LOCALAPPDATA%\\SessionDock writable."))
                {
                    LaunchButton.IsEnabled = false;
                    return;
                }

                _activeProfile = completedProfile;
                _pendingProfile = null;
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
        finally
        {
            _accountCheckLock.Release();
        }
    }

    private static bool IsExpectedWebSessionFailure(Exception exception) =>
        exception is InvalidOperationException or
            System.Runtime.InteropServices.COMException;

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

        var tone = StatusToneClassifier.Classify(badge);
        var isError = tone == StatusTone.Error;
        var isSuccess = tone == StatusTone.Success;
        var isWarning = tone == StatusTone.Warning;

        var accent = ColorConverter.ConvertFromString(
            isError
                ? "#FF7188"
                : isSuccess
                    ? "#57D9A3"
                    : isWarning
                        ? "#E0A33A"
                        : "#9A89FF");
        var surface = ColorConverter.ConvertFromString(
            isError
                ? "#2A171D"
                : isSuccess
                    ? "#15271F"
                    : isWarning
                        ? "#2A2215"
                        : "#211D39");
        var accentBrush = new SolidColorBrush((Color)accent);
        var surfaceBrush = new SolidColorBrush((Color)surface);
        SessionBadge.Foreground = accentBrush;
        SessionBadgeBorder.Background = surfaceBrush;
        StatusIconGlyph.Data = (Geometry)FindResource(
            isError ? "IconError" : isSuccess ? "IconCheck" : "IconActivity");
        StatusIconGlyph.Stroke = accentBrush;
        StatusIconBorder.Background = surfaceBrush;
    }

    private Task RunWindowOperationAsync(
        Func<CancellationToken, Task> operation) =>
        _operationLifetime.RunAsync(
            operation,
            HandleExpectedWindowOperationFailure);

    private void HandleExpectedWindowOperationFailure(Exception exception)
    {
        System.Diagnostics.Trace.WriteLine(
            $"Local operation failed safely: {exception.GetType().Name}.");
        SetStatus(
            "Local operation could not be completed",
            "SessionDock could not access a required local file or folder. Check that %LOCALAPPDATA%\\SessionDock is writable and not locked, then try again.",
            "LOCAL DATA ERROR");
    }

    private bool TryCommitSettingsMutation(
        Action mutation,
        string failureTitle,
        string failureBadge = "SETTINGS ERROR",
        string? failureDetail = null,
        bool showFailure = true)
    {
        if (SettingsMutation.TryCommit(
                _settings,
                mutation,
                _settingsService.Save,
                out var failure))
        {
            return true;
        }

        System.Diagnostics.Trace.WriteLine(
            $"Settings update failed safely: {failure!.GetType().Name}.");
        if (showFailure)
        {
            SetStatus(
                failureTitle,
                failureDetail ??
                    "SessionDock could not confirm the local settings update, so the in-memory change was rolled back. Check that %LOCALAPPDATA%\\SessionDock is writable and not locked, then try again.",
                failureBadge);
        }
        return false;
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
        AutomationProperties.SetItemStatus(
            button,
            selected ? "Selected account" : "Not selected");

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

        var profile = _activeProfile;
        if (!TryCommitSettingsMutation(
                () =>
                {
                    profile.Label = dialog.AccountLabel;
                    profile.ColorHex = dialog.SelectedColor;
                },
                "Account appearance could not be saved"))
        {
            return;
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

        string? stagedCustomFileName = null;
        var settingsCommitted = false;
        try
        {
            var previousCustomFileName =
                _settings.CustomStartupSoundFileName;
            var customFileName = previousCustomFileName;
            if (dialog.PendingCustomSourcePath is not null)
            {
                _soundService.StopPreview();
                stagedCustomFileName = _soundService.ImportStartupSound(
                    dialog.PendingCustomSourcePath);
                customFileName = stagedCustomFileName;
            }

            if (!TryCommitSettingsMutation(
                    () =>
                    {
                        _settings.UiSoundsEnabled = dialog.UiSoundsEnabled;
                        _settings.StartupSound = dialog.StartupSound;
                        _settings.CustomStartupSoundFileName = customFileName;
                    },
                    "Sound settings could not be saved"))
            {
                return;
            }
            settingsCommitted = true;
            if (stagedCustomFileName is not null &&
                !string.Equals(
                    previousCustomFileName,
                    stagedCustomFileName,
                    StringComparison.OrdinalIgnoreCase))
            {
                _soundService.TryDeleteImportedStartupSound(
                    previousCustomFileName);
            }
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
        finally
        {
            if (!settingsCommitted && stagedCustomFileName is not null)
            {
                _soundService.TryDeleteImportedStartupSound(
                    stagedCustomFileName);
            }
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

    private async void AccountButton_Click(object sender, RoutedEventArgs e) =>
        await RunWindowOperationAsync(cancellationToken =>
            AccountButtonClickAsync(sender, cancellationToken));

    private async Task AccountButtonClickAsync(
        object sender,
        CancellationToken cancellationToken)
    {
        if (_operationBusy)
            return;
        if (sender is not Button { Tag: string key })
            return;
        var profile = _settings.Accounts.FirstOrDefault(account => account.Key == key);
        if (profile is null || profile == _activeProfile)
            return;

        if (!TryCommitSettingsMutation(
                () => _settings.ActiveAccountKey = profile.Key,
                $"Could not switch to @{profile.Username}",
                "ACCOUNT SWITCH ERROR"))
        {
            return;
        }

        _activeProfile = profile;
        _pendingProfile = null;
        ShowDestinationForProfile(profile);
        RenderAccountList();
        SetStatus($"Switching to @{profile.Username}", "Loading its isolated Roblox session…", "SWITCHING");
        await InitializeBrowserAsync(
            profile,
            showLogin: false,
            cancellationToken);
    }

    private async void AddAccountButton_Click(object sender, RoutedEventArgs e) =>
        await RunWindowOperationAsync(cancellationToken =>
            AddAccountButtonClickAsync(cancellationToken));

    private async Task AddAccountButtonClickAsync(
        CancellationToken cancellationToken)
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
        await InitializeBrowserAsync(
            _pendingProfile,
            showLogin: true,
            cancellationToken);
    }

    private async void SignInButton_Click(object sender, RoutedEventArgs e) =>
        await RunWindowOperationAsync(cancellationToken =>
            SignInButtonClickAsync(cancellationToken));

    private async Task SignInButtonClickAsync(
        CancellationToken cancellationToken)
    {
        if (_operationBusy)
            return;
        if (_activeProfile is null)
        {
            await AddAccountButtonClickAsync(cancellationToken);
            return;
        }

        LauncherPanel.Visibility = Visibility.Collapsed;
        BrowserPanel.Visibility = Visibility.Visible;
        if (!_webSession.IsReady)
            await InitializeBrowserAsync(
                _activeProfile,
                showLogin: true,
                cancellationToken);
        else
            _webSession.NavigateToLogin();
    }

    private async void BrowserBackButton_Click(object sender, RoutedEventArgs e) =>
        await RunWindowOperationAsync(BrowserBackButtonClickAsync);

    private async Task BrowserBackButtonClickAsync(
        CancellationToken cancellationToken)
    {
        if (_pendingProfile is null)
        {
            BrowserPanel.Visibility = Visibility.Collapsed;
            LauncherPanel.Visibility = Visibility.Visible;
            return;
        }

        if (!await ClearCurrentBrowserProfileAsync(cancellationToken))
        {
            SetStatus(
                "Temporary session could not be cleared",
                "Keep this window open and try Back again before closing SessionDock.",
                "CLEANUP ERROR");
            return;
        }
        cancellationToken.ThrowIfCancellationRequested();

        BrowserPanel.Visibility = Visibility.Collapsed;
        LauncherPanel.Visibility = Visibility.Visible;
        _pendingProfile = null;
        _activeProfile = FindActiveSavedProfile();
        ShowDestinationForProfile(_activeProfile);
        RenderAccountList();
        if (_activeProfile is not null)
            await InitializeBrowserAsync(
                _activeProfile,
                showLogin: false,
                cancellationToken);
        else
            SetSignedOutState();
    }

    private async void LaunchButton_Click(object sender, RoutedEventArgs e) =>
        await RunWindowOperationAsync(LaunchButtonClickAsync);

    private async Task LaunchButtonClickAsync(CancellationToken cancellationToken)
    {
        if (_operationBusy)
            return;
        SetOperationBusy(true);
        try
        {
            await LaunchAsync(cancellationToken);
        }
        finally
        {
            if (!_operationLifetime.IsShuttingDown)
                SetOperationBusy(false);
        }
    }

    private async Task LaunchAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
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

        await CheckAuthenticatedAccountAsync(
            cancellationToken: cancellationToken);
        var currentUser = _currentUser;
        var activeProfile = _activeProfile;
        if (currentUser is null || activeProfile is null ||
            currentUser.Id != activeProfile.UserId)
            return;

        if (!TryCommitSettingsMutation(
                () => activeProfile.Destination = accountDestination,
                "Launch destination could not be saved",
                "LAUNCH SETTINGS ERROR"))
        {
            ShowDestinationForProfile(activeProfile);
            RefreshLaunchAvailability();
            return;
        }
        _launchInProgress = true;
        SetReadyState();

        if (target!.ShareCode is not null)
        {
            SetStatus(
                "Resolving private-server link",
                "Looking up the hidden experience and private-server link code…",
                "RESOLVING SERVER");
            LaunchButton.IsEnabled = false;
            target = await _webSession.ResolvePrivateServerAsync(
                target.ShareCode,
                cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
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

        var ticketTask = _webSession.GetAuthenticationTicketAsync(cancellationToken);
        var nameTask = TryGetExperienceNameAsync(
            target.PlaceId,
            cancellationToken);
        var localeTask = _webSession.GetUserLocaleAsync(cancellationToken);
        await Task.WhenAll(ticketTask, localeTask);
        cancellationToken.ThrowIfCancellationRequested();
        var ticket = ticketTask.Result;
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
        var locale = localeTask.Result;
        await LaunchClientAsync(
            RobloxLaunchUriBuilder.Build(target, ticket, serverJobId, locale),
            recent,
            cancellationToken);
    }

    private async Task<string?> TryGetExperienceNameAsync(
        long placeId,
        CancellationToken cancellationToken)
    {
        try
        {
            return await _webSession.GetExperienceNameAsync(
                placeId,
                cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return null;
        }
    }

    private async Task LaunchClientAsync(
        string launchUri,
        RecentExperience recent,
        CancellationToken cancellationToken)
    {
        SetStatus(
            $"Launching as @{_currentUser?.Name}",
            "Handing this destination directly to Roblox Player…",
            "STARTING CLIENT");
        LaunchButton.IsEnabled = false;

        var launchStartedAt = DateTimeOffset.UtcNow;
        var result = await _robloxClient.LaunchAsync(
            launchUri,
            cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        _launchInProgress = false;
        if (result is { Success: true, ProcessId: int processId })
        {
            SaveRecentExperience(recent);
            BeginServerTracking(recent, launchStartedAt);
            SetStatus(
                "Roblox Player started",
                _launchHook.IsConfigured
                    ? "Running configured local launch integrations…"
                    : "Checking optional local launch integrations…",
                "CLIENT STARTED");
            var accountLabel = _activeProfile?.Label;
            await NotifyLaunchHookAsync(
                recent,
                processId,
                accountLabel,
                cancellationToken);
            SetStatus(
                "Roblox Player started",
                _launchHook.IsConfigured
                    ? "Configured local integrations finished their bounded attempt. They never control launch success."
                    : "No local launch integration is configured, so that step was skipped.",
                "CLIENT STARTED");
            RefreshLaunchAvailability();
            LaunchButtonLabel.Text = "Launch";
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
        DateTimeOffset launchStartedAt)
    {
        var trackingTask = TrackJoinedServerAsync(recent, launchStartedAt);
        _ = trackingTask.ContinueWith(
            completed => System.Diagnostics.Trace.WriteLine(
                $"Roblox server tracking faulted: {completed.Exception?.GetBaseException().GetType().Name}."),
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously |
                TaskContinuationOptions.OnlyOnFaulted,
            TaskScheduler.Default);
    }

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
                _operationLifetime.Token);
            _operationLifetime.Token.ThrowIfCancellationRequested();
            if (serverJobId is null ||
                !_settings.RecentExperiences.Contains(recent))
            {
                return;
            }

            SaveRecentMetadata(
                () => recent.ServerJobId = serverJobId,
                showError: false);
        }
        catch (OperationCanceledException)
        {
            // App shutdown cancels optional local server detection.
        }
        catch (Exception ex) when (
            LocalDataException.IsExpectedPersistenceFailure(ex))
        {
            System.Diagnostics.Trace.WriteLine(
                $"Roblox server detection failed: {ex.GetType().Name}.");
        }
    }

    private async void ResetButton_Click(object sender, RoutedEventArgs e) =>
        await RunWindowOperationAsync(ResetButtonClickAsync);

    private async Task ResetButtonClickAsync(CancellationToken cancellationToken)
    {
        if (_operationBusy)
            return;
        var profile = _activeProfile;
        if (profile is null || _pendingProfile is not null)
            return;

        var result = MessageBox.Show(
            $"Remove @{profile.Username} from SessionDock? Its isolated sign-in will be cleared. Recent and Favorites entries for this account will remain until you remove or clear them.",
            "Remove account", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (result != MessageBoxResult.Yes)
            return;

        SetOperationBusy(true);
        try
        {
            if (!TryCommitSettingsMutation(
                    () =>
                    {
                        _settings.Accounts.RemoveAll(
                            account => account.Key == profile.Key);
                        _settings.ActiveAccountKey =
                            _settings.Accounts.FirstOrDefault()?.Key;
                    },
                    "Account removal could not be saved",
                    "ACCOUNT SAVE ERROR",
                    "SessionDock could not save the account removal, so the account and its isolated sign-in data were left unchanged. Make %LOCALAPPDATA%\\SessionDock writable, then retry."))
            {
                return;
            }

            var profileWasCleared = await ClearBrowserProfileAsync(
                profile,
                cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            _activeProfile = _settings.Accounts.FirstOrDefault();
            ShowDestinationForProfile(_activeProfile);
            _currentUser = null;
            RenderAccountList();
            if (_activeProfile is not null)
                await InitializeBrowserAsync(
                    _activeProfile,
                    showLogin: false,
                    cancellationToken);
            else
                SetSignedOutState();

            if (!profileWasCleared)
            {
                SetStatus(
                    "Account removed; local cleanup is pending",
                    "The account metadata was removed safely, but some isolated browser files are still in use. SessionDock will retry orphan cleanup on a later start.",
                    "CLEANUP WARNING");
            }
        }
        finally
        {
            if (!_operationLifetime.IsShuttingDown)
                SetOperationBusy(false);
        }
    }

    private async Task<bool> ClearCurrentBrowserProfileAsync(
        CancellationToken cancellationToken)
    {
        var profile = _pendingProfile ?? _activeProfile;
        if (profile is null)
            return true;

        return await ClearBrowserProfileAsync(profile, cancellationToken);
    }

    private async Task<bool> ClearBrowserProfileAsync(
        AccountProfile profile,
        CancellationToken cancellationToken)
    {
        await _webSession.ClearProfileAsync(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        _webSession.ReleaseBrowser();
        BrowserHost.Children.Clear();
        var directoryRemoved = await _settingsService.DeleteSessionDataAsync(
            profile,
            cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        return directoryRemoved;
    }

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

    private void SetDestinationForAllButton_Click(object sender, RoutedEventArgs e)
    {
        if (_operationBusy || _launchInProgress || _pendingProfile is not null)
            return;

        if (_settings.Accounts.Count == 0)
        {
            SetStatus(
                "Destination was not changed",
                "Add an account before setting a shared destination.",
                "INVALID DESTINATION");
            RefreshLaunchAvailability();
            return;
        }
        if (!LaunchInputResolver.TryResolve(
                PlaceIdBox.Text,
                _settings.RecentExperiences,
                out var resolved,
                out var error))
        {
            SetStatus("Destination was not changed", error, "INVALID DESTINATION");
            RefreshLaunchAvailability();
            return;
        }

        var assignedCount = _settings.Accounts.Count;
        if (!TryCommitSettingsMutation(
                () =>
                {
                    foreach (var account in _settings.Accounts)
                        account.Destination = resolved!.AccountDestination;
                },
                "Shared destination could not be saved",
                "DESTINATION SAVE ERROR"))
        {
            ShowDestinationForProfile(_activeProfile);
            RefreshLaunchAvailability();
            return;
        }
        ShowDestinationForProfile(_activeProfile);
        RefreshLaunchAvailability();
        SetStatus(
            "Destination set for all accounts",
            $"Saved this destination for {assignedCount} account{(assignedCount == 1 ? string.Empty : "s")}.",
            "DESTINATION SAVED");
    }

    private bool TryResolveLaunchInput(
        string input,
        out string destination,
        out LaunchTarget? target,
        out string? serverJobId,
        out long? trackedPlaceId,
        out string error)
    {
        if (LaunchInputResolver.TryResolve(
                input,
                _settings.RecentExperiences,
                out var resolved,
                out error))
        {
            destination = resolved!.Destination;
            target = resolved.Target;
            serverJobId = resolved.ServerJobId;
            trackedPlaceId = resolved.TrackedPlaceId;
            return true;
        }

        destination = input.Trim();
        target = null;
        serverJobId = null;
        trackedPlaceId = null;
        return false;
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
        else if (LaunchInputResolver.TryResolve(
                     destination,
                     _settings.RecentExperiences,
                     out _,
                     out _))
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

        var destination = PlaceIdBox.Text.Trim();
        var destinationIsValid = LaunchInputResolver.TryResolve(
            destination,
            _settings.RecentExperiences,
            out var resolvedInput,
            out var validationError);
        if (DestinationValidationText is not null)
        {
            DestinationValidationText.Text = validationError;
            DestinationValidationText.Visibility =
                destination.Length > 0 && !destinationIsValid
                    ? Visibility.Visible
                    : Visibility.Collapsed;
        }
        if (ServerJobIdWarningPanel is not null)
        {
            ServerJobIdWarningPanel.Visibility =
                destinationIsValid && resolvedInput?.ServerJobId is not null
                    ? Visibility.Visible
                    : Visibility.Collapsed;
        }

        SetDestinationForAllButton.IsEnabled =
            !_operationBusy &&
            !_launchInProgress &&
            _pendingProfile is null &&
            _settings.Accounts.Count >= 2 &&
            destinationIsValid;

        LaunchButton.IsEnabled =
            !_operationBusy &&
            !_launchInProgress &&
            _currentUser?.Id == _activeProfile?.UserId &&
            destinationIsValid;
        BatchLaunchButton.IsEnabled =
            !_operationBusy &&
            !_launchInProgress &&
            _pendingProfile is null &&
            _settings.Accounts.Count >= 2;
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
        IntegrationsButton.IsEnabled = !busy;
        InstallUpdateButton.IsEnabled = !busy;
        RefreshLaunchAvailability();
    }

    private AccountProfile? FindActiveSavedProfile() =>
        _settings.Accounts.FirstOrDefault(
            account => account.Key == _settings.ActiveAccountKey)
        ?? _settings.Accounts.FirstOrDefault();

    private async void MainWindow_Closing(object? sender, CancelEventArgs e)
    {
        if (_shutdownComplete)
            return;

        e.Cancel = true;
        if (!_operationLifetime.BeginShutdown())
            return;

        // Always leave the original Closing event before Close is requested
        // again, even when there are no active operations to await.
        await Task.Yield();
        try
        {
            await CompleteShutdownAsync();
        }
        catch (Exception exception)
        {
            // A shutdown failure must not keep the process and mutex alive.
            System.Diagnostics.Trace.WriteLine(
                $"Window shutdown failed safely: {exception.GetType().Name}.");
        }
        finally
        {
            _shutdownComplete = true;
            Close();
        }
    }

    private async Task CompleteShutdownAsync()
    {
        CancelScopedOperation(_browserSwitchCancellation);
        CancelScopedOperation(_batchCancellation);
        await _operationLifetime.DrainAsync(OperationShutdownTimeout);
        var incompleteProfile = _pendingProfile;

        try
        {
            TrackDestinationForActiveProfile();
            var shutdownSettings = AppSettingsSnapshot.Create(_settings);
            await BoundedSettingsPersistence.TrySaveAsync(
                () => _settingsService.Save(shutdownSettings),
                SettingsSaveShutdownTimeout);
        }
        catch (Exception exception)
        {
            // Closing must continue even if local settings are unavailable.
            System.Diagnostics.Trace.WriteLine(
                $"Shutdown settings persistence failed: {exception.GetType().Name}.");
        }

        _webSession.RobloxPageLoaded -= WebSession_RobloxPageLoaded;
        try
        {
            BrowserHost.Children.Clear();
        }
        catch
        {
            // Native browser teardown continues below.
        }
        DisposeDuringShutdown(_webSession);
        if (incompleteProfile is not null)
        {
            await PendingProfileCleanup.TryDeleteAsync(
                cancellationToken => _settingsService.DeleteSessionDataAsync(
                    incompleteProfile,
                    cancellationToken),
                PendingProfileCleanupTimeout);
        }

        DisposeDuringShutdown(_browserSwitchCancellation);
        DisposeDuringShutdown(_batchCancellation);
        DisposeDuringShutdown(_launchHook);
        DisposeDuringShutdown(_updateService);
        DisposeDuringShutdown(_operationLifetime);
    }

    private static void CancelScopedOperation(CancellationTokenSource? cancellation)
    {
        try
        {
            cancellation?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // The operation completed while shutdown was taking its snapshot.
        }
    }

    private static void DisposeDuringShutdown(IDisposable? disposable)
    {
        try
        {
            disposable?.Dispose();
        }
        catch
        {
            // One teardown failure must not prevent the remaining releases.
        }
    }

}
