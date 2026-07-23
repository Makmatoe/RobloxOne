namespace SessionDock.Services;

internal enum JoinUserAvailability
{
    Available,
    UserNotFound,
    Offline,
    NotInExperience,
    NotJoinable,
    ServiceUnavailable
}

internal sealed record JoinUserResolution(
    long UserId,
    string Username,
    string DisplayName,
    long PlaceId,
    string ServerJobId);

internal sealed record JoinUserLookupResult(
    JoinUserAvailability Availability,
    JoinUserResolution? Resolution)
{
    internal static JoinUserLookupResult Unavailable(
        JoinUserAvailability availability) => new(availability, null);
}
