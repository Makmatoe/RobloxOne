namespace SessionDock.SystemProcesses;

public enum HandleScopeIntegrationState
{
    NotInstalled,
    InstalledStopped,
    StartPending,
    RunningUntested,
    RunningDisabled,
    Ready,
    UpdateRequired,
    ConfigurationError
}

public sealed record HandleScopeIntegrationResult(
    HandleScopeIntegrationState State,
    bool CanRepairConfiguration = false);
