using RightMenuCheck.Core.Inventory;
using RightMenuCheck.Windows.Backup;

namespace RightMenuCheck.Windows.Tests.Backup;

internal sealed class FakeSecurityDescriptorReader(string defaultDescriptor = "O:BAG:BAD:(A;;KA;;;BA)")
    : IRegistrySecurityDescriptorReader
{
    private readonly Dictionary<string, string?> _descriptors =
        new(StringComparer.OrdinalIgnoreCase);

    public void Set(RegistrySource source, string? descriptor) =>
        _descriptors[CreateKey(source.Hive, source.View, source.KeyPath)] = descriptor;

    public string? GetSecurityDescriptorSddl(
        RegistryHiveKind hive,
        RegistryViewKind view,
        string keyPath) =>
        _descriptors.GetValueOrDefault(CreateKey(hive, view, keyPath), defaultDescriptor);

    private static string CreateKey(
        RegistryHiveKind hive,
        RegistryViewKind view,
        string keyPath) =>
        $"{hive}|{view}|{keyPath}";
}
