using SessionDock.Models;

namespace SessionDock.Services;

internal static class SettingsMutation
{
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
        private readonly List<AccountEntry> _accounts;
        private readonly string? _activeAccountKey;
        private readonly List<RecentEntry> _recentExperiences;
        private readonly bool _uiSoundsEnabled;
        private readonly string _startupSound;
        private readonly string? _customStartupSoundFileName;
        private readonly long? _lockedUserId;
        private readonly string? _lockedUsername;
        private readonly long? _placeId;
        private readonly string? _destination;

        private SettingsCheckpoint(AppSettings settings)
        {
            _accounts = settings.Accounts
                .Select(account => new AccountEntry(
                    account,
                    AppSettingsSnapshot.Clone(account)))
                .ToList();
            _activeAccountKey = settings.ActiveAccountKey;
            _recentExperiences = settings.RecentExperiences
                .Select(recent => new RecentEntry(
                    recent,
                    AppSettingsSnapshot.Clone(recent)))
                .ToList();
            _uiSoundsEnabled = settings.UiSoundsEnabled;
            _startupSound = settings.StartupSound;
            _customStartupSoundFileName = settings.CustomStartupSoundFileName;
            _lockedUserId = settings.LockedUserId;
            _lockedUsername = settings.LockedUsername;
            _placeId = settings.PlaceId;
            _destination = settings.Destination;
        }

        internal static SettingsCheckpoint Capture(AppSettings settings) =>
            new(settings);

        internal void Restore(AppSettings settings)
        {
            foreach (var entry in _accounts)
                AppSettingsSnapshot.Copy(entry.State, entry.Target);
            settings.Accounts = _accounts
                .Select(entry => entry.Target)
                .ToList();
            settings.ActiveAccountKey = _activeAccountKey;

            foreach (var entry in _recentExperiences)
                AppSettingsSnapshot.Copy(entry.State, entry.Target);
            settings.RecentExperiences = _recentExperiences
                .Select(entry => entry.Target)
                .ToList();

            settings.UiSoundsEnabled = _uiSoundsEnabled;
            settings.StartupSound = _startupSound;
            settings.CustomStartupSoundFileName =
                _customStartupSoundFileName;
            settings.LockedUserId = _lockedUserId;
            settings.LockedUsername = _lockedUsername;
            settings.PlaceId = _placeId;
            settings.Destination = _destination;
        }

        private sealed record AccountEntry(
            AccountProfile Target,
            AccountProfile State);

        private sealed record RecentEntry(
            RecentExperience Target,
            RecentExperience State);
    }
}
