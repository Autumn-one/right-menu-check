using System.Security.Principal;

namespace RightMenuCheck.Windows.Security;

public static class ProcessElevationPolicy
{
    public static bool IsCurrentProcessElevated()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    public static void ThrowIfElevated(string operation)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operation);
        if (IsCurrentProcessElevated())
        {
            throw new InvalidOperationException(
                $"{operation} requires a standard, non-elevated process token.");
        }
    }
}
