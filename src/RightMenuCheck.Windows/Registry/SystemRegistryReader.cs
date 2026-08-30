using Microsoft.Win32;
using RightMenuCheck.Core.Inventory;

namespace RightMenuCheck.Windows.Registry;

public sealed class SystemRegistryReader : IRegistryReader
{
    private static readonly IReadOnlyList<RegistryViewKind> Views64Bit =
        [RegistryViewKind.Registry64, RegistryViewKind.Registry32];

    private static readonly IReadOnlyList<RegistryViewKind> Views32Bit =
        [RegistryViewKind.Registry32];

    public IReadOnlyList<RegistryViewKind> AvailableViews =>
        Environment.Is64BitOperatingSystem ? Views64Bit : Views32Bit;

    public bool KeyExists(
        RegistryHiveKind hive,
        RegistryViewKind view,
        string keyPath)
    {
        using var baseKey = OpenBaseKey(hive, view);
        using var key = baseKey.OpenSubKey(NormalizeKeyPath(keyPath), writable: false);
        return key is not null;
    }

    public IReadOnlyList<string> GetSubKeyNames(
        RegistryHiveKind hive,
        RegistryViewKind view,
        string keyPath)
    {
        using var baseKey = OpenBaseKey(hive, view);
        using var key = baseKey.OpenSubKey(NormalizeKeyPath(keyPath), writable: false);
        return key?.GetSubKeyNames() ?? [];
    }

    public IReadOnlyList<string> GetValueNames(
        RegistryHiveKind hive,
        RegistryViewKind view,
        string keyPath)
    {
        using var baseKey = OpenBaseKey(hive, view);
        using var key = baseKey.OpenSubKey(NormalizeKeyPath(keyPath), writable: false);
        return key?.GetValueNames() ?? [];
    }

    public RegistryValueData? GetValue(
        RegistryHiveKind hive,
        RegistryViewKind view,
        string keyPath,
        string? valueName)
    {
        using var baseKey = OpenBaseKey(hive, view);
        using var key = baseKey.OpenSubKey(NormalizeKeyPath(keyPath), writable: false);
        if (key is null)
        {
            return null;
        }

        var normalizedValueName = valueName ?? string.Empty;
        if (!key.GetValueNames().Contains(normalizedValueName, StringComparer.OrdinalIgnoreCase))
        {
            return null;
        }

        var kind = key.GetValueKind(normalizedValueName);
        var value = key.GetValue(
            normalizedValueName,
            defaultValue: null,
            RegistryValueOptions.DoNotExpandEnvironmentNames);

        return new RegistryValueData(value, MapValueKind(kind));
    }

    private static RegistryKey OpenBaseKey(RegistryHiveKind hive, RegistryViewKind view)
    {
        var nativeHive = hive switch
        {
            RegistryHiveKind.CurrentUser => RegistryHive.CurrentUser,
            RegistryHiveKind.LocalMachine => RegistryHive.LocalMachine,
            _ => throw new ArgumentOutOfRangeException(nameof(hive), hive, null),
        };

        var nativeView = view switch
        {
            RegistryViewKind.Registry32 => RegistryView.Registry32,
            RegistryViewKind.Registry64 => RegistryView.Registry64,
            _ => throw new ArgumentOutOfRangeException(nameof(view), view, null),
        };

        return RegistryKey.OpenBaseKey(nativeHive, nativeView);
    }

    private static string NormalizeKeyPath(string keyPath) =>
        keyPath.Replace('/', '\\').Trim('\\');

    private static RegistryValueDataKind MapValueKind(RegistryValueKind kind) => kind switch
    {
        RegistryValueKind.None => RegistryValueDataKind.None,
        RegistryValueKind.String => RegistryValueDataKind.Text,
        RegistryValueKind.ExpandString => RegistryValueDataKind.ExpandableText,
        RegistryValueKind.MultiString => RegistryValueDataKind.MultiText,
        RegistryValueKind.Binary => RegistryValueDataKind.Binary,
        RegistryValueKind.DWord => RegistryValueDataKind.DWord,
        RegistryValueKind.QWord => RegistryValueDataKind.QWord,
        RegistryValueKind.Unknown => RegistryValueDataKind.Unknown,
        _ => RegistryValueDataKind.Unknown,
    };
}
