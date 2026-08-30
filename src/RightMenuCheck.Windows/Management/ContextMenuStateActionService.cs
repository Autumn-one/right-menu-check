using RightMenuCheck.Core.Backup;
using RightMenuCheck.Core.Inventory;
using RightMenuCheck.Core.Metadata;
using RightMenuCheck.Windows.Backup;
using RightMenuCheck.Windows.Registry;

namespace RightMenuCheck.Windows.Management;

public enum ContextMenuStateAction
{
    Disable,
    Enable,
}

public sealed record ContextMenuStatePlan(
    ContextMenuStateAction Action,
    bool IsSupported,
    bool IsNoChange,
    bool RequiresElevation,
    bool HasGlobalClsidImpact,
    string ImpactDescription,
    string? BlockReason,
    IReadOnlyList<RegistryMutation> Mutations);

public sealed record PreparedContextMenuStateAction(
    ContextMenuStatePlan Plan,
    BackupArtifactInfo? Backup,
    RegistryMutationPlan? MutationPlan);

public sealed record ContextMenuStateExecutionResult(
    ContextMenuStatePlan Plan,
    BackupArtifactInfo? Backup,
    RegistryMutationResult? MutationResult);

public sealed class ContextMenuStateActionPlanner
{
    private const string BlockedPath =
        "Software\\Microsoft\\Windows\\CurrentVersion\\Shell Extensions\\Blocked";
    private readonly IRegistryReader _registryReader;

    public ContextMenuStateActionPlanner(IRegistryReader registryReader)
    {
        _registryReader = registryReader ?? throw new ArgumentNullException(nameof(registryReader));
    }

    public ContextMenuStatePlan CreatePlan(
        ContextMenuRegistrationMetadata metadata,
        ContextMenuStateAction action,
        bool allowSystemProtected)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        var registration = metadata.Registration;

        if (metadata.Owner?.IsSystemProtected == true && !allowSystemProtected)
        {
            return Unsupported(
                action,
                "Microsoft or system-owned registrations are protected by default.");
        }

        if (registration.Source is PackageContextMenuSource)
        {
            return Unsupported(
                action,
                "Packaged context-menu commands have no supported per-command registry disable operation.");
        }

        return registration.Kind switch
        {
            ContextMenuRegistrationKind.ClassicContextMenuHandler or
                ContextMenuRegistrationKind.ExplorerCommand =>
                CreateClsidBlockedPlan(registration, action),
            ContextMenuRegistrationKind.StaticVerb or
                ContextMenuRegistrationKind.DelegateExecuteVerb or
                ContextMenuRegistrationKind.CascadingVerb =>
                CreateLegacyDisablePlan(registration, action),
            _ => Unsupported(action, "This registration type does not support state changes."),
        };
    }

    private ContextMenuStatePlan CreateClsidBlockedPlan(
        ContextMenuRegistration registration,
        ContextMenuStateAction action)
    {
        if (registration.HandlerClsid is not { } clsid ||
            registration.Source is not RegistryContextMenuSource registrySource)
        {
            return Unsupported(action, "A classic handler CLSID and registry source are required.");
        }

        var blockedSource = new RegistrySource(
            RegistryHiveKind.CurrentUser,
            registrySource.Location.View,
            BlockedPath);
        var existing = _registryReader.GetValue(
            blockedSource.Hive,
            blockedSource.View,
            blockedSource.KeyPath,
            clsid);
        var isNoChange = action == ContextMenuStateAction.Disable
            ? existing is not null
            : existing is null;
        var mutations = isNoChange
            ? Array.Empty<RegistryMutation>()
            : action == ContextMenuStateAction.Disable
                ?
                [
                    new RegistryMutation(
                        RegistryMutationKind.SetValue,
                        blockedSource,
                        clsid,
                        new RegistryValueSnapshot(
                            clsid,
                            BackupRegistryValueKind.Text,
                            registration.DisplayName,
                            TextItems: null,
                            Base64Data: null,
                            NumericValue: null),
                        KeyTree: null),
                ]
                :
                [
                    new RegistryMutation(
                        RegistryMutationKind.DeleteValue,
                        blockedSource,
                        clsid,
                        Value: null,
                        KeyTree: null),
                ];

        return new ContextMenuStatePlan(
            action,
            IsSupported: true,
            isNoChange,
            RequiresElevation: false,
            HasGlobalClsidImpact: true,
            "The current-user Blocked value affects every context-menu registration that uses this CLSID.",
            BlockReason: null,
            mutations);
    }

    private ContextMenuStatePlan CreateLegacyDisablePlan(
        ContextMenuRegistration registration,
        ContextMenuStateAction action)
    {
        if (registration.Source is not RegistryContextMenuSource registrySource)
        {
            return Unsupported(action, "A registry source is required for LegacyDisable.");
        }

        var source = registrySource.Location;
        var existing = _registryReader.GetValue(
            source.Hive,
            source.View,
            source.KeyPath,
            "LegacyDisable");
        var isNoChange = action == ContextMenuStateAction.Disable
            ? existing is not null
            : existing is null;
        var mutations = isNoChange
            ? Array.Empty<RegistryMutation>()
            : action == ContextMenuStateAction.Disable
                ?
                [
                    new RegistryMutation(
                        RegistryMutationKind.SetValue,
                        source,
                        "LegacyDisable",
                        new RegistryValueSnapshot(
                            "LegacyDisable",
                            BackupRegistryValueKind.Text,
                            string.Empty,
                            TextItems: null,
                            Base64Data: null,
                            NumericValue: null),
                        KeyTree: null),
                ]
                :
                [
                    new RegistryMutation(
                        RegistryMutationKind.DeleteValue,
                        source,
                        "LegacyDisable",
                        Value: null,
                        KeyTree: null),
                ];

        return new ContextMenuStatePlan(
            action,
            IsSupported: true,
            isNoChange,
            RequiresElevation: source.Hive == RegistryHiveKind.LocalMachine,
            HasGlobalClsidImpact: false,
            "LegacyDisable changes only this registered verb key.",
            BlockReason: null,
            mutations);
    }

    private static ContextMenuStatePlan Unsupported(
        ContextMenuStateAction action,
        string reason) =>
        new(
            action,
            IsSupported: false,
            IsNoChange: false,
            RequiresElevation: false,
            HasGlobalClsidImpact: false,
            ImpactDescription: reason,
            BlockReason: reason,
            Mutations: []);
}

public sealed class ContextMenuStateActionService
{
    private readonly ContextMenuStateActionPlanner _planner;
    private readonly RightMenuBackupService _backupService;
    private readonly RegistryTransactionExecutor _transactionExecutor;

    public ContextMenuStateActionService(
        ContextMenuStateActionPlanner planner,
        RightMenuBackupService backupService,
        RegistryTransactionExecutor transactionExecutor)
    {
        _planner = planner ?? throw new ArgumentNullException(nameof(planner));
        _backupService = backupService ?? throw new ArgumentNullException(nameof(backupService));
        _transactionExecutor = transactionExecutor ??
                               throw new ArgumentNullException(nameof(transactionExecutor));
    }

    public async Task<PreparedContextMenuStateAction> PrepareAsync(
        ContextMenuRegistrationMetadata metadata,
        ContextMenuStateAction action,
        string backupPath,
        bool allowSystemProtected,
        bool overwriteBackup,
        CancellationToken cancellationToken = default)
    {
        var plan = _planner.CreatePlan(metadata, action, allowSystemProtected);
        if (!plan.IsSupported || plan.IsNoChange)
        {
            return new PreparedContextMenuStateAction(plan, Backup: null, MutationPlan: null);
        }

        var backup = await _backupService.CreateAsync(
                backupPath,
                [metadata],
                action == ContextMenuStateAction.Disable
                    ? BackupPurpose.BeforeDisable
                    : BackupPurpose.Manual,
                requireComplete: true,
                overwriteBackup,
                cancellationToken)
            .ConfigureAwait(false);
        var mutationPlan = new RegistryMutationPlan(
            Guid.NewGuid(),
            action == ContextMenuStateAction.Disable
                ? "Disable context menu registration"
                : "Enable context menu registration",
            backup.Path,
            plan.Mutations);
        return new PreparedContextMenuStateAction(plan, backup, mutationPlan);
    }

    public async Task<ContextMenuStateExecutionResult> ExecuteLocalAsync(
        PreparedContextMenuStateAction prepared,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(prepared);
        if (!prepared.Plan.IsSupported)
        {
            throw new InvalidOperationException(prepared.Plan.BlockReason);
        }

        if (prepared.Plan.IsNoChange)
        {
            return new ContextMenuStateExecutionResult(
                prepared.Plan,
                prepared.Backup,
                MutationResult: null);
        }

        if (prepared.Plan.RequiresElevation)
        {
            throw new InvalidOperationException(
                "This prepared action requires the elevated helper and cannot run in the UI process.");
        }

        var result = await _transactionExecutor.ExecuteAsync(
                prepared.MutationPlan ??
                throw new InvalidOperationException("Prepared action has no mutation plan."),
                cancellationToken)
            .ConfigureAwait(false);
        return new ContextMenuStateExecutionResult(prepared.Plan, prepared.Backup, result);
    }
}
