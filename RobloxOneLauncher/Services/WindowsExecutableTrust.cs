using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;

namespace RobloxOneLauncher.Services;

internal static class WindowsExecutableTrust
{
    private static readonly Guid WinTrustActionGenericVerifyV2 =
        new("00AAC56B-CD44-11D0-8CC2-00C04FC295EE");

    public static bool TryGetTrustedSigner(
        string path,
        out TrustedWindowsSigner signer)
    {
        signer = TrustedWindowsSigner.Empty;
        try
        {
            if (!File.Exists(path) || !VerifyEmbeddedSignature(path))
                return false;

#pragma warning disable SYSLIB0057 // Authenticode signer extraction requires this Windows API.
            using var signingCertificate = X509Certificate.CreateFromSignedFile(path);
#pragma warning restore SYSLIB0057
            using var certificate = X509CertificateLoader.LoadCertificate(
                signingCertificate.GetRawCertData());
            var subject = certificate.Subject;
            var simpleName = certificate.GetNameInfo(
                X509NameType.SimpleName,
                forIssuer: false);
            if (string.IsNullOrWhiteSpace(subject) || string.IsNullOrWhiteSpace(simpleName))
                return false;

            signer = new TrustedWindowsSigner(subject, simpleName);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool VerifyEmbeddedSignature(string path)
    {
        var fileInfo = new WinTrustFileInfo(path);
        var fileInfoPointer = Marshal.AllocHGlobal(
            Marshal.SizeOf<WinTrustFileInfo>());
        var trustDataPointer = IntPtr.Zero;
        try
        {
            Marshal.StructureToPtr(fileInfo, fileInfoPointer, fDeleteOld: false);
            var trustData = new WinTrustData(fileInfoPointer);
            trustDataPointer = Marshal.AllocHGlobal(
                Marshal.SizeOf<WinTrustData>());
            Marshal.StructureToPtr(trustData, trustDataPointer, fDeleteOld: false);
            return WinVerifyTrust(
                IntPtr.Zero,
                WinTrustActionGenericVerifyV2,
                trustDataPointer) == 0;
        }
        finally
        {
            if (trustDataPointer != IntPtr.Zero)
                Marshal.FreeHGlobal(trustDataPointer);
            Marshal.FreeHGlobal(fileInfoPointer);
        }
    }

    [DllImport("wintrust.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern uint WinVerifyTrust(
        IntPtr windowHandle,
        [MarshalAs(UnmanagedType.LPStruct)] Guid actionId,
        IntPtr trustData);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private readonly struct WinTrustFileInfo
    {
        private readonly uint StructSize;
        [MarshalAs(UnmanagedType.LPWStr)]
        private readonly string FilePath;
        private readonly IntPtr FileHandle;
        private readonly IntPtr KnownSubject;

        public WinTrustFileInfo(string filePath)
        {
            StructSize = (uint)Marshal.SizeOf<WinTrustFileInfo>();
            FilePath = filePath;
            FileHandle = IntPtr.Zero;
            KnownSubject = IntPtr.Zero;
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private readonly struct WinTrustData
    {
        private readonly uint StructSize;
        private readonly IntPtr PolicyCallbackData;
        private readonly IntPtr SipClientData;
        private readonly uint UiChoice;
        private readonly uint RevocationChecks;
        private readonly uint UnionChoice;
        private readonly IntPtr FileInfo;
        private readonly uint StateAction;
        private readonly IntPtr StateData;
        private readonly IntPtr UrlReference;
        private readonly uint ProviderFlags;
        private readonly uint UiContext;
        private readonly IntPtr SignatureSettings;

        public WinTrustData(IntPtr fileInfo)
        {
            StructSize = (uint)Marshal.SizeOf<WinTrustData>();
            PolicyCallbackData = IntPtr.Zero;
            SipClientData = IntPtr.Zero;
            UiChoice = 2; // WTD_UI_NONE
            RevocationChecks = 0;
            UnionChoice = 1; // WTD_CHOICE_FILE
            FileInfo = fileInfo;
            StateAction = 0;
            StateData = IntPtr.Zero;
            UrlReference = IntPtr.Zero;
            ProviderFlags = 0x00001100;
            UiContext = 0;
            SignatureSettings = IntPtr.Zero;
        }
    }
}

internal sealed record TrustedWindowsSigner(string Subject, string SimpleName)
{
    public static TrustedWindowsSigner Empty { get; } = new(string.Empty, string.Empty);
}
