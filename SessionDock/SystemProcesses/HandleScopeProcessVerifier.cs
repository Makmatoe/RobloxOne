using System.ComponentModel;
using System.Diagnostics;
using System.IO;

namespace SessionDock.SystemProcesses;

internal interface IHandleScopeProcessVerifier
{
    bool IsExpected(HandleScopeConnection connection);
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
            if (!HandleScopePathSecurity.IsSafeExistingPath(
                    _localAppDataRoot,
                    _expectedExecutablePath,
                    targetMustExist: true,
                    _isReparsePoint))
            {
                return false;
            }

            using var process = Process.GetProcessById(connection.ApiProcessId);
            using var current = Process.GetCurrentProcess();
            var actualPath = process.MainModule?.FileName;
            if (actualPath is null)
                return false;

            var snapshot = new HandleScopeProcessSnapshot(
                process.Id,
                process.HasExited,
                process.ProcessName,
                process.SessionId,
                Path.GetFullPath(actualPath),
                new DateTimeOffset(process.StartTime.ToUniversalTime()));
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

    internal static bool MatchesExpectedProcess(
        HandleScopeConnection connection,
        string expectedExecutablePath,
        int currentSessionId,
        HandleScopeProcessSnapshot process,
        DateTimeOffset utcNow)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedExecutablePath);

        if (process.HasExited ||
            process.ProcessId != connection.ApiProcessId ||
            process.SessionId != currentSessionId ||
            !process.ProcessName.Equals(
                ExpectedProcessName,
                StringComparison.OrdinalIgnoreCase) ||
            !Path.GetFullPath(process.ExecutablePath).Equals(
                Path.GetFullPath(expectedExecutablePath),
                StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var processStartedAtUtc = process.StartedAtUtc.ToUniversalTime();
        var discoveryStartedAtUtc = connection.StartedAtUtc.ToUniversalTime();
        return discoveryStartedAtUtc >= processStartedAtUtc - AllowedClockSkew &&
            discoveryStartedAtUtc <= utcNow.ToUniversalTime() + AllowedClockSkew;
    }
}

internal readonly record struct HandleScopeProcessSnapshot(
    int ProcessId,
    bool HasExited,
    string ProcessName,
    int SessionId,
    string ExecutablePath,
    DateTimeOffset StartedAtUtc);
