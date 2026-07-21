using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using SessionDock.Services;

namespace SessionDock.SystemProcesses;

internal interface IHandleScopeProcessVerifier
{
    bool IsExpected(HandleScopeConnection connection);

    bool IsExpectedStartedProcess(int processId);

    int? FindExpectedRunningProcessId();
}

internal sealed class HandleScopeProcessVerifier : IHandleScopeProcessVerifier
{
    internal const string ExpectedProcessName = "HandleScope.Api";
    internal static readonly TimeSpan AllowedClockSkew = TimeSpan.FromSeconds(5);

    private readonly string _localAppDataRoot;
    private readonly string _expectedExecutablePath;
    private readonly Func<string, bool>? _isReparsePoint;

    internal HandleScopeProcessVerifier(
        string localAppDataRoot,
        string expectedExecutablePath,
        Func<string, bool>? isReparsePoint = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(localAppDataRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedExecutablePath);
        _localAppDataRoot = Path.GetFullPath(localAppDataRoot);
        _expectedExecutablePath = Path.GetFullPath(expectedExecutablePath);
        _isReparsePoint = isReparsePoint;
    }

    internal static HandleScopeProcessVerifier CreateDefault()
    {
        var localAppDataRoot = Path.GetFullPath(Environment.GetFolderPath(
            Environment.SpecialFolder.LocalApplicationData));
        return new HandleScopeProcessVerifier(
            localAppDataRoot,
            GetExpectedExecutablePath(localAppDataRoot));
    }

    internal static string GetExpectedExecutablePath(string localAppDataRoot) =>
        Path.GetFullPath(Path.Combine(
            localAppDataRoot,
            "Programs",
            "HandleScope",
            "Api",
            "HandleScope.Api.exe"));

    public bool IsExpected(HandleScopeConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);

        try
        {
            if (!TryGetExpectedProcessSnapshot(
                    connection.ApiProcessId,
                    out var snapshot))
                return false;

            using var current = Process.GetCurrentProcess();
            return MatchesExpectedProcess(
                connection,
                _expectedExecutablePath,
                current.SessionId,
                snapshot,
                DateTimeOffset.UtcNow);
        }
        catch (Exception exception) when (
            exception is ArgumentException or InvalidOperationException or
                Win32Exception or NotSupportedException or IOException or
                UnauthorizedAccessException)
        {
            return false;
        }
    }

    public bool IsExpectedStartedProcess(int processId)
    {
        if (processId <= 0)
            return false;

        try
        {
            if (!TryGetExpectedProcessSnapshot(processId, out var snapshot))
                return false;

            using var current = Process.GetCurrentProcess();
            return MatchesExpectedIdentity(
                processId,
                _expectedExecutablePath,
                current.SessionId,
                snapshot);
        }
        catch (Exception exception) when (
            exception is ArgumentException or InvalidOperationException or
                Win32Exception or NotSupportedException or IOException or
                UnauthorizedAccessException)
        {
            return false;
        }
    }

    public int? FindExpectedRunningProcessId()
    {
        Process[] processes;
        try
        {
            processes = Process.GetProcessesByName(ExpectedProcessName);
        }
        catch (Exception exception) when (
            exception is InvalidOperationException or Win32Exception or
                NotSupportedException)
        {
            return null;
        }

        try
        {
            foreach (var process in processes)
            {
                try
                {
                    var processId = process.Id;
                    if (IsExpectedStartedProcess(processId))
                        return processId;
                }
                catch (InvalidOperationException)
                {
                    // The process exited while the snapshot was enumerated.
                }
            }
        }
        finally
        {
            foreach (var process in processes)
                process.Dispose();
        }

        return null;
    }

    private bool TryGetExpectedProcessSnapshot(
        int processId,
        out HandleScopeProcessSnapshot snapshot)
    {
        snapshot = default;
        if (!HandleScopePathSecurity.IsSafeExistingPath(
                _localAppDataRoot,
                _expectedExecutablePath,
                targetMustExist: true,
                _isReparsePoint))
        {
            return false;
        }

        using var process = Process.GetProcessById(processId);
        var actualPath = process.MainModule?.FileName;
        if (actualPath is null ||
            !WindowsProcessSecurity.IsOwnedStandardUserProcessInCurrentSession(
                process))
        {
            return false;
        }

        snapshot = new HandleScopeProcessSnapshot(
            process.Id,
            process.HasExited,
            process.ProcessName,
            process.SessionId,
            Path.GetFullPath(actualPath),
            new DateTimeOffset(process.StartTime.ToUniversalTime()));
        return true;
    }

    internal static bool MatchesExpectedProcess(
        HandleScopeConnection connection,
        string expectedExecutablePath,
        int currentSessionId,
        HandleScopeProcessSnapshot process,
        DateTimeOffset utcNow)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedExecutablePath);

        if (!MatchesExpectedIdentity(
                connection.ApiProcessId,
                expectedExecutablePath,
                currentSessionId,
                process))
        {
            return false;
        }

        var processStartedAtUtc = process.StartedAtUtc.ToUniversalTime();
        var discoveryStartedAtUtc = connection.StartedAtUtc.ToUniversalTime();
        return discoveryStartedAtUtc >= processStartedAtUtc - AllowedClockSkew &&
            discoveryStartedAtUtc <= utcNow.ToUniversalTime() + AllowedClockSkew;
    }

    private static bool MatchesExpectedIdentity(
        int expectedProcessId,
        string expectedExecutablePath,
        int currentSessionId,
        HandleScopeProcessSnapshot process) =>
        !process.HasExited &&
        process.ProcessId == expectedProcessId &&
        process.SessionId == currentSessionId &&
        process.ProcessName.Equals(
            ExpectedProcessName,
            StringComparison.OrdinalIgnoreCase) &&
        Path.GetFullPath(process.ExecutablePath).Equals(
            Path.GetFullPath(expectedExecutablePath),
            StringComparison.OrdinalIgnoreCase);
}

internal readonly record struct HandleScopeProcessSnapshot(
    int ProcessId,
    bool HasExited,
    string ProcessName,
    int SessionId,
    string ExecutablePath,
    DateTimeOffset StartedAtUtc);
