namespace RightMenuCheck.Core.Inventory;

public enum RegistryHiveKind
{
    CurrentUser,
    LocalMachine,
}

public enum RegistryViewKind
{
    Registry32,
    Registry64,
}

public readonly record struct RegistrySource(
    RegistryHiveKind Hive,
    RegistryViewKind View,
    string KeyPath);
