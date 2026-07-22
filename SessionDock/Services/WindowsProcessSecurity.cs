using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

namespace SessionDock.Services;

internal static class WindowsProcessSecurity
{
    internal static bool IsOwnedStandardUserProcessInCurrentSession(
        Process process)
    {
        ArgumentNullException.ThrowIfNull(process);

        try
        {
            using var currentProcess = Process.GetCurrentProcess();
            if (process.SessionId != currentProcess.SessionId ||
                !OpenProcessToken(
                    process.SafeHandle,
                    TokenAccessLevels.Query,
                    out var token))
            {
                return false;
            }

            using (token)
            using (var currentIdentity = WindowsIdentity.GetCurrent(
                       TokenAccessLevels.Query))
            using (var processIdentity = new WindowsIdentity(
                       token.DangerousGetHandle()))
            {
                return currentIdentity.User is not null &&
                       processIdentity.User is not null &&
                       currentIdentity.User.Equals(processIdentity.User) &&
                       !RuntimeSecurityPolicy.IsTokenElevated(token);
            }
        }
        catch (Exception exception) when (
            exception is InvalidOperationException or
                UnauthorizedAccessException or
                System.ComponentModel.Win32Exception or
                NotSupportedException)
        {
            return false;
        }
    }

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenProcessToken(
        SafeProcessHandle processHandle,
        TokenAccessLevels desiredAccess,
        out SafeAccessTokenHandle tokenHandle);
}
