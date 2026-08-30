using RightMenuCheck.Core.Backup;
using RightMenuCheck.Core.Inventory;
using RightMenuCheck.Windows.Backup;

namespace RightMenuCheck.Windows.Management;

public enum RegistryRestoreMode
{
    Merge,
    Exact,
}

public sealed record RegistryRestorePlan(
    string BackupPath,
    RegistryRestoreMode Mode,
    RightMenuBackupManifest Manifest,
    RestorePreflightResult Preflight,
    RegistryMutationPlan MutationPlan,
    bool CanExecute,
    string? BlockReason);

public sealed class RegistryRestoreService
{
    private readonly IRightMenuBackupReader _backupReader;
    private readonly RestorePreflightService _preflightService;
    private readonly RegistryTransactionExecutor _transactionExecutor;

    public RegistryRestoreService(
        IRightMenuBackupReader backupReader,
        RestorePreflightService preflightService,
        RegistryTransactionExecutor transactionExecutor)
    {
        _backupReader = backupReader ?? throw new ArgumentNullException(nameof(backupReader));
        _preflightService = preflightService ??
                            throw new ArgumentNullException(nameof(preflightService));
        _transactionExecutor = transactionExecutor ??
                               throw new ArgumentNullException(nameof(transactionExecutor));
    }

    public async Task<RegistryRestorePlan> CreatePlanAsync(
        string backupPath,
        RegistryRestoreMode mode,
        CancellationToken cancellationToken = default)
    {
        var manifest = await _backupReader
            .ReadAsync(backupPath, cancellationToken)
            .ConfigureAwait(false);
        var preflight = await _preflightService
            .AnalyzeAsync(backupPath, cancellationToken)
            .ConfigureAwait(false);
        var roots = GetRestoreRoots(manifest);
        var mutations = roots
            .SelectMany(root => CreateRestoreMutations(root, manifest, mode))
            .ToArray();
        var blockReason = GetBlockReason(manifest, preflight, mutations);
        return new RegistryRestorePlan(
            Path.GetFullPath(backupPath),
            mode,
            manifest,
            preflight,
            new RegistryMutationPlan(
                Guid.NewGuid(),
                "Restore context menu registration",
                Path.GetFullPath(backupPath),
                mutations),
            CanExecute: blockReason is null,
            blockReason);
    }

    public Task<RegistryMutationResult> ExecuteAsync(
        RegistryRestorePlan plan,
        bool acceptConflicts,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (!plan.CanExecute)
        {
            throw new InvalidOperationException(plan.BlockReason ?? "Restore plan is blocked.");
        }

        if (plan.Preflight.Conflicts.Count > 0 && !acceptConflicts)
        {
            throw new InvalidOperationException(
                "Restore conflicts require explicit confirmation before registry mutation.");
        }

        return _transactionExecutor.ExecuteAsync(plan.MutationPlan, cancellationToken);
    }

    private static RegistrySource[] GetRestoreRoots(RightMenuBackupManifest manifest)
    {
        var candidates = manifest.Registrations
            .Where(static registration => registration.RegistrySource is not null)
            .Select(static registration => registration.RegistrySource!.Value)
            .GroupBy(static source =>
                $"{source.Hive}|{source.View}|{source.KeyPath}",
                StringComparer.OrdinalIgnoreCase)
            .Select(static group => group.First())
            .ToArray();

        return candidates.Where(candidate => !candidates.Any(other =>
                other != candidate &&
                other.Hive == candidate.Hive &&
                other.View == candidate.View &&
                IsStrictChildPath(candidate.KeyPath, other.KeyPath)))
            .ToArray();
    }

    private static IEnumerable<RegistryMutation> CreateRestoreMutations(
        RegistrySource root,
        RightMenuBackupManifest manifest,
        RegistryRestoreMode mode)
    {
        if (mode == RegistryRestoreMode.Exact)
        {
            yield return new RegistryMutation(
                RegistryMutationKind.DeleteKeyTree,
                root,
                ValueName: null,
                Value: null,
                KeyTree: null);
        }

        yield return new RegistryMutation(
            RegistryMutationKind.RestoreKeyTree,
            root,
            ValueName: null,
            Value: null,
            manifest.RegistryKeys.Where(key =>
                    key.Source.Hive == root.Hive &&
                    key.Source.View == root.View &&
                    IsSameOrChildPath(key.Source.KeyPath, root.KeyPath))
                .ToArray());
    }

    private static string? GetBlockReason(
        RightMenuBackupManifest manifest,
        RestorePreflightResult preflight,
        RegistryMutation[] mutations)
    {
        if (!manifest.IsComplete)
        {
            return "The backup is incomplete.";
        }

        if (!preflight.IntegrityVerified)
        {
            return "Backup integrity verification failed.";
        }

        if (preflight.Issues.Count > 0)
        {
            return "Restore preflight contains unresolved issues.";
        }

        if (mutations.Length == 0)
        {
            return "The backup contains no supported registry registration to restore.";
        }

        return null;
    }

    private static bool IsSameOrChildPath(string candidate, string root) =>
        candidate.Equals(root, StringComparison.OrdinalIgnoreCase) ||
        IsStrictChildPath(candidate, root);

    private static bool IsStrictChildPath(string candidate, string root) =>
        candidate.StartsWith($"{root.TrimEnd('\\')}\\", StringComparison.OrdinalIgnoreCase);
}
