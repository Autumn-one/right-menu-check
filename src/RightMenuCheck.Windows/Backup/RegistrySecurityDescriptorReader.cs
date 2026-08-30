using Microsoft.Win32;
using RightMenuCheck.Core.Inventory;
using System.Security.AccessControl;

namespace RightMenuCheck.Windows.Backup;

public interface IRegistrySecurityDescriptorReader
{
    string? GetSecurityDescriptorSddl(
        RegistryHiveKind hive,
        RegistryViewKind view,
        string keyPath);
}

public sealed class SystemRegistrySecurityDescriptorReader : IRegistrySecurityDescriptorReader
{
    private const AccessControlSections BackupSections =
        AccessControlSections.Access |
        AccessControlSections.Owner |
        AccessControlSections.Group;

    public string? GetSecurityDescriptorSddl(
        RegistryHiveKind hive,
        RegistryViewKind view,
        string keyPath)
    {
        using var baseKey = RegistryKey.OpenBaseKey(MapHive(hive), MapView(view));
        using var key = baseKey.OpenSubKey(
            keyPath.Replace('/', '\\').Trim('\\'),
            writable: false);
        if (key is null)
        {
            return null;
        }

        var security = key.GetAccessControl(BackupSections);
        return security.GetSecurityDescriptorSddlForm(BackupSections);
    }

    private static RegistryHive MapHive(RegistryHiveKind hive) => hive switch
    {
        RegistryHiveKind.CurrentUser => RegistryHive.CurrentUser,
        RegistryHiveKind.LocalMachine => RegistryHive.LocalMachine,
        _ => throw new ArgumentOutOfRangeException(nameof(hive), hive, null),
    };

    private static RegistryView MapView(RegistryViewKind view) => view switch
    {
        RegistryViewKind.Registry32 => RegistryView.Registry32,
        RegistryViewKind.Registry64 => RegistryView.Registry64,
        _ => throw new ArgumentOutOfRangeException(nameof(view), view, null),
    };
}
