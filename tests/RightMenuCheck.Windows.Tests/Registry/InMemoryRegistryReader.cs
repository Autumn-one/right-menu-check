using RightMenuCheck.Core.Inventory;
using RightMenuCheck.Windows.Registry;

namespace RightMenuCheck.Windows.Tests.Registry;

internal sealed class InMemoryRegistryReader : IRegistryReader
{
    private readonly Dictionary<string, RegistryNode> _nodes = new(StringComparer.OrdinalIgnoreCase);

    public InMemoryRegistryReader(params RegistryViewKind[] availableViews)
    {
        AvailableViews = availableViews.Length == 0
            ? [RegistryViewKind.Registry64]
            : availableViews;
    }

    public IReadOnlyList<RegistryViewKind> AvailableViews { get; }

    public bool KeyExists(
        RegistryHiveKind hive,
        RegistryViewKind view,
        string keyPath) =>
        TryGetNode(hive, view, keyPath, out _);

    public void AddKey(RegistryHiveKind hive, RegistryViewKind view, string keyPath)
    {
        var parts = NormalizePath(keyPath).Split('\\', StringSplitOptions.RemoveEmptyEntries);
        var currentPath = string.Empty;

        foreach (var part in parts)
        {
            var parentPath = currentPath;
            currentPath = currentPath.Length == 0 ? part : $"{currentPath}\\{part}";
            GetOrAddNode(hive, view, currentPath);

            if (parentPath.Length > 0)
            {
                GetOrAddNode(hive, view, parentPath).SubKeys.Add(part);
            }
        }
    }

    public void SetValue(
        RegistryHiveKind hive,
        RegistryViewKind view,
        string keyPath,
        string? valueName,
        object? value,
        RegistryValueDataKind kind = RegistryValueDataKind.Text)
    {
        AddKey(hive, view, keyPath);
        var node = GetOrAddNode(hive, view, NormalizePath(keyPath));
        node.Values[valueName ?? string.Empty] = new RegistryValueData(value, kind);
    }

    public void DeleteValue(
        RegistryHiveKind hive,
        RegistryViewKind view,
        string keyPath,
        string? valueName)
    {
        if (TryGetNode(hive, view, keyPath, out var node))
        {
            _ = node.Values.Remove(valueName ?? string.Empty);
        }
    }

    public void DeleteKeyTree(
        RegistryHiveKind hive,
        RegistryViewKind view,
        string keyPath)
    {
        var normalized = NormalizePath(keyPath);
        var dictionaryPrefix = $"{hive}|{view}|";
        var fullKey = $"{dictionaryPrefix}{normalized}";
        var childPrefix = $"{fullKey}\\";
        var keysToRemove = _nodes.Keys.Where(key =>
                key.Equals(fullKey, StringComparison.OrdinalIgnoreCase) ||
                key.StartsWith(childPrefix, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        foreach (var key in keysToRemove)
        {
            _ = _nodes.Remove(key);
        }

        var separator = normalized.LastIndexOf('\\');
        if (separator > 0)
        {
            var parentPath = normalized[..separator];
            var childName = normalized[(separator + 1)..];
            if (TryGetNode(hive, view, parentPath, out var parent))
            {
                _ = parent.SubKeys.Remove(childName);
            }
        }
    }

    public IReadOnlyList<string> GetSubKeyNames(
        RegistryHiveKind hive,
        RegistryViewKind view,
        string keyPath) =>
        TryGetNode(hive, view, keyPath, out var node)
            ? node.SubKeys.Order(StringComparer.OrdinalIgnoreCase).ToArray()
            : [];

    public IReadOnlyList<string> GetValueNames(
        RegistryHiveKind hive,
        RegistryViewKind view,
        string keyPath) =>
        TryGetNode(hive, view, keyPath, out var node)
            ? node.Values.Keys.Order(StringComparer.OrdinalIgnoreCase).ToArray()
            : [];

    public RegistryValueData? GetValue(
        RegistryHiveKind hive,
        RegistryViewKind view,
        string keyPath,
        string? valueName)
    {
        if (!TryGetNode(hive, view, keyPath, out var node))
        {
            return null;
        }

        return node.Values.GetValueOrDefault(valueName ?? string.Empty);
    }

    private RegistryNode GetOrAddNode(
        RegistryHiveKind hive,
        RegistryViewKind view,
        string keyPath)
    {
        var key = CreateDictionaryKey(hive, view, keyPath);
        if (!_nodes.TryGetValue(key, out var node))
        {
            node = new RegistryNode();
            _nodes.Add(key, node);
        }

        return node;
    }

    private bool TryGetNode(
        RegistryHiveKind hive,
        RegistryViewKind view,
        string keyPath,
        out RegistryNode node) =>
        _nodes.TryGetValue(CreateDictionaryKey(hive, view, keyPath), out node!);

    private static string CreateDictionaryKey(
        RegistryHiveKind hive,
        RegistryViewKind view,
        string keyPath) =>
        $"{hive}|{view}|{NormalizePath(keyPath)}";

    private static string NormalizePath(string keyPath) =>
        keyPath.Replace('/', '\\').Trim('\\');

    private sealed class RegistryNode
    {
        public HashSet<string> SubKeys { get; } = new(StringComparer.OrdinalIgnoreCase);

        public Dictionary<string, RegistryValueData> Values { get; } =
            new(StringComparer.OrdinalIgnoreCase);
    }
}
