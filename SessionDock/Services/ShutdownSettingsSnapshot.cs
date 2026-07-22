using SessionDock.Models;

namespace SessionDock.Services;

internal static class ShutdownSettingsSnapshot
{
    internal static AppSettings Create(
        AppSettings settings,
        DestinationPersistenceRequest? capturedDestinationRequest,
        DestinationPersistenceRequest? currentDestinationRequest)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var snapshot = AppSettingsSnapshot.Create(settings);
        if (capturedDestinationRequest is null ||
            capturedDestinationRequest != currentDestinationRequest)
        {
            return snapshot;
        }

        var profile = snapshot.Accounts.FirstOrDefault(account =>
            account.Key.Equals(
                capturedDestinationRequest.AccountKey,
                StringComparison.OrdinalIgnoreCase));
        if (profile is not null)
            profile.Destination = capturedDestinationRequest.Destination;
        return snapshot;
    }
}
