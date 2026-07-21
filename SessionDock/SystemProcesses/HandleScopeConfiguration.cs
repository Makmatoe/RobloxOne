namespace SessionDock.SystemProcesses;

public sealed class HandleScopeConfiguration
{
    public bool Enabled { get; set; }
    public string ProcessName { get; set; } =
        HandleScopeConfigurationLoader.RequiredProcessName;
    public string HandleName { get; set; } =
        HandleScopeConfigurationLoader.RequiredHandleName;
    public string? HandleType { get; set; } =
        HandleScopeConfigurationLoader.RequiredHandleType;
    public string? Access { get; set; } =
        HandleScopeConfigurationLoader.RequiredAccess;
    public string Match { get; set; } = "exact";
    public bool CloseAll { get; set; }
    public bool AllProcesses { get; set; } = true;
    public int RetryTimeoutSeconds { get; set; } = 10;
    public int RetryIntervalMilliseconds { get; set; } = 500;
}
