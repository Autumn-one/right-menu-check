using System.Security;
using RightMenuCheck.Core.Backup;
using RightMenuCheck.Core.Inventory;
using RightMenuCheck.Windows.Registry;

namespace RightMenuCheck.Windows.Backup;

public sealed record RegistrySnapshotCaptureResult(
    IReadOnlyList<RegistryKeySnapshot> Keys,
    IReadOnlyList<BackupCaptureIssue> Issues,
    bool IsComplete);

public sealed class RegistrySnapshotReader
{
    private const int MaximumDepth = 32;
    private const int MaximumKeys = 10_000;
    private readonly IRegistryReader _registryReader;
    private readonly IRegistrySecurityDescriptorReader _securityReader;

    public RegistrySnapshotReader(
        IRegistryReader registryReader,
        IRegistrySecurityDescriptorReader securityReader)
    {
        _registryReader = registryReader ?? throw new ArgumentNullException(nameof(registryReader));
        _securityReader = securityReader ?? throw new ArgumentNullException(nameof(securityReader));
    }

    public RegistrySnapshotCaptureResult Capture(
        RegistrySource root,
        CancellationToken cancellationToken = default)
    {
        var keys = new List<RegistryKeySnapshot>();
        var issues = new List<BackupCaptureIssue>();
        var pending = new Queue<(string Path, int Depth)>();
        pending.Enqueue((root.KeyPath, 0));

        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (keyPath, depth) = pending.Dequeue();
            if (keys.Count >= MaximumKeys)
            {
                issues.Add(new BackupCaptureIssue(
                    root.KeyPath,
                    "CaptureRegistryTree",
                    "KeyLimitExceeded",
                    $"Registry snapshot exceeds the {MaximumKeys} key safety limit."));
                break;
            }

            if (!TryKeyExists(root.Hive, root.View, keyPath, issues, out var exists) || !exists)
            {
                issues.Add(new BackupCaptureIssue(
                    keyPath,
                    "CaptureRegistryKey",
                    "KeyNotFound",
                    "The registry key no longer exists."));
                continue;
            }

            var values = ReadValues(root.Hive, root.View, keyPath, issues);
            var securityDescriptor = ReadSecurityDescriptor(
                root.Hive,
                root.View,
                keyPath,
                issues);
            keys.Add(new RegistryKeySnapshot(
                new RegistrySource(root.Hive, root.View, keyPath),
                securityDescriptor,
                values));

            var childNames = ReadSubKeyNames(root.Hive, root.View, keyPath, issues);
            if (childNames.Count > 0 && depth >= MaximumDepth)
            {
                issues.Add(new BackupCaptureIssue(
                    keyPath,
                    "CaptureRegistryTree",
                    "DepthLimitExceeded",
                    $"Registry snapshot exceeds the {MaximumDepth} level safety limit."));
                continue;
            }

            foreach (var childName in childNames.Order(StringComparer.OrdinalIgnoreCase))
            {
                pending.Enqueue(($"{keyPath}\\{childName}", depth + 1));
            }
        }

        var ordered = keys
            .OrderBy(static key => key.Source.KeyPath, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return new RegistrySnapshotCaptureResult(
            ordered,
            issues,
            IsComplete: issues.Count == 0);
    }

    internal static bool TryCreateValueSnapshot(
        string valueName,
        RegistryValueData value,
        out RegistryValueSnapshot? snapshot)
    {
        snapshot = value.Kind switch
        {
            RegistryValueDataKind.None when value.Value is byte[] bytes =>
                CreateBinary(valueName, BackupRegistryValueKind.None, bytes),
            RegistryValueDataKind.Text when value.Value is string text =>
                CreateText(valueName, BackupRegistryValueKind.Text, text),
            RegistryValueDataKind.ExpandableText when value.Value is string text =>
                CreateText(valueName, BackupRegistryValueKind.ExpandableText, text),
            RegistryValueDataKind.MultiText when value.Value is string[] items =>
                new RegistryValueSnapshot(
                    valueName,
                    BackupRegistryValueKind.MultiText,
                    Text: null,
                    items,
                    Base64Data: null,
                    NumericValue: null),
            RegistryValueDataKind.Binary when value.Value is byte[] bytes =>
                CreateBinary(valueName, BackupRegistryValueKind.Binary, bytes),
            RegistryValueDataKind.DWord when value.Value is int integer =>
                CreateInteger(valueName, BackupRegistryValueKind.DWord, integer),
            RegistryValueDataKind.QWord when value.Value is long integer =>
                CreateInteger(valueName, BackupRegistryValueKind.QWord, integer),
            _ => null,
        };
        return snapshot is not null;
    }

    private RegistryValueSnapshot[] ReadValues(
        RegistryHiveKind hive,
        RegistryViewKind view,
        string keyPath,
        List<BackupCaptureIssue> issues)
    {
        var result = new List<RegistryValueSnapshot>();
        foreach (var valueName in ReadValueNames(hive, view, keyPath, issues))
        {
            RegistryValueData? value;
            try
            {
                value = _registryReader.GetValue(hive, view, keyPath, valueName);
            }
            catch (UnauthorizedAccessException exception)
            {
                AddReadIssue(valueName, exception);
                continue;
            }
            catch (SecurityException exception)
            {
                AddReadIssue(valueName, exception);
                continue;
            }
            catch (IOException exception)
            {
                AddReadIssue(valueName, exception);
                continue;
            }

            if (value is null || !TryCreateValueSnapshot(valueName, value, out var snapshot))
            {
                issues.Add(new BackupCaptureIssue(
                    keyPath,
                    $"CaptureRegistryValue:{valueName}",
                    "UnsupportedValue",
                    "The registry value is missing or has an unsupported representation."));
                continue;
            }

            result.Add(snapshot!);

            void AddReadIssue(string name, Exception exception)
            {
                issues.Add(new BackupCaptureIssue(
                    keyPath,
                    $"CaptureRegistryValue:{name}",
                    exception.GetType().Name,
                    exception.Message));
            }
        }

        return result
            .OrderBy(static value => value.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private bool TryKeyExists(
        RegistryHiveKind hive,
        RegistryViewKind view,
        string keyPath,
        List<BackupCaptureIssue> issues,
        out bool exists)
    {
        try
        {
            exists = _registryReader.KeyExists(hive, view, keyPath);
            return true;
        }
        catch (UnauthorizedAccessException exception)
        {
            AddIssue(exception);
        }
        catch (SecurityException exception)
        {
            AddIssue(exception);
        }
        catch (IOException exception)
        {
            AddIssue(exception);
        }

        exists = false;
        return false;

        void AddIssue(Exception exception)
        {
            issues.Add(new BackupCaptureIssue(
                keyPath,
                "CheckRegistryKey",
                exception.GetType().Name,
                exception.Message));
        }
    }

    private IReadOnlyList<string> ReadSubKeyNames(
        RegistryHiveKind hive,
        RegistryViewKind view,
        string keyPath,
        List<BackupCaptureIssue> issues) =>
        ReadNames(
            keyPath,
            "EnumerateRegistrySubKeys",
            () => _registryReader.GetSubKeyNames(hive, view, keyPath),
            issues);

    private IReadOnlyList<string> ReadValueNames(
        RegistryHiveKind hive,
        RegistryViewKind view,
        string keyPath,
        List<BackupCaptureIssue> issues) =>
        ReadNames(
            keyPath,
            "EnumerateRegistryValues",
            () => _registryReader.GetValueNames(hive, view, keyPath),
            issues);

    private static IReadOnlyList<string> ReadNames(
        string keyPath,
        string operation,
        Func<IReadOnlyList<string>> read,
        List<BackupCaptureIssue> issues)
    {
        try
        {
            return read();
        }
        catch (UnauthorizedAccessException exception)
        {
            AddIssue(exception);
        }
        catch (SecurityException exception)
        {
            AddIssue(exception);
        }
        catch (IOException exception)
        {
            AddIssue(exception);
        }

        return [];

        void AddIssue(Exception exception)
        {
            issues.Add(new BackupCaptureIssue(
                keyPath,
                operation,
                exception.GetType().Name,
                exception.Message));
        }
    }

    private string? ReadSecurityDescriptor(
        RegistryHiveKind hive,
        RegistryViewKind view,
        string keyPath,
        List<BackupCaptureIssue> issues)
    {
        try
        {
            var descriptor = _securityReader.GetSecurityDescriptorSddl(hive, view, keyPath);
            if (descriptor is null)
            {
                issues.Add(new BackupCaptureIssue(
                    keyPath,
                    "ReadRegistrySecurity",
                    "SecurityDescriptorUnavailable",
                    "The registry security descriptor is unavailable."));
            }

            return descriptor;
        }
        catch (UnauthorizedAccessException exception)
        {
            AddIssue(exception);
        }
        catch (SecurityException exception)
        {
            AddIssue(exception);
        }
        catch (IOException exception)
        {
            AddIssue(exception);
        }

        return null;

        void AddIssue(Exception exception)
        {
            issues.Add(new BackupCaptureIssue(
                keyPath,
                "ReadRegistrySecurity",
                exception.GetType().Name,
                exception.Message));
        }
    }

    private static RegistryValueSnapshot CreateText(
        string valueName,
        BackupRegistryValueKind kind,
        string text) =>
        new(valueName, kind, text, TextItems: null, Base64Data: null, NumericValue: null);

    private static RegistryValueSnapshot CreateBinary(
        string valueName,
        BackupRegistryValueKind kind,
        byte[] bytes) =>
        new(
            valueName,
            kind,
            Text: null,
            TextItems: null,
            Convert.ToBase64String(bytes),
            NumericValue: null);

    private static RegistryValueSnapshot CreateInteger(
        string valueName,
        BackupRegistryValueKind kind,
        long integer) =>
        new(valueName, kind, Text: null, TextItems: null, Base64Data: null, integer);
}
