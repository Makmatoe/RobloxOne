using System.Diagnostics;

namespace RobloxOneLauncher.SystemProcesses;

public sealed class CompositeLaunchHook : ILaunchHook
{
    private readonly IReadOnlyList<ILaunchHook> _hooks;

    public CompositeLaunchHook(params ILaunchHook[] hooks)
    {
        ArgumentNullException.ThrowIfNull(hooks);
        if (hooks.Any(hook => hook is null))
            throw new ArgumentException("Launch hooks cannot contain null.", nameof(hooks));
        _hooks = hooks.ToArray();
    }

    public Task NotifyLaunchAsync(
        LaunchHookEvent launchEvent,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(launchEvent);
        return Task.WhenAll(_hooks.Select(
            hook => NotifySafelyAsync(hook, launchEvent, cancellationToken)));
    }

    public void Dispose()
    {
        foreach (var hook in _hooks)
        {
            try
            {
                hook.Dispose();
            }
            catch (Exception ex)
            {
                Trace.WriteLine(
                    $"Optional launch hook disposal failed: {ex.GetType().Name}.");
            }
        }
    }

    private static async Task NotifySafelyAsync(
        ILaunchHook hook,
        LaunchHookEvent launchEvent,
        CancellationToken cancellationToken)
    {
        try
        {
            await hook.NotifyLaunchAsync(launchEvent, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Application shutdown cancellation is expected.
        }
        catch (Exception ex)
        {
            Trace.WriteLine(
                $"Optional launch hook failed: {ex.GetType().Name}.");
        }
    }
}
