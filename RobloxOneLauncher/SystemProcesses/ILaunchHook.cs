namespace RobloxOneLauncher.SystemProcesses;

public interface ILaunchHook : IDisposable
{
    bool IsConfigured { get; }

    Task NotifyLaunchAsync(
        LaunchHookEvent launchEvent,
        CancellationToken cancellationToken = default);
}
