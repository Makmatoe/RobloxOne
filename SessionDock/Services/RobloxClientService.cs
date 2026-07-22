using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using SessionDock.SystemProcesses;

namespace SessionDock.Services;

public sealed class RobloxClientService
{
    private static readonly TimeSpan GracefulCloseTimeout = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan ForcedCloseTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan TotalCloseTimeout = TimeSpan.FromSeconds(12);
    private static readonly Regex ExecutablePattern = new(
        "^\\s*\"(?<path>[^\"]+\\.exe)\"|^\\s*(?<path>[^\\s]+\\.exe)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public string? FindPlayerPath()
    {
        try
        {
            var registeredPath = FindRegisteredPlayerPath();
            if (registeredPath is not null)
                return registeredPath;

            var versionsDirectory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Roblox", "Versions");
            if (!Directory.Exists(versionsDirectory) ||
                IsReparsePoint(versionsDirectory))
            {
                return null;
            }

            return Directory.EnumerateDirectories(
                    versionsDirectory,
                    "version-*",
                    SearchOption.TopDirectoryOnly)
                .Where(directory => !IsReparsePoint(directory))
                .Take(256)
                .Select(directory => Path.Combine(directory, "RobloxPlayerBeta.exe"))
                .Where(File.Exists)
                .Select(path => new FileInfo(path))
                .OrderByDescending(file => file.LastWriteTimeUtc)
                .Select(file => file.FullName)
                .FirstOrDefault(RobloxExecutableTrust.IsTrustedPlayerPath);
        }
        catch (Exception ex) when (
            ex is IOException or UnauthorizedAccessException or
                System.Security.SecurityException or ArgumentException)
        {
            return null;
        }
    }

    public Task<LaunchResult> LaunchAsync(
        string launchUri,
        CancellationToken cancellationToken = default)
    {
        return Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var playerPath = FindPlayerPath();
                if (playerPath is null)
                {
                    return LaunchResult.Failed(
                        "Roblox Player was not found. Install Roblox Player, then restart SessionDock.");
                }

                var startInfo = CreatePlayerStartInfo(playerPath, launchUri);
                cancellationToken.ThrowIfCancellationRequested();
                using var process = Process.Start(startInfo);
                return process is null
                    ? LaunchResult.Failed("Roblox Player did not return a process.")
                    : LaunchResult.Succeeded(process.Id);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                return LaunchResult.Failed($"Roblox Player could not start: {ex.Message}");
            }
        }, cancellationToken);
    }

    internal static ProcessStartInfo CreatePlayerStartInfo(
        string playerPath,
        string launchUri,
        IReadOnlyDictionary<string, string?>? inheritedEnvironment = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(playerPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(launchUri);
        var startInfo = new ProcessStartInfo
        {
            FileName = playerPath,
            UseShellExecute = false
        };
        if (inheritedEnvironment is not null)
        {
            startInfo.Environment.Clear();
            foreach (var variable in inheritedEnvironment)
            {
                if (variable.Value is not null)
                    startInfo.Environment[variable.Key] = variable.Value;
            }
        }

        LocalApiLaunchHook.RemoveConfigurationFromChildEnvironment(startInfo);
        startInfo.ArgumentList.Add(launchUri);
        return startInfo;
    }

    public Task<ClosePlayersResult> CloseAllPlayersAsync(
        CancellationToken cancellationToken = default) =>
        Task.Run(() => CloseAllPlayers(cancellationToken), cancellationToken);

    private static ClosePlayersResult CloseAllPlayers(
        CancellationToken cancellationToken)
    {
        var seenProcessIds = new HashSet<int>();
        var backgroundProcessIds = new HashSet<int>();
        var closedProcessIds = new HashSet<int>();
        var deadline = DateTimeOffset.UtcNow + TotalCloseTimeout;

        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var scan = FindRunningPlayers();
            foreach (var processId in scan.AllProcessIds)
                seenProcessIds.Add(processId);
            foreach (var processId in scan.BackgroundProcessIds)
                backgroundProcessIds.Add(processId);

            if (scan.VerifiedProcesses.Count == 0)
            {
                return new ClosePlayersResult(
                    seenProcessIds.Count,
                    closedProcessIds.Count,
                    0,
                    scan.UnverifiedCount,
                    backgroundProcessIds.Count);
            }

            try
            {
                foreach (var verifiedProcess in scan.VerifiedProcesses)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var process = verifiedProcess.Process;
                    if (!IsStillVerified(verifiedProcess))
                        continue;
                    try
                    {
                        process.CloseMainWindow();
                    }
                    catch (InvalidOperationException)
                    {
                        closedProcessIds.Add(process.Id);
                    }
                }

                WaitForPlayersToExit(
                    scan.VerifiedProcesses,
                    GracefulCloseTimeout,
                    closedProcessIds,
                    cancellationToken);

                foreach (var verifiedProcess in scan.VerifiedProcesses)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var process = verifiedProcess.Process;
                    if (HasExited(process))
                        continue;
                    if (!IsStillVerified(verifiedProcess))
                        continue;
                    try
                    {
                        process.Kill(entireProcessTree: false);
                    }
                    catch (InvalidOperationException)
                    {
                        closedProcessIds.Add(process.Id);
                    }
                    catch (System.ComponentModel.Win32Exception)
                    {
                        // The final rescan reports a verified process that could not be closed.
                    }
                }

                WaitForPlayersToExit(
                    scan.VerifiedProcesses,
                    ForcedCloseTimeout,
                    closedProcessIds,
                    cancellationToken);
            }
            finally
            {
                foreach (var process in scan.VerifiedProcesses)
                    process.Process.Dispose();
            }
        }

        var remaining = FindRunningPlayers();
        try
        {
            foreach (var processId in remaining.AllProcessIds)
                seenProcessIds.Add(processId);
            foreach (var processId in remaining.BackgroundProcessIds)
                backgroundProcessIds.Add(processId);
            return new ClosePlayersResult(
                seenProcessIds.Count,
                closedProcessIds.Count,
                remaining.VerifiedProcesses.Count,
                remaining.UnverifiedCount,
                backgroundProcessIds.Count);
        }
        finally
        {
            foreach (var process in remaining.VerifiedProcesses)
                process.Process.Dispose();
        }
    }

    private static PlayerProcessScan FindRunningPlayers()
    {
        var verified = new List<VerifiedPlayerProcess>();
        var allProcessIds = new List<int>();
        var backgroundProcessIds = new List<int>();
        var unverified = 0;
        foreach (var process in Process.GetProcessesByName("RobloxPlayerBeta"))
        {
            try
            {
                if (process.HasExited)
                {
                    process.Dispose();
                    continue;
                }
                allProcessIds.Add(process.Id);
                var executablePath = process.MainModule?.FileName;
                if (executablePath is not null &&
                    RobloxExecutableTrust.IsTrustedPlayerPath(executablePath) &&
                    WindowsProcessSecurity.IsOwnedStandardUserProcessInCurrentSession(
                        process))
                {
                    var startTimeUtc = process.StartTime.ToUniversalTime();
                    var isBackgroundProcess = process.MainWindowHandle == IntPtr.Zero;
                    verified.Add(new VerifiedPlayerProcess(process, startTimeUtc));
                    if (isBackgroundProcess)
                        backgroundProcessIds.Add(process.Id);
                    continue;
                }

                unverified++;
            }
            catch (Exception ex) when (
                ex is InvalidOperationException or
                System.ComponentModel.Win32Exception or
                NotSupportedException)
            {
                if (!HasExited(process))
                    unverified++;
            }

            process.Dispose();
        }

        return new PlayerProcessScan(
            verified,
            allProcessIds,
            backgroundProcessIds,
            unverified);
    }

    private static void WaitForPlayersToExit(
        IEnumerable<VerifiedPlayerProcess> processes,
        TimeSpan timeout,
        ISet<int> closedProcessIds,
        CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var allExited = true;
            foreach (var verifiedProcess in processes)
            {
                var process = verifiedProcess.Process;
                if (HasExited(process))
                    closedProcessIds.Add(process.Id);
                else
                    allExited = false;
            }

            if (allExited)
                return;
            cancellationToken.WaitHandle.WaitOne(TimeSpan.FromMilliseconds(100));
        }

        foreach (var verifiedProcess in processes)
        {
            var process = verifiedProcess.Process;
            if (HasExited(process))
                closedProcessIds.Add(process.Id);
        }
    }

    private static bool HasExited(Process process)
    {
        try
        {
            return process.HasExited;
        }
        catch (InvalidOperationException)
        {
            return true;
        }
    }

    private static bool IsStillVerified(VerifiedPlayerProcess verifiedProcess)
    {
        try
        {
            var process = verifiedProcess.Process;
            return !process.HasExited &&
                   process.StartTime.ToUniversalTime() ==
                       verifiedProcess.StartTimeUtc &&
                   process.MainModule?.FileName is { } path &&
                   RobloxExecutableTrust.IsTrustedPlayerPath(path) &&
                   WindowsProcessSecurity.IsOwnedStandardUserProcessInCurrentSession(
                       process);
        }
        catch (Exception ex) when (
            ex is InvalidOperationException or
                System.ComponentModel.Win32Exception or NotSupportedException)
        {
            return false;
        }
    }

    private static string? FindRegisteredPlayerPath()
    {
        foreach (var scheme in new[] { "roblox-player", "roblox" })
        {
            using var commandKey = Registry.ClassesRoot.OpenSubKey(
                $@"{scheme}\shell\open\command");
            var command = commandKey?.GetValue(null) as string;
            if (string.IsNullOrWhiteSpace(command))
                continue;

            var match = ExecutablePattern.Match(command);
            var path = match.Success ? match.Groups["path"].Value : string.Empty;
            if (File.Exists(path) && RobloxExecutableTrust.IsTrustedPlayerPath(path))
                return path;
        }

        return null;
    }

    private static bool IsReparsePoint(string path) =>
        (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;

    public sealed record LaunchResult(bool Success, string? Error, int? ProcessId)
    {
        public static LaunchResult Succeeded(int processId) =>
            new(true, null, processId);

        public static LaunchResult Failed(string error) =>
            new(false, error, null);
    }

    public sealed record ClosePlayersResult(
        int Found,
        int Closed,
        int Remaining,
        int Unverified,
        int BackgroundFound)
    {
        public bool Success => Remaining == 0 && Unverified == 0;
    }

    private sealed record PlayerProcessScan(
        IReadOnlyList<VerifiedPlayerProcess> VerifiedProcesses,
        IReadOnlyList<int> AllProcessIds,
        IReadOnlyList<int> BackgroundProcessIds,
        int UnverifiedCount);

    private sealed record VerifiedPlayerProcess(
        Process Process,
        DateTime StartTimeUtc);

}
