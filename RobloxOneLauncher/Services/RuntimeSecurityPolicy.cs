using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

namespace RobloxOneLauncher.Services;

public static class RuntimeSecurityPolicy
{
    private static readonly HashSet<string> ServiceSids = new(
        StringComparer.Ordinal)
    {
        "S-1-5-18", // LocalSystem
        "S-1-5-19", // LocalService
        "S-1-5-20"  // NetworkService
    };

    public static bool IsCurrentProcessSupported(out string reason)
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent(
                TokenAccessLevels.Query);
            using var process = Process.GetCurrentProcess();
            var context = new RuntimeSecurityContext(
                IsTokenElevated(identity.AccessToken),
                process.SessionId,
                identity.User?.Value);
            return IsSupported(context, out reason);
        }
        catch (Exception ex) when (
            ex is Win32Exception or InvalidOperationException or
                UnauthorizedAccessException or NotSupportedException or
                SecurityException)
        {
            reason =
                "The current Windows security context could not be verified.";
            return false;
        }
    }

    public static bool IsSupported(
        RuntimeSecurityContext context,
        out string reason)
    {
        if (context.IsElevated)
        {
            reason =
                "Roblox One must be started as a standard user, not as administrator.";
            return false;
        }

        if (context.SessionId <= 0)
        {
            reason =
                "Roblox One requires an interactive Windows user session.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(context.UserSid) ||
            ServiceSids.Contains(context.UserSid))
        {
            reason =
                "Roblox One cannot run under a Windows service identity.";
            return false;
        }

        reason = string.Empty;
        return true;
    }

    internal static bool IsTokenElevated(SafeAccessTokenHandle token)
    {
        var elevation = new TokenElevation();
        var size = Marshal.SizeOf<TokenElevation>();
        if (!GetTokenInformation(
                token,
                TokenInformationClass.TokenElevation,
                ref elevation,
                size,
                out _))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        return elevation.TokenIsElevated != 0;
    }

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetTokenInformation(
        SafeAccessTokenHandle tokenHandle,
        TokenInformationClass tokenInformationClass,
        ref TokenElevation tokenInformation,
        int tokenInformationLength,
        out int returnLength);

    private enum TokenInformationClass
    {
        TokenElevation = 20
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TokenElevation
    {
        public int TokenIsElevated;
    }
}

public readonly record struct RuntimeSecurityContext(
    bool IsElevated,
    int SessionId,
    string? UserSid);
