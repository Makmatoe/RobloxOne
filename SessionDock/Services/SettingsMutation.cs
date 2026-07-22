using SessionDock.Models;

namespace SessionDock.Services;

internal static class SettingsMutation
{
    internal static async Task<SettingsMutationResult> TryCommitAsync(
        AppSettings settings,
        Action mutation,
        Func<AppSettings, Task> saveAsync)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(mutation);
        ArgumentNullException.ThrowIfNull(saveAsync);

        var checkpoint = SettingsCheckpoint.Capture(settings);
        try
        {
            mutation();
        }
        catch
        {
            checkpoint.Restore(settings);
            throw;
        }

        try
        {
            await saveAsync(settings);
            return new SettingsMutationResult(true, null);
        }
        catch (Exception exception) when (
            LocalDataException.IsExpectedPersistenceFailure(exception))
        {
            checkpoint.Restore(settings);
            return new SettingsMutationResult(false, exception);
        }
        catch
        {
            checkpoint.Restore(settings);
            throw;
        }
    }

    internal static bool TryCommit(
        AppSettings settings,
        Action mutation,
        Action<AppSettings> save,
        out Exception? failure)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(mutation);
        ArgumentNullException.ThrowIfNull(save);

        var checkpoint = SettingsCheckpoint.Capture(settings);
        try
        {
            mutation();
        }
        catch
        {
            checkpoint.Restore(settings);
            throw;
        }

        try
        {
            save(settings);
            failure = null;
            return true;
        }
        catch (Exception exception) when (
            LocalDataException.IsExpectedPersistenceFailure(exception))
        {
            checkpoint.Restore(settings);
            failure = exception;
            return false;
        }
        catch
        {
            checkpoint.Restore(settings);
            throw;
        }
    }

    private sealed class SettingsCheckpoint
    {
        private readonly AppSettings _state;
        private readonly List<AccountProfile> _originalAccounts;
        private readonly List<RecentExperience> _originalRecentExperiences;

        private SettingsCheckpoint(AppSettings settings)
        {
            _originalAccounts = [.. settings.Accounts];
            _originalRecentExperiences = [.. settings.RecentExperiences];
            _state = AppSettingsSnapshot.Create(settings);
        }

        internal static SettingsCheckpoint Capture(AppSettings settings) =>
            new(settings);

        internal void Restore(AppSettings settings)
            => AppSettingsSnapshot.Restore(
                _state,
                settings,
                _originalAccounts,
                _originalRecentExperiences);
    }
}

internal readonly record struct SettingsMutationResult(
    bool Committed,
    Exception? Failure,
    bool Closed = false)
{
    internal static SettingsMutationResult ClosedResult =>
        new(false, null, true);
}
