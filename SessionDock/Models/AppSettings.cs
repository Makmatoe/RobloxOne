namespace SessionDock.Models;

public sealed class AppSettings
{
    public List<AccountProfile> Accounts { get; set; } = [];
    public string? ActiveAccountKey { get; set; }
    public List<RecentExperience> RecentExperiences { get; set; } = [];
    public bool UiSoundsEnabled { get; set; } = true;
    public string StartupSound { get; set; } = "soft";
    public string? CustomStartupSoundFileName { get; set; }

    // Kept for automatic migration from the legacy Roblox One 1.x format.
    public long? LockedUserId { get; set; }
    public string? LockedUsername { get; set; }
    public long? PlaceId { get; set; }
    public string? Destination { get; set; }
}

public sealed class AccountProfile
{
    public string Key { get; set; } = Guid.NewGuid().ToString("N");
    public long UserId { get; set; }
    public string Username { get; set; } = string.Empty;
    public string SessionFolder { get; set; } = string.Empty;
    public string? Label { get; set; }
    public string? ColorHex { get; set; }
    public string? Destination { get; set; }
}
