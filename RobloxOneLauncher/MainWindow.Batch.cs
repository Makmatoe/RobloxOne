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

        SetOperationBusy(true);
        try
        {
            await RunBatchLaunchAsync(
                dialog.SelectedAccounts,
                accountDestination,
                destination,
                parsedTarget!,
                serverJobId,
                trackedPlaceId,
                dialog.Delay,
                _launchHookCancellation.Token);
        }
        catch (OperationCanceledException)
        {
            // Closing Roblox One cancels the remaining batch safely.
        }
        finally
        {
            _launchInProgress = false;
            SetOperationBusy(false);
        }
    }

    private async Task RunBatchLaunchAsync(
        IReadOnlyList<AccountProfile> accounts,
        string accountDestination,
        string destination,
        LaunchTarget parsedTarget,
        string? serverJobId,
        long? trackedPlaceId,
        TimeSpan delay,
        CancellationToken cancellationToken)
    {
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
            return;
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
            return;
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

        if (failures.Count == 0)
        {
            SetStatus(
                $"Batch complete: {started} clients started",
                "Every selected account received its own launch ticket.",
                "BATCH COMPLETE");
            return;
        }

        SetStatus(
            $"Batch complete: {started} of {accounts.Count} started",
            string.Join("; ", failures),
            started > 0 ? "BATCH PARTIAL" : "BATCH ERROR");
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
            var ticket = await ticketTask;
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
            var result = await _robloxClient.LaunchAsync(
                RobloxLaunchUriBuilder.Build(target, ticket, serverJobId));
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
}
