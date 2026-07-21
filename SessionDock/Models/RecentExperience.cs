namespace SessionDock.Models;

public sealed class RecentExperience
{
    public string Destination { get; set; } = string.Empty;
    public long PlaceId { get; set; }
    public string? Name { get; set; }
    public string? CustomName { get; set; }
    public bool IsPrivateServer { get; set; }
    public bool IsPinned { get; set; }
    public string? ServerJobId { get; set; }
    public long AccountUserId { get; set; }
    public string? AccountUsername { get; set; }
    public DateTimeOffset LastLaunchedAt { get; set; }
}
