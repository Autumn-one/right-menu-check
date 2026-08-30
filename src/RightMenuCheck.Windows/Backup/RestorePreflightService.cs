using System.Security;
using RightMenuCheck.Core.Backup;
using RightMenuCheck.Core.Inventory;
using RightMenuCheck.Windows.Registry;

namespace RightMenuCheck.Windows.Backup;

public sealed class RestorePreflightService
{
    private readonly IRightMenuBackupReader _backupReader;
    private readonly IRegistryReader _registryReader;
    private readonly IRegistrySecurityDescriptorReader _securityReader;

    public RestorePreflightService(
        IRightMenuBackupReader backupReader,
        IRegistryReader registryReader,
        IRegistrySecurityDescriptorReader securityReader)
    {
        _backupReader = backupReader ?? throw new ArgumentNullException(nameof(backupReader));
        _registryReader = registryReader ?? throw new ArgumentNullException(nameof(registryReader));
        _securityReader = securityReader ?? throw new ArgumentNullException(nameof(securityReader));
    }

    public async Task<RestorePreflightResult> AnalyzeAsync(
        string backupPath,
        CancellationToken cancellationToken = default)
    {
        var manifest = await _backupReader
            .ReadAsync(backupPath, cancellationToken)
            .ConfigureAwait(false);
        var conflicts = new List<RestoreConflict>();
        var issues = new List<BackupCaptureIssue>();
        var keysToCreate = 0;
        var keysToUpdate = 0;
        var valuesToWrite = 0;

        if (!manifest.IsComplete)
        {
            issues.Add(new BackupCaptureIssue(
                manifest.BackupId.ToString("D"),
                "RestorePreflight",
                "IncompleteBackup",
                "The backup was captured with unresolved issues."));
        }

        if (manifest.Registrations.Any(static registration =>
                registration.Package is not null && registration.RegistrySource is null))
        {
            issues.Add(new BackupCaptureIssue(
                manifest.BackupId.ToString("D"),
                "RestorePreflight",
                "PackageRestoreUnsupported",
                "Package manifest metadata cannot reinstall or re-register an uninstalled application package."));
        }

        foreach (var snapshot in manifest.RegistryKeys)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!TryKeyExists(snapshot.Source, issues, out var exists))
            {
                continue;
            }

            if (!exists)
            {
                keysToCreate++;
                valuesToWrite += snapshot.Values.Count;
                conflicts.Add(new RestoreConflict(
                    snapshot.Source,
                    ValueName: null,
                    RestoreConflictKind.MissingKey,
                    "The backed-up registry key does not currently exist."));
                continue;
            }

            var keyNeedsUpdate = false;
            var currentValueNames = ReadValueNames(snapshot.Source, issues);
            var currentValueNameSet = currentValueNames.ToHashSet(StringComparer.OrdinalIgnoreCase);
            var snapshotValueNameSet = snapshot.Values
                .Select(static value => value.Name)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            foreach (var backedUpValue in snapshot.Values)
            {
                if (!currentValueNameSet.Contains(backedUpValue.Name))
                {
                    keyNeedsUpdate = true;
                    valuesToWrite++;
                    conflicts.Add(new RestoreConflict(
                        snapshot.Source,
                        backedUpValue.Name,
                        RestoreConflictKind.MissingValue,
                        "The backed-up value is missing from the current key."));
                    continue;
                }

                var currentValue = ReadValue(snapshot.Source, backedUpValue.Name, issues);
                if (currentValue is null ||
                    !RegistrySnapshotReader.TryCreateValueSnapshot(
                        backedUpValue.Name,
                        currentValue,
                        out var currentSnapshot) ||
                    !ValueEquals(backedUpValue, currentSnapshot!))
                {
                    keyNeedsUpdate = true;
                    valuesToWrite++;
                    conflicts.Add(new RestoreConflict(
                        snapshot.Source,
                        backedUpValue.Name,
                        RestoreConflictKind.DifferentValue,
                        "The current value differs from the backed-up value."));
                }
            }

            foreach (var extraValueName in currentValueNames.Where(name =>
                         !snapshotValueNameSet.Contains(name)))
            {
                conflicts.Add(new RestoreConflict(
                    snapshot.Source,
                    extraValueName,
                    RestoreConflictKind.ExtraCurrentValue,
                    "The current key contains a value that is not present in the backup; merge restore would keep it."));
            }

            if (snapshot.SecurityDescriptorSddl is not null &&
                ReadSecurityDescriptor(snapshot.Source, issues) is { } currentSddl &&
                !currentSddl.Equals(
                    snapshot.SecurityDescriptorSddl,
                    StringComparison.Ordinal))
            {
                keyNeedsUpdate = true;
                conflicts.Add(new RestoreConflict(
                    snapshot.Source,
                    ValueName: null,
                    RestoreConflictKind.DifferentSecurityDescriptor,
                    "The current registry security descriptor differs from the backup."));
            }

            if (keyNeedsUpdate)
            {
                keysToUpdate++;
            }
        }

        return new RestorePreflightResult(
            manifest.BackupId,
            IntegrityVerified: true,
            keysToCreate,
            keysToUpdate,
            valuesToWrite,
            conflicts,
            issues);
    }

    private bool TryKeyExists(
        RegistrySource source,
        List<BackupCaptureIssue> issues,
        out bool exists)
    {
        try
        {
            exists = _registryReader.KeyExists(source.Hive, source.View, source.KeyPath);
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
            issues.Add(CreateIssue(source, "CheckCurrentKey", exception));
        }
    }

    private IReadOnlyList<string> ReadValueNames(
        RegistrySource source,
        List<BackupCaptureIssue> issues)
    {
        try
        {
            return _registryReader.GetValueNames(source.Hive, source.View, source.KeyPath);
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
            issues.Add(CreateIssue(source, "ReadCurrentValueNames", exception));
        }
    }

    private RegistryValueData? ReadValue(
        RegistrySource source,
        string valueName,
        List<BackupCaptureIssue> issues)
    {
        try
        {
            return _registryReader.GetValue(
                source.Hive,
                source.View,
                source.KeyPath,
                valueName);
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
            issues.Add(CreateIssue(source, $"ReadCurrentValue:{valueName}", exception));
        }
    }

    private string? ReadSecurityDescriptor(
        RegistrySource source,
        List<BackupCaptureIssue> issues)
    {
        try
        {
            return _securityReader.GetSecurityDescriptorSddl(
                source.Hive,
                source.View,
                source.KeyPath);
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
            issues.Add(CreateIssue(source, "ReadCurrentSecurity", exception));
        }
    }

    private static bool ValueEquals(
        RegistryValueSnapshot left,
        RegistryValueSnapshot right) =>
        left.Kind == right.Kind &&
        string.Equals(left.Text, right.Text, StringComparison.Ordinal) &&
        string.Equals(left.Base64Data, right.Base64Data, StringComparison.Ordinal) &&
        left.NumericValue == right.NumericValue &&
        SequenceEquals(left.TextItems, right.TextItems);

    private static bool SequenceEquals(
        IReadOnlyList<string>? left,
        IReadOnlyList<string>? right)
    {
        if (ReferenceEquals(left, right))
        {
            return true;
        }

        if (left is null || right is null || left.Count != right.Count)
        {
            return false;
        }

        for (var index = 0; index < left.Count; index++)
        {
            if (!left[index].Equals(right[index], StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    private static BackupCaptureIssue CreateIssue(
        RegistrySource source,
        string operation,
        Exception exception) =>
        new(
            source.KeyPath,
            operation,
            exception.GetType().Name,
            exception.Message);
}
