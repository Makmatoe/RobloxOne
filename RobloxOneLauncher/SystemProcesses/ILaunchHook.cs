namespace RobloxOneLauncher.SystemProcesses;

public interface ILaunchHook : IDisposable
{
    Task NotifyLaunchAsync(
        LaunchHookEvent launchEvent,
        CancellationToken cancellationToken = default);
}
