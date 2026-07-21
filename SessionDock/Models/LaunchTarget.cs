namespace SessionDock.Models;

public sealed record LaunchTarget(long PlaceId, string? LinkCode, string? ShareCode)
{
    public bool IsPrivateServer => LinkCode is not null || ShareCode is not null;
}
