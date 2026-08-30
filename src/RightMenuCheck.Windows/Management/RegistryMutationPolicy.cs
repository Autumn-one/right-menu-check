using RightMenuCheck.Core.Inventory;

namespace RightMenuCheck.Windows.Management;

internal static class RegistryMutationPolicy
{
    private const string ClassesPrefix = "Software\\Classes\\";
    private const string BlockedPath =
        "Software\\Microsoft\\Windows\\CurrentVersion\\Shell Extensions\\Blocked";

    public static void Validate(RegistrySource source)
    {
        var normalized = source.KeyPath.Replace('/', '\\').Trim('\\');
        var segments = normalized.Split('\\', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Any(static segment => segment is "." or "..") || normalized.Length == 0)
        {
            throw new InvalidOperationException("Registry mutation path is invalid.");
        }

        if (normalized.Equals(BlockedPath, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (!normalized.StartsWith(ClassesPrefix, StringComparison.OrdinalIgnoreCase) ||
            normalized.Equals("Software\\Classes", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "Registry mutation is outside the context-menu allowlist.");
        }
    }
}
