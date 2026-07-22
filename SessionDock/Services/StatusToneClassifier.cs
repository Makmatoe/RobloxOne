namespace SessionDock.Services;

public enum StatusTone
{
    Neutral,
    Success,
    Warning,
    Error
}

public static class StatusToneClassifier
{
    private static readonly string[] ErrorTerms =
        ["ERROR", "BLOCKED", "INVALID", "REQUIRED", "REJECTED", "DENIED", "TIMEOUT"];
    private static readonly string[] WarningTerms =
        ["PARTIAL", "FULL", "DUPLICATE", "UNAVAILABLE", "CANCEL", "BUSY"];
    private static readonly string[] SuccessTerms =
        ["VERIFIED", "READY", "STARTED", "CLOSED", "COMPLETE", "SAVED", "UP TO DATE"];

    public static StatusTone Classify(string badge)
    {
        ArgumentNullException.ThrowIfNull(badge);
        if (ContainsAny(badge, ErrorTerms))
            return StatusTone.Error;
        if (ContainsAny(badge, WarningTerms))
            return StatusTone.Warning;
        if (ContainsAny(badge, SuccessTerms))
            return StatusTone.Success;
        return StatusTone.Neutral;
    }

    private static bool ContainsAny(string value, IEnumerable<string> terms) =>
        terms.Any(term => value.Contains(term, StringComparison.OrdinalIgnoreCase));
}
