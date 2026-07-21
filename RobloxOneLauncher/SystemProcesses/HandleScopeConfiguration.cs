namespace RobloxOneLauncher.SystemProcesses;

public sealed class HandleScopeConfiguration
{
    public bool Enabled { get; set; }
    public string ProcessName { get; set; } = "RobloxPlayerBeta";
    public string HandleName { get; set; } = string.Empty;
    public string? HandleType { get; set; }
    public string? Access { get; set; }
    public string Match { get; set; } = "exact";
    public bool CloseAll { get; set; }
    public bool AllProcesses { get; set; } = true;
    public int RetryTimeoutSeconds { get; set; } = 10;
    public int RetryIntervalMilliseconds { get; set; } = 500;
}
