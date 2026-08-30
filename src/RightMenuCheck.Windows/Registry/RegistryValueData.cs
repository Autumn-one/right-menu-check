using RightMenuCheck.Core.Inventory;

namespace RightMenuCheck.Windows.Registry;

public enum RegistryValueDataKind
{
    Unknown,
    None,
    Text,
    ExpandableText,
    MultiText,
    Binary,
    DWord,
    QWord,
}

public sealed record RegistryValueData(object? Value, RegistryValueDataKind Kind);

public interface IRegistryReader
{
    IReadOnlyList<RegistryViewKind> AvailableViews { get; }

    IReadOnlyList<string> GetSubKeyNames(
        RegistryHiveKind hive,
        RegistryViewKind view,
        string keyPath);

    IReadOnlyList<string> GetValueNames(
        RegistryHiveKind hive,
        RegistryViewKind view,
        string keyPath);

    RegistryValueData? GetValue(
        RegistryHiveKind hive,
        RegistryViewKind view,
        string keyPath,
        string? valueName);
}
