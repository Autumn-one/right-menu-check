using Microsoft.Win32;
using RightMenuCheck.Core.Backup;
using RightMenuCheck.Core.Inventory;
using System.Security.AccessControl;

namespace RightMenuCheck.Windows.Management;

public interface IRegistryWriter
{
    void SetValue(RegistrySource source, RegistryValueSnapshot value);

    void DeleteValue(RegistrySource source, string valueName);

    void DeleteKeyTree(RegistrySource source);

    void RestoreKeyTree(RegistrySource root, IReadOnlyList<RegistryKeySnapshot> keys);
}

public sealed class SystemRegistryWriter : IRegistryWriter
{
    private const AccessControlSections BackupSections =
        AccessControlSections.Access |
        AccessControlSections.Owner |
        AccessControlSections.Group;

    public void SetValue(RegistrySource source, RegistryValueSnapshot value)
    {
        RegistryMutationPolicy.Validate(source);
        using var baseKey = OpenBaseKey(source);
        using var key = baseKey.CreateSubKey(source.KeyPath, writable: true) ??
                        throw new IOException("Registry key could not be created or opened.");
        var (nativeValue, nativeKind) = ConvertValue(value);
        key.SetValue(value.Name, nativeValue, nativeKind);
    }

    public void DeleteValue(RegistrySource source, string valueName)
    {
        RegistryMutationPolicy.Validate(source);
        using var baseKey = OpenBaseKey(source);
        using var key = baseKey.OpenSubKey(source.KeyPath, writable: true);
        key?.DeleteValue(valueName, throwOnMissingValue: false);
    }

    public void DeleteKeyTree(RegistrySource source)
    {
        RegistryMutationPolicy.Validate(source);
        var normalized = source.KeyPath.Replace('/', '\\').Trim('\\');
        var separator = normalized.LastIndexOf('\\');
        if (separator <= 0 || separator == normalized.Length - 1)
        {
            throw new InvalidOperationException("Registry key tree target has no safe parent.");
        }

        var parentPath = normalized[..separator];
        var childName = normalized[(separator + 1)..];
        using var baseKey = OpenBaseKey(source);
        using var parent = baseKey.OpenSubKey(parentPath, writable: true);
        parent?.DeleteSubKeyTree(childName, throwOnMissingSubKey: false);
    }

    public void RestoreKeyTree(RegistrySource root, IReadOnlyList<RegistryKeySnapshot> keys)
    {
        RegistryMutationPolicy.Validate(root);
        ArgumentNullException.ThrowIfNull(keys);

        foreach (var keySnapshot in keys.OrderBy(static key => GetDepth(key.Source.KeyPath)))
        {
            RegistryMutationPolicy.Validate(keySnapshot.Source);
            if (keySnapshot.Source.Hive != root.Hive || keySnapshot.Source.View != root.View ||
                !IsSameOrChildPath(keySnapshot.Source.KeyPath, root.KeyPath))
            {
                throw new InvalidOperationException("Registry snapshot escapes its declared root.");
            }

            using var baseKey = OpenBaseKey(keySnapshot.Source);
            using var key = baseKey.CreateSubKey(keySnapshot.Source.KeyPath, writable: true) ??
                            throw new IOException("Registry key could not be restored.");
            foreach (var value in keySnapshot.Values)
            {
                var (nativeValue, nativeKind) = ConvertValue(value);
                key.SetValue(value.Name, nativeValue, nativeKind);
            }

            if (!string.IsNullOrWhiteSpace(keySnapshot.SecurityDescriptorSddl))
            {
                var security = new RegistrySecurity();
                security.SetSecurityDescriptorSddlForm(
                    keySnapshot.SecurityDescriptorSddl,
                    BackupSections);
                key.SetAccessControl(security);
            }
        }
    }

    private static (object Value, RegistryValueKind Kind) ConvertValue(
        RegistryValueSnapshot snapshot) => snapshot.Kind switch
    {
        BackupRegistryValueKind.None => (
            Convert.FromBase64String(snapshot.Base64Data ?? string.Empty),
            RegistryValueKind.None),
        BackupRegistryValueKind.Text => (
            snapshot.Text ?? string.Empty,
            RegistryValueKind.String),
        BackupRegistryValueKind.ExpandableText => (
            snapshot.Text ?? string.Empty,
            RegistryValueKind.ExpandString),
        BackupRegistryValueKind.MultiText => (
            snapshot.TextItems?.ToArray() ?? [],
            RegistryValueKind.MultiString),
        BackupRegistryValueKind.Binary => (
            Convert.FromBase64String(snapshot.Base64Data ?? string.Empty),
            RegistryValueKind.Binary),
        BackupRegistryValueKind.DWord => (
            checked((int)(snapshot.NumericValue ?? 0)),
            RegistryValueKind.DWord),
        BackupRegistryValueKind.QWord => (
            snapshot.NumericValue ?? 0,
            RegistryValueKind.QWord),
        _ => throw new ArgumentOutOfRangeException(nameof(snapshot), snapshot.Kind, null),
    };

    private static RegistryKey OpenBaseKey(RegistrySource source) => RegistryKey.OpenBaseKey(
        source.Hive switch
        {
            RegistryHiveKind.CurrentUser => RegistryHive.CurrentUser,
            RegistryHiveKind.LocalMachine => RegistryHive.LocalMachine,
            _ => throw new ArgumentOutOfRangeException(nameof(source), source.Hive, null),
        },
        source.View switch
        {
            RegistryViewKind.Registry32 => RegistryView.Registry32,
            RegistryViewKind.Registry64 => RegistryView.Registry64,
            _ => throw new ArgumentOutOfRangeException(nameof(source), source.View, null),
        });

    private static int GetDepth(string keyPath) => keyPath.Count(static character => character == '\\');

    private static bool IsSameOrChildPath(string candidate, string root) =>
        candidate.Equals(root, StringComparison.OrdinalIgnoreCase) ||
        candidate.StartsWith($"{root.TrimEnd('\\')}\\", StringComparison.OrdinalIgnoreCase);
}
