using System.ComponentModel;
using System.Runtime.InteropServices;

namespace RightMenuCheck.Windows.Metadata;

internal static partial class CommandLineParser
{
    public static string? TryGetExecutable(string? commandLine)
    {
        if (string.IsNullOrWhiteSpace(commandLine))
        {
            return null;
        }

        var expanded = Environment.ExpandEnvironmentVariables(commandLine.Trim());
        var argumentsPointer = CommandLineToArgvW(expanded, out var argumentCount);
        if (argumentsPointer == IntPtr.Zero)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        try
        {
            if (argumentCount == 0)
            {
                return null;
            }

            var firstArgumentPointer = Marshal.ReadIntPtr(argumentsPointer);
            return Marshal.PtrToStringUni(firstArgumentPointer);
        }
        finally
        {
            _ = LocalFree(argumentsPointer);
        }
    }

    [LibraryImport("shell32.dll", EntryPoint = "CommandLineToArgvW", SetLastError = true,
        StringMarshalling = StringMarshalling.Utf16)]
    private static partial IntPtr CommandLineToArgvW(string commandLine, out int argumentCount);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial IntPtr LocalFree(IntPtr memory);
}
