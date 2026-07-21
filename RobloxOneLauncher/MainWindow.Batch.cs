using System.Diagnostics;
using System.Windows;
using RobloxOneLauncher.Models;
using RobloxOneLauncher.Services;

namespace RobloxOneLauncher;

public partial class MainWindow
{
    private async void BatchLaunchButton_Click(object sender, RoutedEventArgs e)
    {
        if (_operationBusy || _pendingProfile is not null)
            return;
        var accountDestination = PlaceIdBox.Text.Trim();
        if (!TryResolveLaunchInput(
                accountDestination,
                out var destination,
                out var parsedTarget,
                out var serverJobId,
                out var trackedPlaceId,
                out var parseError))
        {
            SetStatus("Destination is not valid", parseError, "INVALID DESTINATION");
            return;
        }

        var dialog = new BatchLaunchDialog(_settings.Accounts) { Owner = this };
        if (dialog.ShowDialog() != true)
            return;

        var originalProfile = _activeProfile;
        _batchCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            _launchHookCancellation.Token);
        SetOperationBusy(true);
        CancelBatchButton.Visibility = Visibility.Visible;
        CancelBatchButton.IsEnabled = true;
        BatchLaunchResult? result = null;
        var restoredOriginalProfile = true;
        try
        {
            result = await RunBatchLaunchAsync(
                dialog.SelectedAccounts,
                accountDestination,
                destination,
                parsedTarget!,
                serverJobId,
                trackedPlaceId,
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
            if (!_launchHookCancellation.IsCancellationRequested)
            {
                try
                {
                    restoredOriginalProfile = await RestoreBatchProfileAsync(
                        originalProfile,
                        _launchHookCancellation.Token);
                }
                catch (OperationCanceledException) when (
                    _launchHookCancellation.IsCancellationRequested)
                {
                    restoredOriginalProfile = false;
                }
            }
            _batchCancellation.Dispose();
            _batchCancellation = null;
            CancelBatchButton.Visibility = Visibility.Collapsed;
            SetOperationBusy(false);
        }

        if (_launchHookCancellation.IsCancellationRequested)
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
            "Roblox One will stop before the next safe step. Clients already started remain open.",
            "BATCH CANCELLING");
        _batchCancellation.Cancel();
    }

    private async Task<BatchLaunchResult> RunBatchLaunchAsync(
        IReadOnlyList<AccountProfile> accounts,
        string accountDestination,
        string destination,
        LaunchTarget parsedTarget,
        string? serverJobId,
        long? trackedPlaceId,
        TimeSpan delay,
        CancellationToken cancellationToken)
    {
        var unavailableAccounts = await PreflightBatchAccountsAsync(
            accounts,
            parsedTarget,
            trackedPlaceId,
            cancellationToken);
        if (unavailableAccounts.Count > 0)
        {
            return new BatchLaunchResult(
                0,
                accounts.Count,
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
                "Roblox One could not verify that all current clients were closed.",
                "BATCH ERROR");
            return new BatchLaunchResult(
                0,
                accounts.Count,
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
                accounts.Count,
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

        foreach (var account in accounts)
            account.Destination = accountDestination;
        _settingsService.Save(_settings);

        var started = 0;
        var failures = new List<string>();
        for (var index = 0; index < accounts.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var account = accounts[index];
            var position = $"{index + 1} of {accounts.Count}";
            SetStatus(
                $"Batch {position}: {GetAccountDisplayName(account)}",
                "Loading this account's isolated Roblox session…",
                "BATCH SWITCH");

            bool launched;
            try
            {
                if (!await ActivateBatchAccountAsync(account, cancellationToken))
                {
                    failures.Add($"@{account.Username}: sign-in unavailable");
                    continue;
                }

                launched = await LaunchBatchAccountAsync(
                    account,
                    destination,
                    parsedTarget,
                    serverJobId,
                    trackedPlaceId,
                    position,
                    cancellationToken);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                Trace.WriteLine(
                    $"Batch launch failed for one account: {ex.GetType().Name}.");
                launched = false;
            }
            if (launched)
                started++;
            else
                failures.Add($"@{account.Username}: launch failed");

            if (launched && index < accounts.Count - 1)
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
            accounts.Count,
            failures,
            ClientsWereClosed: true,
            Cancelled: false);
    }

    private async Task<List<string>> PreflightBatchAccountsAsync(
        IReadOnlyList<AccountProfile> accounts,
        LaunchTarget parsedTarget,
        long? trackedPlaceId,
        CancellationToken cancellationToken)
    {
        var unavailable = new List<string>();
        for (var index = 0; index < accounts.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var account = accounts[index];
            SetStatus(
                $"Checking account {index + 1} of {accounts.Count}",
                $"Verifying {GetAccountDisplayName(account)} before any running client is closed…",
                "BATCH CHECK");
            try
            {
                if (!await ActivateBatchAccountAsync(account, cancellationToken))
                {
                    unavailable.Add($"@{account.Username}: sign-in unavailable");
                    continue;
                }

                if (parsedTarget.ShareCode is not null)
                {
                    var resolvedTarget = await _webSession.ResolvePrivateServerAsync(
                        parsedTarget.ShareCode,
                        cancellationToken);
                    if (resolvedTarget is null ||
                        trackedPlaceId is not null &&
                        resolvedTarget.PlaceId != trackedPlaceId)
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
            catch (Exception ex)
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
            var restored = await ActivateBatchAccountAsync(profile, cancellationToken);
            ShowDestinationForProfile(profile);
            return restored;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Trace.WriteLine(
                $"Batch account restore failed: {ex.GetType().Name}.");
            ShowDestinationForProfile(profile);
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

    private async Task<bool> ActivateBatchAccountAsync(
        AccountProfile account,
        CancellationToken cancellationToken)
    {
        var pageLoaded = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        void PageLoadedHandler(object? sender, EventArgs args) =>
            pageLoaded.TrySetResult();

        _webSession.RobloxPageLoaded += PageLoadedHandler;
        try
        {
            _activeProfile = account;
            _pendingProfile = null;
            _currentUser = null;
            _settings.ActiveAccountKey = account.Key;
            _settingsService.Save(_settings);
            RenderAccountList();

            await InitializeBrowserAsync(account, showLogin: false);
            await pageLoaded.Task.WaitAsync(
                TimeSpan.FromSeconds(20),
                cancellationToken);
            await CheckAuthenticatedAccountAsync();
            return _currentUser?.Id == account.UserId;
        }
        catch (TimeoutException)
        {
            SetStatus(
                $"Could not load @{account.Username}",
                "Its Roblox session did not become ready within 20 seconds.",
                "BATCH ACCOUNT ERROR");
            return false;
        }
        finally
        {
            _webSession.RobloxPageLoaded -= PageLoadedHandler;
        }
    }

    private async Task<bool> LaunchBatchAccountAsync(
        AccountProfile account,
        string destination,
        LaunchTarget parsedTarget,
        string? serverJobId,
        long? trackedPlaceId,
        string position,
        CancellationToken cancellationToken)
    {
        var target = parsedTarget;
        if (target.ShareCode is not null)
        {
            SetStatus(
                $"Batch {position}: resolving server",
                $"Resolving the private-server link for @{account.Username}…",
                "BATCH RESOLVE");
            target = await _webSession.ResolvePrivateServerAsync(
                target.ShareCode,
                cancellationToken);
            if (target is null)
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
            var ticketTask = _webSession.GetAuthenticationTicketAsync(cancellationToken);
            var nameTask = TryGetExperienceNameAsync(target.PlaceId);
            var localeTask = _webSession.GetUserLocaleAsync(cancellationToken);
            await Task.WhenAll(ticketTask, localeTask);
            var ticket = ticketTask.Result;
            if (string.IsNullOrWhiteSpace(ticket))
                return false;

            var recent = new RecentExperience
            {
                Destination = destination,
                PlaceId = target.PlaceId,
                Name = await nameTask,
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
            var launchStartedAt = DateTimeOffset.UtcNow;
            var locale = localeTask.Result;
            var result = await _robloxClient.LaunchAsync(
                RobloxLaunchUriBuilder.Build(
                    target,
                    ticket,
                    serverJobId,
                    locale));
            if (result is not { Success: true, ProcessId: int processId })
                return false;

            SaveRecentExperience(recent);
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
