using RightMenuCheck.Installation;

namespace RightMenuCheck.Installer;

internal static class TemporaryUninstallWorkerCleanup
{
    public static void TryCleanup()
    {
        var root = new DirectoryInfo(Path.Combine(Path.GetTempPath(), "RightMenuCheck"));
        if (!root.Exists || root.Attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            return;
        }

        foreach (var directory in root.EnumerateDirectories("Uninstall-*"))
        {
            if (directory.Name.Length != "Uninstall-".Length + 32 ||
                !Guid.TryParseExact(directory.Name["Uninstall-".Length..], "N", out _))
            {
                continue;
            }

            try
            {
                SafeFileTree.DeleteDirectory(directory.FullName);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
            catch (InvalidDataException)
            {
            }
        }
    }
}
