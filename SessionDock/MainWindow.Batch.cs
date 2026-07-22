using System.Diagnostics;
using System.Windows;
using SessionDock.Models;
using SessionDock.Services;

namespace SessionDock;

public partial class MainWindow
{
    private async void BatchLaunchButton_Click(object sender, RoutedEventArgs e) =>
        await RunWindowOperationAsync(BatchLaunchButtonClickAsync);

    private async Task BatchLaunchButtonClickAsync(
        CancellationToken cancellationToken)
    {
        if (_operationBusy || _pendingProfile is not null)
            return;

        if (!await FlushDestinationPersistenceAsync())
            return;
        cancellationToken.ThrowIfCancellationRequested();
        if (_operationBusy || _pendingProfile is not null)
            return;
        var dialog = new BatchLaunchDialog(_settings.Accounts) { Owner = this };
        if (dialog.ShowDialog() != true)
            return;
        if (!BatchDestinationPlanner.TryCreate(
                dialog.SelectedAccounts,
                _settings.RecentExperiences,
                out var launchPlans,
                out var planningError))
        {
            SetStatus(
                "Batch destinations are not ready",
                planningError,
                "INVALID DESTINATION");
            return;
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (_operationBusy || _pendingProfile is not null)
            return;

        var originalProfile = _activeProfile;
        _batchCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken);
        SetOperationBusy(true);
        CancelBatchButton.Visibility = Visibility.Visible;
        CancelBatchButton.IsEnabled = true;
        BatchLaunchResult? result = null;
        var restoredOriginalProfile = true;
        try
        {
            result = await RunBatchLaunchAsync(
                launchPlans,
                dialog.Delay,
                _batchCancellation.Token);
        }
        catch (OperationCanceledException)
        {
            result = BatchLaunchResult.CancelledResult(dialog.SelectedAccounts.Count);
        }
        finally
        {
            _launchInProgress = false;
            CancelBatchButton.IsEnabled = false;
            if (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    restoredOriginalProfile = await RestoreBatchProfileAsync(
                        originalProfile,
                        cancellationToken);
                }
                catch (OperationCanceledException) when (
                    cancellationToken.IsCancellationRequested)
                {
                    restoredOriginalProfile = false;
                }
            }
            _batchCancellation.Dispose();
            _batchCancellation = null;
            if (!_operationLifetime.IsShuttingDown)
            {
                CancelBatchButton.Visibility = Visibility.Collapsed;
                SetOperationBusy(false);
            }
        }

        if (cancellationToken.IsCancellationRequested)
            return;

        ShowBatchResult(
            result ?? BatchLaunchResult.CancelledResult(dialog.SelectedAccounts.Count),
            restoredOriginalProfile);
    }

    private void CancelBatchButton_Click(object sender, RoutedEventArgs e)
    {
        if (_batchCancellation is null || _batchCancellation.IsCancellationRequested)
            return;

        CancelBatchButton.IsEnabled = false;
        SetStatus(
            "Cancelling batch launch",
            "SessionDock will stop before the next safe step. Clients already started remain open.",
            "BATCH CANCELLING");
        _batchCancellation.Cancel();
    }

    private async Task<BatchLaunchResult> RunBatchLaunchAsync(
        IReadOnlyList<BatchLaunchPlan> launchPlans,
        TimeSpan delay,
        CancellationToken cancellationToken)
    {
        var unavailableAccounts = await PreflightBatchAccountsAsync(
            launchPlans,
            cancellationToken);
        if (unavailableAccounts.Count > 0)
        {
            return new BatchLaunchResult(
                0,
                launchPlans.Count,
                unavailableAccounts,
                ClientsWereClosed: false,
                Cancelled: false);
        }

        SetStatus(
            "Preparing batch launch",
            "Closing every running, verified Roblox Player instance…",
            "BATCH CLEANUP");
        RobloxClientService.ClosePlayersResult closeResult;
        try
        {
            closeResult = await _robloxClient.CloseAllPlayersAsync(
                cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Trace.WriteLine(
                $"Roblox batch cleanup failed: {ex.GetType().Name}.");
            SetStatus(
                "Batch launch stopped",
                "SessionDock could not verify that all current clients were closed.",
                "BATCH ERROR");
            return new BatchLaunchResult(
                0,
                launchPlans.Count,
                ["Existing Roblox clients could not be verified as closed"],
                ClientsWereClosed: false,
                Cancelled: false);
        }

        if (!closeResult.Success)
        {
            var detail = closeResult.Unverified > 0
                ? $"{closeResult.Unverified} Roblox-named process(es) could not be safely verified and were left running."
                : $"{closeResult.Remaining} verified Roblox Player instance(s) could not be closed.";
            SetStatus(
                "Batch launch stopped",
                detail,
                "BATCH ERROR");
            return new BatchLaunchResult(
                0,
                launchPlans.Count,
                [detail],
                ClientsWereClosed: false,
                Cancelled: false);
        }

        if (closeResult.Closed > 0)
        {
            SetStatus(
                $"Closed {closeResult.Closed} Roblox Player instance(s)",
                "Waiting two seconds for Roblox background cleanup to settle…",
                "BATCH CLEANUP");
            await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
        }

        var started = 0;
        var failures = new List<string>();
        for (var index = 0; index < launchPlans.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var plan = launchPlans[index];
            var account = _settings.Accounts.FirstOrDefault(candidate =>
                candidate.Key.Equals(
                    plan.Account.Key,
                    StringComparison.OrdinalIgnoreCase)) ?? plan.Account;
            var position = $"{index + 1} of {launchPlans.Count}";
            SetStatus(
                $"Batch {position}: {GetAccountDisplayName(account)}",
                "Loading this account's isolated Roblox session…",
                "BATCH SWITCH");

            bool launched;
            try
            {
                var sessionToken = await ActivateBatchAccountAsync(
                    account.Key,
                    cancellationToken);
                if (sessionToken is null)
                {
                    failures.Add($"@{account.Username}: sign-in unavailable");
                    continue;
                }

                var currentAccount = _settings.Accounts.FirstOrDefault(candidate =>
                    candidate.Key.Equals(
                        account.Key,
                        StringComparison.OrdinalIgnoreCase));
                if (currentAccount is null)
                {
                    failures.Add($"@{plan.Account.Username}: account unavailable");
                    continue;
                }
                account = currentAccount;

                launched = await LaunchBatchAccountAsync(
                    account,
                    sessionToken.Value,
                    plan.LaunchInput.Destination,
                    plan.LaunchInput.Target,
                    plan.LaunchInput.ServerJobId,
                    plan.LaunchInput.TrackedPlaceId,
                    position,
                    cancellationToken);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex) when (
                WebSessionException.IsExpectedLifecycleFailure(ex))
            {
                Trace.WriteLine(
                    $"Batch launch failed for one account: {ex.GetType().Name}.");
                launched = false;
            }
            if (launched)
                started++;
            else
                failures.Add($"@{account.Username}: launch failed");

            if (launched && index < launchPlans.Count - 1)
            {
                SetStatus(
                    $"Batch {position}: @{account.Username} started",
                    $"Waiting {delay.TotalSeconds:0} seconds before switching accounts so Roblox and local hooks can settle…",
                    "BATCH WAIT");
                await Task.Delay(delay, cancellationToken);
            }
        }

        return new BatchLaunchResult(
            started,
            launchPlans.Count,
            failures,
            ClientsWereClosed: true,
            Cancelled: false);
    }

    private async Task<List<string>> PreflightBatchAccountsAsync(
        IReadOnlyList<BatchLaunchPlan> launchPlans,
        CancellationToken cancellationToken)
    {
        var unavailable = new List<string>();
        for (var index = 0; index < launchPlans.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var plan = launchPlans[index];
            var account = _settings.Accounts.FirstOrDefault(candidate =>
                candidate.Key.Equals(
                    plan.Account.Key,
                    StringComparison.OrdinalIgnoreCase)) ?? plan.Account;
            SetStatus(
                $"Checking account {index + 1} of {launchPlans.Count}",
                $"Verifying {GetAccountDisplayName(account)} before any running client is closed…",
                "BATCH CHECK");
            try
            {
                var sessionToken = await ActivateBatchAccountAsync(
                    account.Key,
                    cancellationToken);
                if (sessionToken is null)
                {
                    unavailable.Add($"@{account.Username}: sign-in unavailable");
                    continue;
                }

                var currentAccount = _settings.Accounts.FirstOrDefault(candidate =>
                    candidate.Key.Equals(
                        account.Key,
                        StringComparison.OrdinalIgnoreCase));
                if (currentAccount is null ||
                    !IsCurrentWebSessionOwner(sessionToken.Value) ||
                    !TryGetCurrentWebSessionToken(
                        currentAccount,
                        out var currentToken) ||
                    currentToken != sessionToken.Value)
                {
                    unavailable.Add(
                        $"@{plan.Account.Username}: account session unavailable");
                    continue;
                }
                account = currentAccount;
                var activeSessionToken = sessionToken.Value;

                if (plan.LaunchInput.Target.ShareCode is not null)
                {
                    if (!IsCurrentWebSessionOwner(activeSessionToken))
                    {
                        unavailable.Add(
                            $"@{account.Username}: account session unavailable");
                        continue;
                    }
                    var resolvedTarget = await _webSession.ResolvePrivateServerAsync(
                        plan.LaunchInput.Target.ShareCode,
                        activeSessionToken,
                        cancellationToken);
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!IsCurrentWebSessionOwner(activeSessionToken))
                    {
                        unavailable.Add(
                            $"@{account.Username}: account session unavailable");
                        continue;
                    }
                    if (resolvedTarget is null ||
                        plan.LaunchInput.TrackedPlaceId is not null &&
                        resolvedTarget.PlaceId != plan.LaunchInput.TrackedPlaceId)
                    {
                        unavailable.Add(
                            $"@{account.Username}: private server unavailable");
                    }
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex) when (
                WebSessionException.IsExpectedLifecycleFailure(ex))
            {
                Trace.WriteLine(
                    $"Batch preflight failed for one account: {ex.GetType().Name}.");
                unavailable.Add($"@{account.Username}: account check failed");
            }
        }
        return unavailable;
    }

    private async Task<bool> RestoreBatchProfileAsync(
        AccountProfile? profile,
        CancellationToken cancellationToken)
    {
        if (profile is null)
            return true;

        SetStatus(
            $"Restoring {GetAccountDisplayName(profile)}",
            "Returning the launcher to the account that was selected before the batch…",
            "BATCH RESTORE");
        try
        {
            var restoredToken = await ActivateBatchAccountAsync(
                profile.Key,
                cancellationToken);
            var restoredProfile = _settings.Accounts.FirstOrDefault(account =>
                account.Key.Equals(
                    profile.Key,
                    StringComparison.OrdinalIgnoreCase));
            ShowDestinationForProfile(restoredProfile ?? profile);
            return restoredToken is not null && restoredProfile is not null;
        }
        catch (Exception ex) when (
            WebSessionException.IsExpectedLifecycleFailure(ex))
        {
            Trace.WriteLine(
                $"Batch account restore failed: {ex.GetType().Name}.");
            var restoredProfile = _settings.Accounts.FirstOrDefault(account =>
                account.Key.Equals(
                    profile.Key,
                    StringComparison.OrdinalIgnoreCase));
            ShowDestinationForProfile(restoredProfile ?? profile);
            return false;
        }
    }

    private void ShowBatchResult(
        BatchLaunchResult result,
        bool restoredOriginalProfile)
    {
        var restoreDetail = restoredOriginalProfile
            ? string.Empty
            : " The original account needs to be signed in again.";
        if (result.Cancelled)
        {
            SetStatus(
                "Batch launch cancelled",
                $"No further accounts will start. Started clients remain open; clients already closed during cleanup cannot be restored.{restoreDetail}",
                "BATCH CANCELLED");
            return;
        }

        if (result.Failures.Count == 0)
        {
            SetStatus(
                $"Batch complete: {result.Started} clients started",
                $"Every selected account received its own launch ticket.{restoreDetail}",
                "BATCH COMPLETE");
            return;
        }

        var title = result.ClientsWereClosed
            ? $"Batch complete: {result.Started} of {result.Total} started"
            : "Batch not started";
        SetStatus(
            title,
            $"{string.Join("; ", result.Failures)}.{restoreDetail}".Trim(),
            result.Started > 0 ? "BATCH PARTIAL" : "BATCH ERROR");
    }

    private async Task<WebSessionToken?> ActivateBatchAccountAsync(
        string accountKey,
        CancellationToken cancellationToken)
    {
        var pageLoaded = new TaskCompletionSource<WebSessionToken>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var observedTokens = new HashSet<WebSessionToken>();
        WebSessionToken? expectedToken = null;
        void PageLoadedHandler(object? sender, WebSessionEventArgs args)
        {
            if (!args.Token.AccountKey.Equals(
                    accountKey,
                    StringComparison.OrdinalIgnoreCase) ||
                !IsCurrentWebSessionOwner(args.Token))
            {
                return;
            }

            if (expectedToken is { } token)
            {
                if (args.Token == token)
                    pageLoaded.TrySetResult(args.Token);
                return;
            }

            observedTokens.Add(args.Token);
        }

        _webSession.RobloxPageLoaded += PageLoadedHandler;
        AccountProfile? account = null;
        try
        {
            var mutationApplied = false;
            if (!await TryCommitSettingsMutationAsync(
                    () =>
                    {
                        account = _settings.Accounts.FirstOrDefault(candidate =>
                            candidate.Key.Equals(
                                accountKey,
                                StringComparison.OrdinalIgnoreCase));
                        if (account is null)
                            return;

                        _settings.ActiveAccountKey = account.Key;
                        mutationApplied = true;
                    },
                    "Could not activate the selected account",
                    "BATCH ACCOUNT ERROR",
                    onCommitted: () =>
                    {
                        if (!mutationApplied || account is null)
                            return;
                        _activeProfile = account;
                        _pendingProfile = null;
                        _currentUser = null;
                        RenderAccountList();
                    }) ||
                !mutationApplied ||
                account is null)
            {
                return null;
            }

            cancellationToken.ThrowIfCancellationRequested();
            // This activation performs an awaited verification below. Skip only
            // its first automatic page-load check so the same Roblox request is
            // not serialized twice; later navigations remain observable.
            using var automaticCheckSuppression =
                _accountVerificationGate.SuppressNextAutomaticVerification(
                    account.Key);
            var initialization = InitializeBrowserAsync(
                account,
                showLogin: false,
                cancellationToken);
            if (!TryGetAffineWebSessionToken(account, out var sessionToken))
            {
                await initialization;
                return null;
            }
            expectedToken = sessionToken;
            if (observedTokens.Contains(sessionToken) &&
                IsCurrentWebSessionOwner(sessionToken))
            {
                pageLoaded.TrySetResult(sessionToken);
            }

            if (!await initialization ||
                !IsCurrentWebSessionOwner(sessionToken))
            {
                return null;
            }

            var sessionEnded = _webSession.GetSessionEndedTask(sessionToken);
            if (!await RobloxWebSessionService.WaitForSessionWorkAsync(
                    pageLoaded.Task,
                    sessionEnded,
                    TimeSpan.FromSeconds(20),
                    cancellationToken))
            {
                return null;
            }
            var loadedToken = await pageLoaded.Task;
            cancellationToken.ThrowIfCancellationRequested();
            if (loadedToken != sessionToken ||
                !IsCurrentWebSessionOwner(sessionToken))
            {
                return null;
            }

            await CheckAuthenticatedAccountAsync(
                sessionToken,
                skipIfBusy: false,
                cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            return IsCurrentWebSessionOwner(sessionToken) &&
                   _activeProfile?.Key.Equals(
                       account.Key,
                       StringComparison.OrdinalIgnoreCase) == true &&
                   _currentUser?.Id == account.UserId
                ? sessionToken
                : null;
        }
        catch (TimeoutException)
        {
            SetStatus(
                account is null
                    ? "Could not load the selected account"
                    : $"Could not load @{account.Username}",
                "Its Roblox session did not become ready within 20 seconds.",
                "BATCH ACCOUNT ERROR");
            return null;
        }
        finally
        {
            _webSession.RobloxPageLoaded -= PageLoadedHandler;
        }
    }

    private async Task<bool> LaunchBatchAccountAsync(
        AccountProfile account,
        WebSessionToken sessionToken,
        string destination,
        LaunchTarget parsedTarget,
        string? serverJobId,
        long? trackedPlaceId,
        string position,
        CancellationToken cancellationToken)
    {
        var currentAccount = _settings.Accounts.FirstOrDefault(candidate =>
            candidate.Key.Equals(
                account.Key,
                StringComparison.OrdinalIgnoreCase));
        if (currentAccount is null ||
            !IsCurrentWebSessionOwner(sessionToken) ||
            !TryGetCurrentWebSessionToken(
                currentAccount,
                out var currentToken) ||
            currentToken != sessionToken)
        {
            return false;
        }
        account = currentAccount;

        var target = parsedTarget;
        if (target.ShareCode is not null)
        {
            SetStatus(
                $"Batch {position}: resolving server",
                $"Resolving the private-server link for @{account.Username}…",
                "BATCH RESOLVE");
            if (!IsCurrentWebSessionOwner(sessionToken))
                return false;
            target = await _webSession.ResolvePrivateServerAsync(
                target.ShareCode,
                sessionToken,
                cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (!IsCurrentWebSessionOwner(sessionToken) || target is null)
                return false;
        }

        if (trackedPlaceId is not null && target.PlaceId != trackedPlaceId)
            return false;

        _launchInProgress = true;
        SetStatus(
            $"Batch {position}: preparing @{account.Username}",
            "Requesting this account's secure Roblox game-client ticket…",
            "BATCH TICKET");

        try
        {
            if (!IsCurrentWebSessionOwner(sessionToken))
                return false;
            var ticketTask = _webSession.GetAuthenticationTicketAsync(
                sessionToken,
                cancellationToken);
            if (!IsCurrentWebSessionOwner(sessionToken))
                return false;
            var nameTask = _webSession.GetExperienceNameAsync(
                target.PlaceId,
                sessionToken,
                cancellationToken);
            if (!IsCurrentWebSessionOwner(sessionToken))
                return false;
            var localeTask = _webSession.GetUserLocaleAsync(
                sessionToken,
                cancellationToken);
            await Task.WhenAll(ticketTask, nameTask, localeTask);
            cancellationToken.ThrowIfCancellationRequested();
            if (!IsCurrentWebSessionOwner(sessionToken))
                return false;

            var ticket = await ticketTask;
            if (!IsCurrentWebSessionOwner(sessionToken))
                return false;
            if (string.IsNullOrWhiteSpace(ticket))
                return false;

            var locale = await localeTask;
            if (!IsCurrentWebSessionOwner(sessionToken))
                return false;
            var experienceName = await nameTask;
            if (!IsCurrentWebSessionOwner(sessionToken))
                return false;

            var recent = new RecentExperience
            {
                Destination = destination,
                PlaceId = target.PlaceId,
                Name = experienceName,
                IsPrivateServer = target.IsPrivateServer,
                ServerJobId = serverJobId,
                AccountUserId = account.UserId,
                AccountUsername = account.Username,
                LastLaunchedAt = DateTimeOffset.UtcNow
            };

            SetStatus(
                $"Batch {position}: launching @{account.Username}",
                "Handing this account's ticket directly to Roblox Player…",
                "BATCH LAUNCH");
            if (!IsCurrentWebSessionOwner(sessionToken))
                return false;
            var launchStartedAt = DateTimeOffset.UtcNow;
            var result = await _robloxClient.LaunchAsync(
                RobloxLaunchUriBuilder.Build(
                    target,
                    ticket,
                    serverJobId,
                    locale),
                cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (result is not { Success: true, ProcessId: int processId })
                return false;

            await SaveRecentExperienceAsync(recent);
            if (_settings.RecentExperiences.Contains(recent))
                BeginServerTracking(recent, launchStartedAt);
            SetStatus(
                $"Batch {position}: @{account.Username} started",
                "Waiting for optional local launch hooks to finish…",
                "BATCH HOOK");
            await NotifyLaunchHookAsync(
                recent,
                processId,
                account.Label,
                cancellationToken);
            return true;
        }
        finally
        {
            _launchInProgress = false;
        }
    }

    private static string GetAccountDisplayName(AccountProfile account) =>
        account.Label is null
            ? $"@{account.Username}"
            : $"{account.Label} (@{account.Username})";

    private sealed record BatchLaunchResult(
        int Started,
        int Total,
        IReadOnlyList<string> Failures,
        bool ClientsWereClosed,
        bool Cancelled)
    {
        public static BatchLaunchResult CancelledResult(int total) =>
            new(0, total, [], ClientsWereClosed: false, Cancelled: true);
    }
}
