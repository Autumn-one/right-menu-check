using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using RightMenuCheck.Core.Metadata;

namespace RightMenuCheck.Windows.Metadata;

internal static partial class AuthenticodeVerifier
{
    private const uint WinTrustUiNone = 2;
    private const uint WinTrustRevokeNone = 0;
    private const uint WinTrustChoiceFile = 1;
    private const uint WinTrustStateActionVerify = 1;
    private const uint WinTrustStateActionClose = 2;
    private const uint WinTrustRevocationCheckNone = 0x00000010;
    private const uint WinTrustCacheOnlyUrlRetrieval = 0x00001000;

    private const int TrustENoSignature = unchecked((int)0x800B0100);
    private const int TrustEProviderUnknown = unchecked((int)0x800B0001);
    private const int TrustESubjectFormUnknown = unchecked((int)0x800B0003);

    private static readonly Guid GenericVerifyV2Action =
        new("00AAC56B-CD44-11D0-8CC2-00C04FC295EE");

    public static AuthenticodeSignatureMetadata Verify(string filePath)
    {
        var filePathPointer = Marshal.StringToCoTaskMemUni(filePath);
        var fileInfoPointer = IntPtr.Zero;
        var trustData = new WinTrustData();

        try
        {
            var fileInfo = new WinTrustFileInfo
            {
                StructSize = checked((uint)Marshal.SizeOf<WinTrustFileInfo>()),
                FilePath = filePathPointer,
            };
            fileInfoPointer = Marshal.AllocCoTaskMem(Marshal.SizeOf<WinTrustFileInfo>());
            Marshal.StructureToPtr(fileInfo, fileInfoPointer, fDeleteOld: false);

            trustData = new WinTrustData
            {
                StructSize = checked((uint)Marshal.SizeOf<WinTrustData>()),
                UiChoice = WinTrustUiNone,
                RevocationChecks = WinTrustRevokeNone,
                UnionChoice = WinTrustChoiceFile,
                FileInfo = fileInfoPointer,
                StateAction = WinTrustStateActionVerify,
                ProviderFlags = WinTrustRevocationCheckNone | WinTrustCacheOnlyUrlRetrieval,
            };

            var action = GenericVerifyV2Action;
            var trustResult = WinVerifyTrust(new IntPtr(-1), ref action, ref trustData);
            var certificate = TryReadEmbeddedCertificate(filePath);
            var status = trustResult switch
            {
                0 => SignatureVerificationStatus.Valid,
                TrustENoSignature or TrustEProviderUnknown or TrustESubjectFormUnknown =>
                    SignatureVerificationStatus.NoSignature,
                _ => SignatureVerificationStatus.Invalid,
            };

            return new AuthenticodeSignatureMetadata(
                status,
                certificate?.PublisherName,
                certificate?.Subject,
                certificate?.Issuer,
                certificate?.Thumbprint,
                certificate?.ValidFrom,
                certificate?.ValidTo,
                trustResult == 0 ? null : $"0x{unchecked((uint)trustResult):X8}");
        }
        finally
        {
            if (trustData.StateData != IntPtr.Zero)
            {
                trustData.StateAction = WinTrustStateActionClose;
                var action = GenericVerifyV2Action;
                _ = WinVerifyTrust(new IntPtr(-1), ref action, ref trustData);
            }

            if (fileInfoPointer != IntPtr.Zero)
            {
                Marshal.FreeCoTaskMem(fileInfoPointer);
            }

            Marshal.FreeCoTaskMem(filePathPointer);
        }
    }

    private static CertificateDetails? TryReadEmbeddedCertificate(string filePath)
    {
        try
        {
#pragma warning disable SYSLIB0057
            using var certificate = X509Certificate.CreateFromSignedFile(filePath);
#pragma warning restore SYSLIB0057
            using var certificate2 = X509CertificateLoader.LoadCertificate(
                certificate.GetRawCertData());
            return new CertificateDetails(
                certificate2.GetNameInfo(X509NameType.SimpleName, forIssuer: false),
                certificate2.Subject,
                certificate2.Issuer,
                certificate2.Thumbprint,
                certificate2.NotBefore,
                certificate2.NotAfter);
        }
        catch (CryptographicException)
        {
            return null;
        }
    }

    [LibraryImport("wintrust.dll", SetLastError = true)]
    private static partial int WinVerifyTrust(
        IntPtr windowHandle,
        ref Guid actionId,
        ref WinTrustData trustData);

    [StructLayout(LayoutKind.Sequential)]
    private struct WinTrustFileInfo
    {
        public uint StructSize;
        public IntPtr FilePath;
        public IntPtr FileHandle;
        public IntPtr KnownSubject;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WinTrustData
    {
        public uint StructSize;
        public IntPtr PolicyCallbackData;
        public IntPtr SipClientData;
        public uint UiChoice;
        public uint RevocationChecks;
        public uint UnionChoice;
        public IntPtr FileInfo;
        public uint StateAction;
        public IntPtr StateData;
        public IntPtr UrlReference;
        public uint ProviderFlags;
        public uint UiContext;
        public IntPtr SignatureSettings;
    }

    private sealed record CertificateDetails(
        string PublisherName,
        string Subject,
        string Issuer,
        string Thumbprint,
        DateTimeOffset ValidFrom,
        DateTimeOffset ValidTo);
}
