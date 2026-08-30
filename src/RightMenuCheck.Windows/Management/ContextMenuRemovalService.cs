using RightMenuCheck.Core.Backup;
using RightMenuCheck.Core.Inventory;
using RightMenuCheck.Core.Metadata;
using RightMenuCheck.Windows.Backup;
using RightMenuCheck.Windows.Registry;

namespace RightMenuCheck.Windows.Management;

public sealed record ContextMenuRemovalPlan(
    bool IsSupported,
    bool IsNoChange,
    bool RequiresElevation,
    string ImpactDescription,
    string? BlockReason,
    RegistryMutation? Mutation);

public sealed record PreparedContextMenuRemoval(
    ContextMenuRemovalPlan Plan,
    string RegistrationId,
    BackupArtifactInfo? Backup,
    RegistryMutationPlan? MutationPlan);

public sealed record ContextMenuRemovalResult(
    ContextMenuRemovalPlan Plan,
    BackupArtifactInfo? Backup,
    RegistryMutationResult? MutationResult);

public sealed class ContextMenuRemovalService
{
    private readonly IRegistryReader _registryReader;
    private readonly RightMenuBackupService _backupService;
    private readonly RegistryTransactionExecutor _transactionExecutor;

    public ContextMenuRemovalService(
        IRegistryReader registryReader,
        RightMenuBackupService backupService,
        RegistryTransactionExecutor transactionExecutor)
    {
        _registryReader = registryReader ?? throw new ArgumentNullException(nameof(registryReader));
        _backupService = backupService ?? throw new ArgumentNullException(nameof(backupService));
        _transactionExecutor = transactionExecutor ??
                               throw new ArgumentNullException(nameof(transactionExecutor));
    }

    public ContextMenuRemovalPlan CreatePlan(
        ContextMenuRegistrationMetadata metadata,
        bool allowSystemProtected)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        if (metadata.Owner?.IsSystemProtected == true && !allowSystemProtected)
        {
            return Unsupported("Microsoft or system-owned registrations are protected by default.");
        }

        if (metadata.Registration.Source is not RegistryContextMenuSource registrySource)
        {
            return Unsupported(
                "Packaged context-menu commands must be removed through the owning package uninstaller.");
        }

        var source = registrySource.Location;
        var exists = _registryReader.KeyExists(source.Hive, source.View, source.KeyPath);
        return new ContextMenuRemovalPlan(
            IsSupported: true,
            IsNoChange: !exists,
            RequiresElevation: source.Hive == RegistryHiveKind.LocalMachine,
            "Only the selected registration key tree is deleted. Shared CLSID registrations and binaries are retained.",
            BlockReason: null,
            exists
                ? new RegistryMutation(
                    RegistryMutationKind.DeleteKeyTree,
                    source,
                    ValueName: null,
                    Value: null,
                    KeyTree: null)
                : null);
    }

    public async Task<PreparedContextMenuRemoval> PrepareAsync(
        ContextMenuRegistrationMetadata metadata,
        string backupPath,
        bool allowSystemProtected,
        bool overwriteBackup,
        CancellationToken cancellationToken = default)
    {
        var plan = CreatePlan(metadata, allowSystemProtected);
        if (!plan.IsSupported || plan.IsNoChange)
        {
            return new PreparedContextMenuRemoval(
                plan,
                metadata.Registration.Id,
                Backup: null,
                MutationPlan: null);
        }

        var backup = await _backupService.CreateAsync(
                backupPath,
                [metadata],
                BackupPurpose.BeforeRemove,
                requireComplete: true,
                overwriteBackup,
                cancellationToken)
            .ConfigureAwait(false);
        var mutationPlan = new RegistryMutationPlan(
            Guid.NewGuid(),
            "Remove context menu registration",
            backup.Path,
            [plan.Mutation!]);
        return new PreparedContextMenuRemoval(
            plan,
            metadata.Registration.Id,
            backup,
            mutationPlan);
    }

    public async Task<ContextMenuRemovalResult> ExecuteLocalAsync(
        PreparedContextMenuRemoval prepared,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(prepared);
        if (!prepared.Plan.IsSupported)
        {
            throw new InvalidOperationException(prepared.Plan.BlockReason);
        }

        if (prepared.Plan.IsNoChange)
        {
            return new ContextMenuRemovalResult(
                prepared.Plan,
                prepared.Backup,
                MutationResult: null);
        }

        if (prepared.Plan.RequiresElevation)
        {
            throw new InvalidOperationException(
                "This prepared removal requires the elevated helper and cannot run in the UI process.");
        }

        var result = await _transactionExecutor.ExecuteAsync(
                prepared.MutationPlan ??
                throw new InvalidOperationException("Prepared removal has no mutation plan."),
                cancellationToken)
            .ConfigureAwait(false);
        return new ContextMenuRemovalResult(prepared.Plan, prepared.Backup, result);
    }

    private static ContextMenuRemovalPlan Unsupported(string reason) => new(
        IsSupported: false,
        IsNoChange: false,
        RequiresElevation: false,
        ImpactDescription: reason,
        BlockReason: reason,
        Mutation: null);
}
