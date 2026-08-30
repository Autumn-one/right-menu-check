using System.ComponentModel;
using System.Runtime.InteropServices;

namespace RightMenuCheck.Windows.Metadata;

internal static partial class CommandLineParser
{
    public static IReadOnlyList<string> Parse(string commandLine)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(commandLine);
        var expanded = Environment.ExpandEnvironmentVariables(commandLine.Trim());
        var argumentsPointer = CommandLineToArgvW(expanded, out var argumentCount);
        if (argumentsPointer == IntPtr.Zero)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        try
        {
            var result = new string[argumentCount];
            for (var index = 0; index < argumentCount; index++)
            {
                var argumentPointer = Marshal.ReadIntPtr(argumentsPointer, index * IntPtr.Size);
                result[index] = Marshal.PtrToStringUni(argumentPointer) ?? string.Empty;
            }

            return result;
        }
        finally
        {
            _ = LocalFree(argumentsPointer);
        }
    }

    public static string? TryGetExecutable(string? commandLine)
    {
        if (string.IsNullOrWhiteSpace(commandLine))
        {
            return null;
        }

        var arguments = Parse(commandLine);
        return arguments.Count == 0 ? null : arguments[0];
    }

    [LibraryImport("shell32.dll", EntryPoint = "CommandLineToArgvW", SetLastError = true,
        StringMarshalling = StringMarshalling.Utf16)]
    private static partial IntPtr CommandLineToArgvW(string commandLine, out int argumentCount);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial IntPtr LocalFree(IntPtr memory);
}
