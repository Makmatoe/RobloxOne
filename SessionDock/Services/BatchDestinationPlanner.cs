using SessionDock.Models;

namespace SessionDock.Services;

public sealed record BatchLaunchPlan(
    AccountProfile Account,
    ResolvedLaunchInput LaunchInput,
    bool UsesFirstDestination);

public static class BatchDestinationPlanner
{
    public static bool TryCreate(
        IReadOnlyList<AccountProfile> accounts,
        IEnumerable<RecentExperience> recentExperiences,
        out IReadOnlyList<BatchLaunchPlan> plans,
        out string error)
    {
        ArgumentNullException.ThrowIfNull(accounts);
        ArgumentNullException.ThrowIfNull(recentExperiences);
        plans = [];

        if (accounts.Count == 0)
        {
            error = "Select at least one account for the batch.";
            return false;
        }

        var firstDestination = accounts[0].Destination?.Trim();
        if (string.IsNullOrWhiteSpace(firstDestination))
        {
            error =
                $"{GetDisplayName(accounts[0])} is the first selected account and needs a destination.";
            return false;
        }

        var recent = recentExperiences as IReadOnlyList<RecentExperience>
            ?? recentExperiences.ToArray();
        var resolvedPlans = new List<BatchLaunchPlan>(accounts.Count);
        foreach (var account in accounts)
        {
            var savedDestination = account.Destination?.Trim();
            var usesFirstDestination = string.IsNullOrWhiteSpace(savedDestination);
            var launchDestination = usesFirstDestination
                ? firstDestination
                : savedDestination;
            if (!LaunchInputResolver.TryResolve(
                    launchDestination!,
                    recent,
                    out var resolved,
                    out var resolutionError))
            {
                error = $"{GetDisplayName(account)}: {resolutionError}";
                return false;
            }

            resolvedPlans.Add(new BatchLaunchPlan(
                account,
                resolved!,
                usesFirstDestination));
        }

        plans = resolvedPlans;
        error = string.Empty;
        return true;
    }

    private static string GetDisplayName(AccountProfile account) =>
        account.Label is null
            ? $"@{account.Username}"
            : $"{account.Label} (@{account.Username})";
}
