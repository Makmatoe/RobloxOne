namespace RobloxOneLauncher.SystemProcesses;

public sealed record LaunchHookEvent(
    string EventId,
    DateTimeOffset OccurredAt,
    int ProcessId,
    long PlaceId,
    string? ExperienceName,
    bool IsPrivateServer,
    long AccountUserId,
    string AccountUsername,
    string? AccountLabel)
{
    public string EventType => "roblox_launch";
}
