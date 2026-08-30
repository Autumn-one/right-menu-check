using System.ComponentModel;
using System.Security.Cryptography;
using System.Text.Json;
using RightMenuCheck.Distribution;
using RightMenuCheck.Windows.Diagnostics;

namespace RightMenuCheck.Updater;

public sealed record UpdateInstallResult(
    bool Succeeded,
    bool RolledBack,
    string Message,
    string? ErrorType);

public sealed class UpdateInstaller
{
    private static readonly TimeSpan ParentExitTimeout = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan HealthTimeout = TimeSpan.FromSeconds(45);
    private readonly IUpdateHealthMonitor _healthMonitor;
    private readonly IAppLogger _logger;
    private readonly IUpdateTransactionObserver _observer;
    private readonly IUpdateProcessController _processController;
    private readonly string _publicKeyPem;
    private readonly IUpdateReadySignal _readySignal;
    private readonly IUpdateTargetPolicy _targetPolicy;
    private readonly ISafeZipExtractor _zipExtractor;

    public UpdateInstaller(
        ISafeZipExtractor zipExtractor,
        IUpdateProcessController processController,
        IUpdateHealthMonitor healthMonitor,
        IUpdateTargetPolicy targetPolicy,
        IUpdateReadySignal readySignal,
        string publicKeyPem,
        IAppLogger logger,
        IUpdateTransactionObserver? observer = null)
    {
        _zipExtractor = zipExtractor ?? throw new ArgumentNullException(nameof(zipExtractor));
        _processController = processController ??
                             throw new ArgumentNullException(nameof(processController));
        _healthMonitor = healthMonitor ?? throw new ArgumentNullException(nameof(healthMonitor));
        _targetPolicy = targetPolicy ?? throw new ArgumentNullException(nameof(targetPolicy));
        _readySignal = readySignal ?? throw new ArgumentNullException(nameof(readySignal));
        ArgumentException.ThrowIfNullOrWhiteSpace(publicKeyPem);
        _publicKeyPem = publicKeyPem;
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _observer = observer ?? NullUpdateTransactionObserver.Instance;
    }

    public async Task<UpdateInstallResult> InstallAsync(
        UpdateInstallRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var validated = ValidateRequest(request, _publicKeyPem, _targetPolicy);
        _logger.Log(
            AppLogLevel.Information,
            "update.install_started",
            "Update installation started.",
            new Dictionary<string, object?>
            {
                ["expectedVersion"] = validated.ExpectedVersion.ToString(),
                ["parentProcessId"] = request.ParentProcessId,
                ["targetKey"] = validated.Target.TargetKey,
            });

        UpdateTransactionJournal? transaction = null;
        UpdateTargetLock? targetLock = null;
        var parentExited = false;
        var targetLockAcquired = false;
        try
        {
            targetLock = UpdateTargetLock.Acquire(validated.Target.LockPath);
            targetLockAcquired = true;
            _logger.Log(
                AppLogLevel.Information,
                "update.target_lock_acquired",
                "Update target lock acquired.",
                TargetProperties(validated.Target.TargetKey));

            var recovery = RecoverPendingTransaction(validated);
            if (recovery is PendingRecoveryOutcome.PreviousVersionRestored or
                PendingRecoveryOutcome.PreviousSourceRestarted)
            {
                return new UpdateInstallResult(
                    Succeeded: false,
                    RolledBack: recovery == PendingRecoveryOutcome.PreviousVersionRestored,
                    "An interrupted update was recovered; start the update again to install the package.",
                    "InterruptedUpdateRecovered");
            }

            ValidateFreshInputs(validated);
            var paths = CreatePaths(validated, validated.HealthToken);
            using var parentProcess = _processController.OpenVerifiedParent(
                request.ParentProcessId,
                validated.ParentApplicationPath);

            transaction = new UpdateTransactionJournal(
                UpdateTransactionJournal.CurrentSchemaVersion,
                validated.Target.TargetKey,
                validated.Target.InstallDirectory,
                validated.HealthToken,
                paths.StagingDirectory,
                paths.BackupDirectory,
                paths.FailedDirectory,
                validated.ExpectedVersion.ToString(),
                Directory.Exists(validated.Target.InstallDirectory),
                UpdateTransactionPhase.StagingStarted,
                DateTimeOffset.UtcNow);
            transaction = PersistPhase(
                validated,
                transaction,
                UpdateTransactionPhase.StagingStarted);

            await using (var packageStream = new FileStream(
                             validated.PackagePath,
                             FileMode.Open,
                             FileAccess.Read,
                             FileShare.Read,
                             bufferSize: 81920,
                             FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                await VerifyPackageAsync(
                        packageStream,
                        request.Manifest.Payload.Package,
                        cancellationToken)
                    .ConfigureAwait(false);
                await _zipExtractor.ExtractAsync(
                        packageStream,
                        paths.StagingDirectory,
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            ValidatePayload(paths.StagingDirectory, validated.ExpectedVersion);
            transaction = PersistPhase(
                validated,
                transaction,
                UpdateTransactionPhase.StagingPrepared);
            await _readySignal.SignalAsync(
                    request.ReadyPipeName,
                    request.ReadyNonce,
                    cancellationToken)
                .ConfigureAwait(false);
            await parentProcess.WaitForExitAsync(ParentExitTimeout, cancellationToken)
                .ConfigureAwait(false);
            parentExited = true;

            transaction = PersistPhase(
                validated,
                transaction,
                UpdateTransactionPhase.BackupMoveStarted);
            if (transaction.HadExistingInstall)
            {
                Directory.Move(
                    validated.Target.InstallDirectory,
                    paths.BackupDirectory);
            }

            transaction = PersistPhase(
                validated,
                transaction,
                UpdateTransactionPhase.BackupMoved);
            transaction = PersistPhase(
                validated,
                transaction,
                UpdateTransactionPhase.ActivationMoveStarted);
            Directory.Move(paths.StagingDirectory, validated.Target.InstallDirectory);
            transaction = PersistPhase(
                validated,
                transaction,
                UpdateTransactionPhase.NewActive);

            using var healthEndpoint = _healthMonitor.Create(validated.HealthToken);
            transaction = PersistPhase(
                validated,
                transaction,
                UpdateTransactionPhase.HealthCheckStarted);
            var updatedProcess = _processController.Start(
                validated.TargetApplicationPath,
                validated.Target.InstallDirectory,
                [
                    "--update-health-pipe",
                    healthEndpoint.PipeName,
                    "--update-health-token",
                    validated.HealthToken,
                ]);
            var healthy = await _healthMonitor.WaitForHealthyAsync(
                    healthEndpoint,
                    updatedProcess,
                    validated.ExpectedVersion.ToString(),
                    _processController,
                    HealthTimeout,
                    cancellationToken)
                .ConfigureAwait(false);
            if (!healthy)
            {
                await _processController.StopAsync(updatedProcess, cancellationToken)
                    .ConfigureAwait(false);
                RecoverUnconfirmedTransaction(validated, transaction, restartPrevious: true);
                return new UpdateInstallResult(
                    Succeeded: false,
                    RolledBack: transaction.HadExistingInstall,
                    "The updated application did not report healthy startup; the previous version was restored.",
                    "HealthCheckFailed");
            }

            transaction = PersistPhase(
                validated,
                transaction,
                UpdateTransactionPhase.HealthConfirmed);
            CompleteHealthyTransaction(validated, transaction, deletePackage: true);
            return new UpdateInstallResult(
                Succeeded: true,
                RolledBack: false,
                "Update installation completed.",
                ErrorType: null);
        }
        catch (Exception exception) when (IsHandledFailure(exception))
        {
            var committed = false;
            var rolledBack = false;
            var recoveryFailed = false;
            try
            {
                var persisted = targetLockAcquired
                    ? UpdateTransactionJournalStore.Read(validated.Target.JournalPath)
                    : null;
                if (persisted is not null)
                {
                    UpdateTransactionPathPolicy.ValidateJournal(validated.Target, persisted);
                    if (IsCommittedPhase(persisted.Phase))
                    {
                        CompleteHealthyTransaction(
                            validated,
                            persisted,
                            deletePackage: false);
                        committed = true;
                    }
                    else if (IsStagingPhase(persisted.Phase))
                    {
                        AbandonStagingTransaction(validated, persisted);
                    }
                    else
                    {
                        RecoverUnconfirmedTransaction(
                            validated,
                            persisted,
                            restartPrevious: parentExited);
                        rolledBack = persisted.HadExistingInstall;
                    }
                }
            }
            catch (Exception recoveryException) when (IsHandledFailure(recoveryException))
            {
                recoveryFailed = true;
                _logger.Log(
                    AppLogLevel.Error,
                    "update.recovery_failed",
                    "Update recovery could not complete.",
                    new Dictionary<string, object?>
                    {
                        ["phase"] = transaction?.Phase.ToString() ?? "unknown",
                        ["targetKey"] = validated.Target.TargetKey,
                        ["errorType"] = recoveryException.GetType().Name,
                    });
            }

            if (committed)
            {
                return new UpdateInstallResult(
                    Succeeded: true,
                    RolledBack: false,
                    "The update was installed; interrupted cleanup was completed.",
                    ErrorType: null);
            }

            return new UpdateInstallResult(
                Succeeded: false,
                RolledBack: rolledBack && !recoveryFailed,
                exception.Message,
                recoveryFailed ? "RecoveryFailed" : exception.GetType().Name);
        }
        finally
        {
            targetLock?.Dispose();
        }
    }

    private PendingRecoveryOutcome RecoverPendingTransaction(ValidatedInstallRequest request)
    {
        var journal = UpdateTransactionJournalStore.Read(request.Target.JournalPath);
        if (journal is null)
        {
            return PendingRecoveryOutcome.None;
        }

        UpdateTransactionPathPolicy.ValidateJournal(request.Target, journal);
        _logger.Log(
            AppLogLevel.Warning,
            "update.recovery_started",
            "Interrupted update recovery started.",
            PhaseProperties(journal.Phase, request.Target.TargetKey));
        if (IsCommittedPhase(journal.Phase))
        {
            CompleteHealthyTransaction(request, journal, deletePackage: false);
            return PendingRecoveryOutcome.CommittedVersionKept;
        }

        if (IsStagingPhase(journal.Phase))
        {
            AbandonStagingTransaction(request, journal);
            return PendingRecoveryOutcome.StagingRemoved;
        }

        RecoverUnconfirmedTransaction(
            request,
            journal,
            restartPrevious: !HasVerifiedParent(request));
        return journal.HadExistingInstall
            ? PendingRecoveryOutcome.PreviousVersionRestored
            : PendingRecoveryOutcome.PreviousSourceRestarted;
    }

    private bool HasVerifiedParent(ValidatedInstallRequest request)
    {
        try
        {
            using var parent = _processController.OpenVerifiedParent(
                request.ParentProcessId,
                request.ParentApplicationPath);
            return true;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private void AbandonStagingTransaction(
        ValidatedInstallRequest request,
        UpdateTransactionJournal journal)
    {
        var paths = CreatePaths(request, journal.HealthToken);
        if (Directory.Exists(paths.BackupDirectory) || Directory.Exists(paths.FailedDirectory))
        {
            throw new InvalidDataException(
                "Staging recovery found unexpected update transaction directories.");
        }

        journal = PersistPhase(
            request,
            journal,
            UpdateTransactionPhase.StagingCleanupStarted);
        DeleteDerivedDirectory(request.Target, journal.HealthToken, paths.StagingDirectory);
        journal = PersistPhase(
            request,
            journal,
            UpdateTransactionPhase.StagingAbandoned);
        UpdateTransactionJournalStore.Delete(request.Target.JournalPath);
    }

    private void RecoverUnconfirmedTransaction(
        ValidatedInstallRequest request,
        UpdateTransactionJournal journal,
        bool restartPrevious)
    {
        UpdateTransactionPathPolicy.ValidateJournal(request.Target, journal);
        var originalPhase = journal.Phase;
        var paths = CreatePaths(request, journal.HealthToken);
        ValidateRollbackState(request, journal, paths, originalPhase);

        journal = PersistPhase(
            request,
            journal,
            UpdateTransactionPhase.RollbackStarted);
        if (journal.HadExistingInstall)
        {
            if (Directory.Exists(paths.BackupDirectory))
            {
                if (Directory.Exists(request.Target.InstallDirectory))
                {
                    RequireExpectedPayload(
                        request.Target.InstallDirectory,
                        journal.ExpectedVersion,
                        "Update active directory cannot be identified for rollback.");
                    Directory.Move(
                        request.Target.InstallDirectory,
                        paths.FailedDirectory);
                }

                journal = PersistPhase(
                    request,
                    journal,
                    UpdateTransactionPhase.RollbackActiveMoved);
                journal = PersistPhase(
                    request,
                    journal,
                    UpdateTransactionPhase.RollbackRestoreStarted);
                Directory.Move(paths.BackupDirectory, request.Target.InstallDirectory);
                journal = PersistPhase(
                    request,
                    journal,
                    UpdateTransactionPhase.RollbackRestored);
            }
            else
            {
                journal = PersistPhase(
                    request,
                    journal,
                    UpdateTransactionPhase.RollbackRestored);
            }
        }
        else
        {
            if (Directory.Exists(request.Target.InstallDirectory))
            {
                RequireExpectedPayload(
                    request.Target.InstallDirectory,
                    journal.ExpectedVersion,
                    "Migrated update directory cannot be identified for rollback.");
                Directory.Move(
                    request.Target.InstallDirectory,
                    paths.FailedDirectory);
            }

            journal = PersistPhase(
                request,
                journal,
                UpdateTransactionPhase.RollbackActiveMoved);
            journal = PersistPhase(
                request,
                journal,
                UpdateTransactionPhase.RollbackRestored);
        }

        journal = PersistPhase(
            request,
            journal,
            UpdateTransactionPhase.RollbackCleanupStarted);
        DeleteDerivedDirectory(request.Target, journal.HealthToken, paths.StagingDirectory);
        DeleteDerivedDirectory(request.Target, journal.HealthToken, paths.FailedDirectory);
        journal = PersistPhase(
            request,
            journal,
            UpdateTransactionPhase.RolledBack);
        UpdateTransactionJournalStore.Delete(request.Target.JournalPath);

        if (restartPrevious)
        {
            var applicationPath = journal.HadExistingInstall
                ? request.TargetApplicationPath
                : request.ParentApplicationPath;
            if (!File.Exists(applicationPath))
            {
                throw new InvalidDataException("Recovered application executable is missing.");
            }

            _ = _processController.Start(
                applicationPath,
                Path.GetDirectoryName(applicationPath)!,
                ["--update-rollback"]);
        }
    }

    private void CompleteHealthyTransaction(
        ValidatedInstallRequest request,
        UpdateTransactionJournal journal,
        bool deletePackage)
    {
        UpdateTransactionPathPolicy.ValidateJournal(request.Target, journal);
        var paths = CreatePaths(request, journal.HealthToken);
        RequireExpectedPayload(
            request.Target.InstallDirectory,
            journal.ExpectedVersion,
            "Health-confirmed update payload is missing or does not match the journal.");
        if (Directory.Exists(paths.StagingDirectory) || Directory.Exists(paths.FailedDirectory))
        {
            throw new InvalidDataException(
                "Health-confirmed update has unexpected transaction directories.");
        }

        journal = PersistPhase(
            request,
            journal,
            UpdateTransactionPhase.BackupCleanupStarted);
        DeleteDerivedDirectory(request.Target, journal.HealthToken, paths.BackupDirectory);
        journal = PersistPhase(
            request,
            journal,
            UpdateTransactionPhase.BackupCleaned);
        if (deletePackage)
        {
            journal = PersistPhase(
                request,
                journal,
                UpdateTransactionPhase.PackageCleanupStarted);
            TryDeleteFile(request.PackagePath);
            journal = PersistPhase(
                request,
                journal,
                UpdateTransactionPhase.PackageCleaned);
        }

        _ = PersistPhase(request, journal, UpdateTransactionPhase.Completed);
        UpdateTransactionJournalStore.Delete(request.Target.JournalPath);
    }

    private UpdateTransactionJournal PersistPhase(
        ValidatedInstallRequest request,
        UpdateTransactionJournal journal,
        UpdateTransactionPhase phase)
    {
        var updated = journal with
        {
            Phase = phase,
            UpdatedAtUtc = DateTimeOffset.UtcNow,
        };
        UpdateTransactionJournalStore.Write(request.Target.JournalPath, updated);
        _logger.Log(
            AppLogLevel.Information,
            "update.transaction_phase",
            "Update transaction phase persisted.",
            PhaseProperties(phase, request.Target.TargetKey));
        _observer.OnPhasePersisted(phase, request.Target.TargetKey);
        return updated;
    }

    private static ValidatedInstallRequest ValidateRequest(
        UpdateInstallRequest request,
        string publicKeyPem,
        IUpdateTargetPolicy targetPolicy)
    {
        if (request.SchemaVersion != UpdateInstallRequest.CurrentSchemaVersion ||
            request.ParentProcessId <= 0 ||
            !Guid.TryParseExact(request.HealthToken, "N", out _) ||
            !Path.IsPathFullyQualified(request.PackagePath) ||
            !Path.IsPathFullyQualified(request.InstallDirectory) ||
            !Path.IsPathFullyQualified(request.ParentApplicationPath) ||
            string.IsNullOrWhiteSpace(request.ReadyPipeName) ||
            string.IsNullOrWhiteSpace(request.ReadyNonce))
        {
            throw new InvalidDataException("Update installation request is invalid.");
        }

        var manifestDecision = UpdatePolicyEvaluator.Evaluate(
            SemanticVersion.Parse("0.0.0"),
            request.Manifest,
            publicKeyPem,
            DateTimeOffset.UtcNow);
        if (manifestDecision.Kind != UpdateDecisionKind.Required ||
            manifestDecision.TargetVersion is not { } expectedVersion)
        {
            throw new InvalidDataException("Update installation manifest is invalid.");
        }

        var packagePath = Path.GetFullPath(request.PackagePath);
        var parentApplicationPath = Path.GetFullPath(request.ParentApplicationPath);
        if (!Path.GetFileName(parentApplicationPath).Equals(
                UpdateInstallLocations.ApplicationFileName,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Update parent application path is invalid.");
        }

        var installDirectory = targetPolicy.ResolveTarget(
            parentApplicationPath,
            request.InstallDirectory);
        var target = UpdateTransactionPathPolicy.CreateTargetContext(installDirectory);
        Directory.CreateDirectory(target.ParentDirectory);
        return new ValidatedInstallRequest(
            request.ParentProcessId,
            packagePath,
            parentApplicationPath,
            Path.Combine(target.InstallDirectory, UpdateInstallLocations.ApplicationFileName),
            request.HealthToken,
            target,
            expectedVersion,
            request.Manifest.Payload.Package);
    }

    private static void ValidateFreshInputs(ValidatedInstallRequest request)
    {
        if (!File.Exists(request.ParentApplicationPath))
        {
            throw new InvalidDataException("Update parent application path is invalid.");
        }

        if (!File.Exists(request.PackagePath))
        {
            throw new InvalidDataException("Update package does not exist.");
        }

        if (!Path.GetFileName(request.PackagePath).Equals(
                request.Package.AssetName,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "Update package name does not match the signed manifest.");
        }

        var paths = CreatePaths(request, request.HealthToken);
        if (Directory.Exists(paths.StagingDirectory) ||
            Directory.Exists(paths.BackupDirectory) ||
            Directory.Exists(paths.FailedDirectory))
        {
            throw new InvalidDataException("Update working directories already exist.");
        }
    }

    private static InstallPaths CreatePaths(
        ValidatedInstallRequest request,
        string healthToken)
    {
        var working = UpdateTransactionPathPolicy.CreateWorkingPaths(
            request.Target,
            healthToken);
        return new InstallPaths(
            working.StagingDirectory,
            working.BackupDirectory,
            working.FailedDirectory);
    }

    private static async Task VerifyPackageAsync(
        Stream packageStream,
        UpdatePackage package,
        CancellationToken cancellationToken)
    {
        if (packageStream.Length != package.SizeBytes)
        {
            throw new InvalidDataException(
                "Update package size does not match the signed manifest.");
        }

        packageStream.Position = 0;
        var hash = await SHA256.HashDataAsync(packageStream, cancellationToken)
            .ConfigureAwait(false);
        if (!Convert.ToHexString(hash).Equals(
                package.Sha256,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                "Update package hash does not match the signed manifest.");
        }

        packageStream.Position = 0;
    }

    private static void ValidateRollbackState(
        ValidatedInstallRequest request,
        UpdateTransactionJournal journal,
        InstallPaths paths,
        UpdateTransactionPhase phase)
    {
        var installExists = Directory.Exists(request.Target.InstallDirectory);
        var backupExists = Directory.Exists(paths.BackupDirectory);
        var failedExists = Directory.Exists(paths.FailedDirectory);
        if (journal.HadExistingInstall)
        {
            if (backupExists && installExists && failedExists)
            {
                throw new InvalidDataException("Update rollback state is ambiguous.");
            }

            if (backupExists && installExists)
            {
                RequireExpectedPayload(
                    request.Target.InstallDirectory,
                    journal.ExpectedVersion,
                    "Update active directory cannot be identified for rollback.");
            }

            if (!backupExists && !installExists)
            {
                throw new InvalidDataException("Update rollback backup is missing.");
            }

            if (!backupExists && installExists && RequiresUnrestoredBackup(phase))
            {
                throw new InvalidDataException("Update rollback backup is missing.");
            }

            if (failedExists)
            {
                RequireExpectedPayload(
                    paths.FailedDirectory,
                    journal.ExpectedVersion,
                    "Update failed directory cannot be identified for rollback.");
            }
        }
        else
        {
            if (backupExists)
            {
                throw new InvalidDataException(
                    "Migrated update unexpectedly contains a rollback backup.");
            }

            if (installExists)
            {
                if (phase is UpdateTransactionPhase.BackupMoveStarted or
                    UpdateTransactionPhase.BackupMoved)
                {
                    throw new InvalidDataException(
                        "Migrated update target appeared before activation.");
                }

                RequireExpectedPayload(
                    request.Target.InstallDirectory,
                    journal.ExpectedVersion,
                    "Migrated update directory cannot be identified for rollback.");
            }

            if (failedExists)
            {
                RequireExpectedPayload(
                    paths.FailedDirectory,
                    journal.ExpectedVersion,
                    "Migrated failed directory cannot be identified for rollback.");
            }
        }
    }

    private static bool RequiresUnrestoredBackup(UpdateTransactionPhase phase) => phase is
        UpdateTransactionPhase.BackupMoved or
        UpdateTransactionPhase.ActivationMoveStarted or
        UpdateTransactionPhase.NewActive or
        UpdateTransactionPhase.HealthCheckStarted;

    private static void ValidatePayload(
        string directory,
        SemanticVersion expectedVersion) =>
        RequireExpectedPayload(
            directory,
            expectedVersion.ToString(),
            "Update archive is missing required files or has an unexpected version.");

    private static void RequireExpectedPayload(
        string directory,
        string expectedVersion,
        string errorMessage)
    {
        var applicationPath = Path.Combine(
            directory,
            UpdateInstallLocations.ApplicationFileName);
        var buildInfoPath = Path.Combine(directory, "build-info.json");
        if (!File.Exists(applicationPath) || !File.Exists(buildInfoPath))
        {
            throw new InvalidDataException(errorMessage);
        }

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(buildInfoPath));
            if (!document.RootElement.TryGetProperty("version", out var versionElement) ||
                !SemanticVersion.TryParse(versionElement.GetString(), out var packagedVersion) ||
                !SemanticVersion.TryParse(expectedVersion, out var journalVersion) ||
                packagedVersion.CompareTo(journalVersion) != 0)
            {
                throw new InvalidDataException(errorMessage);
            }
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException(errorMessage, exception);
        }
    }

    private static void DeleteDerivedDirectory(
        UpdateTargetContext target,
        string healthToken,
        string directory)
    {
        var derived = UpdateTransactionPathPolicy.CreateWorkingPaths(target, healthToken);
        if (!UpdateTransactionPathPolicy.PathEquals(directory, derived.StagingDirectory) &&
            !UpdateTransactionPathPolicy.PathEquals(directory, derived.BackupDirectory) &&
            !UpdateTransactionPathPolicy.PathEquals(directory, derived.FailedDirectory))
        {
            throw new InvalidDataException(
                "Refusing to delete a directory outside the update transaction.");
        }

        DeleteDirectoryTree(directory);
    }

    private static void DeleteDirectoryTree(string directory)
    {
        if (!Directory.Exists(directory))
        {
            return;
        }

        var root = new DirectoryInfo(directory);
        if ((root.Attributes & FileAttributes.ReparsePoint) != 0)
        {
            root.Delete(recursive: false);
            return;
        }

        foreach (var entry in root.EnumerateFileSystemInfos())
        {
            if ((entry.Attributes & FileAttributes.Directory) != 0)
            {
                if ((entry.Attributes & FileAttributes.ReparsePoint) != 0)
                {
                    Directory.Delete(entry.FullName, recursive: false);
                }
                else
                {
                    DeleteDirectoryTree(entry.FullName);
                }
            }
            else
            {
                if ((entry.Attributes & FileAttributes.ReadOnly) != 0)
                {
                    entry.Attributes &= ~FileAttributes.ReadOnly;
                }

                entry.Delete();
            }
        }

        root.Delete(recursive: false);
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static bool IsStagingPhase(UpdateTransactionPhase phase) => phase is
        UpdateTransactionPhase.StagingStarted or
        UpdateTransactionPhase.StagingPrepared or
        UpdateTransactionPhase.StagingCleanupStarted or
        UpdateTransactionPhase.StagingAbandoned;

    private static bool IsCommittedPhase(UpdateTransactionPhase phase) => phase is
        UpdateTransactionPhase.HealthConfirmed or
        UpdateTransactionPhase.BackupCleanupStarted or
        UpdateTransactionPhase.BackupCleaned or
        UpdateTransactionPhase.PackageCleanupStarted or
        UpdateTransactionPhase.PackageCleaned or
        UpdateTransactionPhase.Completed;

    private static bool IsHandledFailure(Exception exception) => exception is
        ArgumentException or
        InvalidDataException or
        IOException or
        UnauthorizedAccessException or
        TimeoutException or
        InvalidOperationException or
        Win32Exception or
        JsonException;

    private static Dictionary<string, object?> TargetProperties(string targetKey) =>
        new Dictionary<string, object?> { ["targetKey"] = targetKey };

    private static Dictionary<string, object?> PhaseProperties(
        UpdateTransactionPhase phase,
        string targetKey) => new Dictionary<string, object?>
        {
            ["phase"] = phase.ToString(),
            ["targetKey"] = targetKey,
        };

    private enum PendingRecoveryOutcome
    {
        None,
        StagingRemoved,
        PreviousVersionRestored,
        PreviousSourceRestarted,
        CommittedVersionKept,
    }

    private sealed record ValidatedInstallRequest(
        int ParentProcessId,
        string PackagePath,
        string ParentApplicationPath,
        string TargetApplicationPath,
        string HealthToken,
        UpdateTargetContext Target,
        SemanticVersion ExpectedVersion,
        UpdatePackage Package);

    private sealed record InstallPaths(
        string StagingDirectory,
        string BackupDirectory,
        string FailedDirectory);
}
