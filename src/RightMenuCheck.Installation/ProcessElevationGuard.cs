using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace RightMenuCheck.Installation;

public static partial class ProcessElevationGuard
{
    private const uint TokenQuery = 0x0008;

    public static void ThrowIfElevated(string operation)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operation);
        if (!OpenProcessToken(GetCurrentProcess(), TokenQuery, out var token))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        using (token)
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

            if (elevation.TokenIsElevated != 0)
            {
                throw new InvalidOperationException(
                    $"{operation} must run as the current standard user, not as administrator.");
            }
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TokenElevation
    {
        public int TokenIsElevated;
    }

    private enum TokenInformationClass
    {
        TokenElevation = 20,
    }

    [LibraryImport("kernel32.dll")]
    private static partial nint GetCurrentProcess();

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool OpenProcessToken(
        nint processHandle,
        uint desiredAccess,
        out SafeFileHandle tokenHandle);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetTokenInformation(
        SafeFileHandle tokenHandle,
        TokenInformationClass tokenInformationClass,
        ref TokenElevation tokenInformation,
        int tokenInformationLength,
        out int returnLength);
}
