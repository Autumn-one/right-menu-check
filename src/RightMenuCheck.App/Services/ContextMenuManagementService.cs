using System.IO;
using RightMenuCheck.Core.Backup;
using RightMenuCheck.Core.Metadata;
using RightMenuCheck.Windows.Backup;
using RightMenuCheck.Windows.Diagnostics;
using RightMenuCheck.Windows.Elevation;
using RightMenuCheck.Windows.Management;
using RightMenuCheck.Windows.Registry;

namespace RightMenuCheck.App.Services;

public sealed record ManagementExecutionResult(
    bool Succeeded,
    bool Cancelled,
    string Message,
    BackupArtifactInfo? Backup);

public interface IContextMenuManagementService
{
    ContextMenuStatePlan PreviewState(
        ContextMenuRegistrationMetadata metadata,
        ContextMenuStateAction action);

    ContextMenuRemovalPlan PreviewRemoval(ContextMenuRegistrationMetadata metadata);

    ApplicationUninstallPlan PreviewUninstall(ApplicationOwnerMetadata owner);

    Task<BackupArtifactInfo> CreateBackupAsync(
        string path,
        IReadOnlyList<ContextMenuRegistrationMetadata> registrations,
        BackupPurpose purpose,
        CancellationToken cancellationToken);

    Task<ManagementExecutionResult> ExecuteStateAsync(
        ContextMenuRegistrationMetadata metadata,
        ContextMenuStateAction action,
        string backupPath,
        CancellationToken cancellationToken);

    Task<ManagementExecutionResult> ExecuteRemovalAsync(
        ContextMenuRegistrationMetadata metadata,
        string backupPath,
        CancellationToken cancellationToken);

    Task<RegistryRestorePlan> CreateRestorePlanAsync(
        string backupPath,
        RegistryRestoreMode mode,
        CancellationToken cancellationToken);

    Task<ManagementExecutionResult> ExecuteRestoreAsync(
        RegistryRestorePlan plan,
        bool acceptConflicts,
        CancellationToken cancellationToken);

    Task<ApplicationUninstallExecutionResult> ExecuteUninstallAsync(
        ApplicationUninstallPlan plan,
        CancellationToken cancellationToken);
}

public sealed class ContextMenuManagementService : IContextMenuManagementService
{
    private readonly RightMenuBackupService _backupService;
    private readonly ContextMenuStateActionPlanner _statePlanner;
    private readonly ContextMenuStateActionService _stateService;
    private readonly ContextMenuRemovalService _removalService;
    private readonly RegistryRestoreService _restoreService;
    private readonly ElevatedHelperClient _elevatedClient = new();
    private readonly ApplicationUninstallPlanner _uninstallPlanner = new();
    private readonly ApplicationUninstallService _uninstallService = new(
        new SystemPackageUninstaller(),
        new SystemProcessUninstallLauncher());
    private readonly IAppLogger _logger;

    public ContextMenuManagementService(IAppLogger? logger = null)
    {
        _logger = logger ?? NullAppLogger.Instance;
        var registryReader = new SystemRegistryReader();
        var securityReader = new SystemRegistrySecurityDescriptorReader();
        var snapshotReader = new RegistrySnapshotReader(registryReader, securityReader);
        _backupService = new RightMenuBackupService(snapshotReader);
        var journalDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "RightMenuCheck",
            "Journals");
        var transaction = new RegistryTransactionExecutor(
            registryReader,
            snapshotReader,
            new SystemRegistryWriter(),
            new FileRegistryActionJournalStore(journalDirectory));
        _statePlanner = new ContextMenuStateActionPlanner(registryReader);
        _stateService = new ContextMenuStateActionService(
            _statePlanner,
            _backupService,
            transaction);
        _removalService = new ContextMenuRemovalService(
            registryReader,
            _backupService,
            transaction);
        var backupReader = new RightMenuBackupReader();
        _restoreService = new RegistryRestoreService(
            backupReader,
            new RestorePreflightService(backupReader, registryReader, securityReader),
            transaction);
    }

    public ContextMenuStatePlan PreviewState(
        ContextMenuRegistrationMetadata metadata,
        ContextMenuStateAction action) =>
        _statePlanner.CreatePlan(metadata, action, allowSystemProtected: false);

    public ContextMenuRemovalPlan PreviewRemoval(ContextMenuRegistrationMetadata metadata) =>
        _removalService.CreatePlan(metadata, allowSystemProtected: false);

    public ApplicationUninstallPlan PreviewUninstall(ApplicationOwnerMetadata owner) =>
        _uninstallPlanner.CreatePlan(owner, allowSystemProtected: false);

    public async Task<BackupArtifactInfo> CreateBackupAsync(
        string path,
        IReadOnlyList<ContextMenuRegistrationMetadata> registrations,
        BackupPurpose purpose,
        CancellationToken cancellationToken)
    {
        _logger.Log(
            AppLogLevel.Information,
            "backup.started",
            "Context-menu backup started.",
            new Dictionary<string, object?>
            {
                ["registrationCount"] = registrations.Count,
                ["purpose"] = purpose.ToString(),
                ["backupPath"] = path,
            });
        var artifact = await _backupService.CreateAsync(
                path,
                registrations,
                purpose,
                requireComplete: true,
                overwrite: true,
                cancellationToken)
            .ConfigureAwait(false);
        _logger.Log(
            AppLogLevel.Information,
            "backup.completed",
            "Context-menu backup completed.",
            new Dictionary<string, object?>
            {
                ["backupId"] = artifact.BackupId,
                ["registrationCount"] = artifact.RegistrationCount,
                ["registryKeyCount"] = artifact.RegistryKeyCount,
                ["isComplete"] = artifact.IsComplete,
            });
        return artifact;
    }

    public async Task<ManagementExecutionResult> ExecuteStateAsync(
        ContextMenuRegistrationMetadata metadata,
        ContextMenuStateAction action,
        string backupPath,
        CancellationToken cancellationToken)
    {
        _logger.Log(
            AppLogLevel.Information,
            "state_change.started",
            "Context-menu state change started.",
            new Dictionary<string, object?>
            {
                ["registrationId"] = metadata.Registration.Id,
                ["action"] = action.ToString(),
            });
        var prepared = await _stateService.PrepareAsync(
                metadata,
                action,
                backupPath,
                allowSystemProtected: false,
                overwriteBackup: true,
                cancellationToken)
            .ConfigureAwait(false);
        if (!prepared.Plan.IsSupported)
        {
            return Failure(prepared.Plan.BlockReason, prepared.Backup);
        }

        if (prepared.Plan.IsNoChange)
        {
            return Success("当前状态无需更改。", prepared.Backup);
        }

        if (prepared.Plan.RequiresElevation)
        {
            var response = await _elevatedClient.RunStateChangeAsync(
                    prepared,
                    ElevatedHelperLocator.GetOptions(),
                    cancellationToken)
                .ConfigureAwait(false);
            var elevatedResult = FromElevation(response, prepared.Backup);
            LogManagementResult("state_change.completed", elevatedResult);
            return elevatedResult;
        }

        var result = await _stateService
            .ExecuteLocalAsync(prepared, cancellationToken)
            .ConfigureAwait(false);
        var localResult = result.MutationResult?.Succeeded == true
            ? Success("菜单状态已更新。", result.Backup)
            : Failure(result.MutationResult?.ErrorMessage, result.Backup);
        LogManagementResult("state_change.completed", localResult);
        return localResult;
    }

    public async Task<ManagementExecutionResult> ExecuteRemovalAsync(
        ContextMenuRegistrationMetadata metadata,
        string backupPath,
        CancellationToken cancellationToken)
    {
        _logger.Log(
            AppLogLevel.Information,
            "registration_removal.started",
            "Context-menu registration removal started.",
            new Dictionary<string, object?>
            {
                ["registrationId"] = metadata.Registration.Id,
            });
        var prepared = await _removalService.PrepareAsync(
                metadata,
                backupPath,
                allowSystemProtected: false,
                overwriteBackup: true,
                cancellationToken)
            .ConfigureAwait(false);
        if (!prepared.Plan.IsSupported)
        {
            return Failure(prepared.Plan.BlockReason, prepared.Backup);
        }

        if (prepared.Plan.IsNoChange)
        {
            return Success("注册键已经不存在。", prepared.Backup);
        }

        if (prepared.Plan.RequiresElevation)
        {
            var response = await _elevatedClient.RunRemovalAsync(
                    prepared,
                    ElevatedHelperLocator.GetOptions(),
                    cancellationToken)
                .ConfigureAwait(false);
            var elevatedResult = FromElevation(response, prepared.Backup);
            LogManagementResult("registration_removal.completed", elevatedResult);
            return elevatedResult;
        }

        var result = await _removalService
            .ExecuteLocalAsync(prepared, cancellationToken)
            .ConfigureAwait(false);
        var localResult = result.MutationResult?.Succeeded == true
            ? Success("菜单注册已删除。", result.Backup)
            : Failure(result.MutationResult?.ErrorMessage, result.Backup);
        LogManagementResult("registration_removal.completed", localResult);
        return localResult;
    }

    public Task<RegistryRestorePlan> CreateRestorePlanAsync(
        string backupPath,
        RegistryRestoreMode mode,
        CancellationToken cancellationToken) =>
        _restoreService.CreatePlanAsync(backupPath, mode, cancellationToken);

    public async Task<ManagementExecutionResult> ExecuteRestoreAsync(
        RegistryRestorePlan plan,
        bool acceptConflicts,
        CancellationToken cancellationToken)
    {
        _logger.Log(
            AppLogLevel.Information,
            "restore.started",
            "Context-menu restore started.",
            new Dictionary<string, object?>
            {
                ["backupId"] = plan.Manifest.BackupId,
                ["mode"] = plan.Mode.ToString(),
                ["conflictCount"] = plan.Preflight.Conflicts.Count,
            });
        var requiresElevation = plan.MutationPlan.Mutations.Any(static mutation =>
            mutation.Source.Hive == RightMenuCheck.Core.Inventory.RegistryHiveKind.LocalMachine);
        if (requiresElevation)
        {
            var response = await _elevatedClient.RunRestoreAsync(
                    plan,
                    acceptConflicts,
                    ElevatedHelperLocator.GetOptions(),
                    cancellationToken)
                .ConfigureAwait(false);
            var elevatedResult = FromElevation(response, Backup: null);
            LogManagementResult("restore.completed", elevatedResult);
            return elevatedResult;
        }

        var result = await _restoreService
            .ExecuteAsync(plan, acceptConflicts, cancellationToken)
            .ConfigureAwait(false);
        var localResult = result.Succeeded
            ? Success("备份已恢复。", Backup: null)
            : Failure(result.ErrorMessage, Backup: null);
        LogManagementResult("restore.completed", localResult);
        return localResult;
    }

    public async Task<ApplicationUninstallExecutionResult> ExecuteUninstallAsync(
        ApplicationUninstallPlan plan,
        CancellationToken cancellationToken)
    {
        _logger.Log(
            AppLogLevel.Warning,
            "uninstall.started",
            "Application uninstaller started.",
            new Dictionary<string, object?>
            {
                ["owner"] = plan.Owner.DisplayName,
                ["method"] = plan.Method.ToString(),
            });
        var result = await _uninstallService
            .ExecuteAsync(plan, cancellationToken)
            .ConfigureAwait(false);
        _logger.Log(
            result.Completed ? AppLogLevel.Information : AppLogLevel.Warning,
            "uninstall.completed",
            "Application uninstaller completed.",
            new Dictionary<string, object?>
            {
                ["owner"] = plan.Owner.DisplayName,
                ["started"] = result.Started,
                ["completed"] = result.Completed,
                ["cancelled"] = result.Cancelled,
                ["exitCode"] = result.ExitCode,
                ["errorType"] = result.ErrorType,
            });
        return result;
    }

    private static ManagementExecutionResult FromElevation(
        ElevationResponse response,
        BackupArtifactInfo? Backup) => response.Outcome switch
    {
        ElevationOutcome.Succeeded => Success("管理员操作已完成。", Backup),
        ElevationOutcome.Cancelled => new ManagementExecutionResult(
            Succeeded: false,
            Cancelled: true,
            response.ErrorMessage ?? "管理员授权已取消。",
            Backup),
        _ => Failure(response.ErrorMessage, Backup),
    };

    private static ManagementExecutionResult Success(
        string message,
        BackupArtifactInfo? Backup) =>
        new(Succeeded: true, Cancelled: false, message, Backup);

    private static ManagementExecutionResult Failure(
        string? message,
        BackupArtifactInfo? Backup) =>
        new(
            Succeeded: false,
            Cancelled: false,
            message ?? "操作失败。",
            Backup);

    private void LogManagementResult(string eventName, ManagementExecutionResult result)
    {
        _logger.Log(
            result.Succeeded ? AppLogLevel.Information : AppLogLevel.Warning,
            eventName,
            result.Message,
            new Dictionary<string, object?>
            {
                ["succeeded"] = result.Succeeded,
                ["cancelled"] = result.Cancelled,
                ["backupId"] = result.Backup?.BackupId,
            });
    }
}

internal static class ElevatedHelperLocator
{
    private const string HelperFileName = "RightMenuCheck.Elevated.exe";

    public static ElevatedHelperOptions GetOptions()
    {
        var baseDirectory = AppContext.BaseDirectory;
        var candidates = new List<string>
        {
            Path.Combine(baseDirectory, "helpers", HelperFileName),
        };
        var artifactsRoot = FindArtifactsRoot(baseDirectory);
        if (artifactsRoot is not null)
        {
            candidates.Add(Path.Combine(
                artifactsRoot,
                "bin",
                "RightMenuCheck.Elevated",
                "debug",
                HelperFileName));
        }

        var helperPath = candidates.FirstOrDefault(File.Exists) ?? candidates[0];
        return new ElevatedHelperOptions(helperPath, TimeSpan.FromMinutes(3));
    }

    private static string? FindArtifactsRoot(string path)
    {
        var directory = new DirectoryInfo(path);
        while (directory is not null)
        {
            if (directory.Name.Equals("artifacts", StringComparison.OrdinalIgnoreCase))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        return null;
    }
}
