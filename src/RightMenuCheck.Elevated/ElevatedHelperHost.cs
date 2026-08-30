using System.IO.Pipes;
using System.Security.Principal;
using RightMenuCheck.Core.Backup;
using RightMenuCheck.Windows.Backup;
using RightMenuCheck.Windows.Elevation;
using RightMenuCheck.Windows.Management;
using RightMenuCheck.Windows.Registry;

namespace RightMenuCheck.Elevated;

internal static class ElevatedHelperHost
{
    private static readonly TimeSpan ConnectionTimeout = TimeSpan.FromSeconds(30);

    public static async Task<int> RunAsync(string[] args, CancellationToken cancellationToken)
    {
        if (!ElevatedArguments.TryParse(args, out var arguments) || arguments is null)
        {
            return 2;
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(ConnectionTimeout);
        await using var pipe = new NamedPipeClientStream(
            ".",
            arguments.PipeName,
            PipeDirection.InOut,
            PipeOptions.Asynchronous);
        ElevationRequest? activeRequest = null;

        try
        {
            await pipe.ConnectAsync(timeout.Token).ConfigureAwait(false);
            var request = await ElevationMessageSerializer
                .ReadRequestAsync(pipe, timeout.Token)
                .ConfigureAwait(false);
            activeRequest = request;
            var validation = ElevationRequestValidator.ValidateEnvelope(request, arguments.Nonce);
            if (!validation.IsValid)
            {
                if (validation.Error?.Equals(
                        "Nonce validation failed.",
                        StringComparison.Ordinal) == true)
                {
                    return 3;
                }

                await WriteResponseAsync(
                        pipe,
                        request,
                        arguments.Nonce,
                        ElevationOutcome.InvalidRequest,
                        mutationResult: null,
                        "RequestValidation",
                        validation.Error,
                        timeout.Token)
                    .ConfigureAwait(false);
                return 4;
            }

            if (!IsAdministrator())
            {
                await WriteResponseAsync(
                        pipe,
                        request,
                        arguments.Nonce,
                        ElevationOutcome.NotElevated,
                        mutationResult: null,
                        "AdministratorTokenRequired",
                        "The helper process is not running with an administrator token.",
                        timeout.Token)
                    .ConfigureAwait(false);
                return 5;
            }

            timeout.CancelAfter(Timeout.InfiniteTimeSpan);
            var backupReader = new RightMenuBackupReader();
            var manifest = await backupReader
                .ReadAsync(request.BackupPath, CancellationToken.None)
                .ConfigureAwait(false);
            if (manifest.BackupId != request.ExpectedBackupId || !manifest.IsComplete)
            {
                await WriteResponseAsync(
                        pipe,
                        request,
                        arguments.Nonce,
                        ElevationOutcome.InvalidRequest,
                        mutationResult: null,
                        "BackupIdentityMismatch",
                        "Backup identity does not match the request or the backup is incomplete.",
                        CancellationToken.None)
                    .ConfigureAwait(false);
                return 6;
            }

            RegistryMutationResult mutationResult;
            if (request.Operation == ElevationOperation.StateChange)
            {
                var mutationPlan = request.StateMutationPlan!;
                if (!StateTargetsBelongToBackup(mutationPlan, manifest))
                {
                    await WriteResponseAsync(
                            pipe,
                            request,
                            arguments.Nonce,
                            ElevationOutcome.InvalidRequest,
                            mutationResult: null,
                            "MutationBackupMismatch",
                            "State-change target is not a registration root in the verified backup.",
                            CancellationToken.None)
                        .ConfigureAwait(false);
                    return 7;
                }

                mutationResult = await CreateTransactionExecutor()
                    .ExecuteAsync(mutationPlan, CancellationToken.None)
                    .ConfigureAwait(false);
            }
            else if (request.Operation == ElevationOperation.Restore)
            {
                var registryReader = new SystemRegistryReader();
                var securityReader = new SystemRegistrySecurityDescriptorReader();
                var snapshotReader = new RegistrySnapshotReader(registryReader, securityReader);
                var transaction = CreateTransactionExecutor(
                    registryReader,
                    snapshotReader);
                var preflight = new RestorePreflightService(
                    backupReader,
                    registryReader,
                    securityReader);
                var restore = new RegistryRestoreService(backupReader, preflight, transaction);
                var restorePlan = await restore.CreatePlanAsync(
                        request.BackupPath,
                        request.RestoreMode!.Value,
                        CancellationToken.None)
                    .ConfigureAwait(false);
                mutationResult = await restore.ExecuteAsync(
                        restorePlan,
                        request.AcceptRestoreConflicts,
                        CancellationToken.None)
                    .ConfigureAwait(false);
            }
            else
            {
                var registration = manifest.Registrations.SingleOrDefault(item => item.Id.Equals(
                    request.RemovalRegistrationId,
                    StringComparison.Ordinal));
                if (registration?.RegistrySource is not { } removalSource ||
                    removalSource.Hive != RightMenuCheck.Core.Inventory.RegistryHiveKind.LocalMachine ||
                    registration.Owner?.IsSystemProtected == true)
                {
                    await WriteResponseAsync(
                            pipe,
                            request,
                            arguments.Nonce,
                            ElevationOutcome.InvalidRequest,
                            mutationResult: null,
                            "RemovalBackupMismatch",
                            "Removal target is missing, not machine-scoped, or system protected.",
                            CancellationToken.None)
                        .ConfigureAwait(false);
                    return 11;
                }

                var removalPlan = new RegistryMutationPlan(
                    Guid.NewGuid(),
                    "Remove context menu registration",
                    request.BackupPath,
                    [new RegistryMutation(
                        RegistryMutationKind.DeleteKeyTree,
                        removalSource,
                        ValueName: null,
                        Value: null,
                        KeyTree: null)]);
                mutationResult = await CreateTransactionExecutor()
                    .ExecuteAsync(removalPlan, CancellationToken.None)
                    .ConfigureAwait(false);
            }

            await WriteResponseAsync(
                    pipe,
                    request,
                    arguments.Nonce,
                    mutationResult.Succeeded
                        ? ElevationOutcome.Succeeded
                        : ElevationOutcome.Failed,
                    mutationResult,
                    mutationResult.ErrorType,
                    mutationResult.ErrorMessage,
                    CancellationToken.None)
                .ConfigureAwait(false);
            return mutationResult.Succeeded ? 0 : 8;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return 9;
        }
#pragma warning disable CA1031
        catch (Exception exception)
#pragma warning restore CA1031
        {
            if (activeRequest is not null && pipe.IsConnected)
            {
                try
                {
                    await WriteResponseAsync(
                            pipe,
                            activeRequest,
                            arguments.Nonce,
                            ElevationOutcome.Failed,
                            mutationResult: null,
                            exception.GetType().Name,
                            exception.Message,
                            CancellationToken.None)
                        .ConfigureAwait(false);
                }
#pragma warning disable CA1031
                catch (Exception)
#pragma warning restore CA1031
                {
                }
            }

            return 10;
        }
    }

    private static RegistryTransactionExecutor CreateTransactionExecutor()
    {
        var registryReader = new SystemRegistryReader();
        var securityReader = new SystemRegistrySecurityDescriptorReader();
        var snapshotReader = new RegistrySnapshotReader(registryReader, securityReader);
        return CreateTransactionExecutor(registryReader, snapshotReader);
    }

    private static RegistryTransactionExecutor CreateTransactionExecutor(
        SystemRegistryReader registryReader,
        RegistrySnapshotReader snapshotReader)
    {
        var journalDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "RightMenuCheck",
            "Journals");
        return new RegistryTransactionExecutor(
            registryReader,
            snapshotReader,
            new SystemRegistryWriter(),
            new FileRegistryActionJournalStore(journalDirectory));
    }

    private static bool StateTargetsBelongToBackup(
        RegistryMutationPlan plan,
        RightMenuBackupManifest manifest)
    {
        var roots = manifest.Registrations
            .Where(static registration => registration.RegistrySource is not null)
            .Select(static registration => registration.RegistrySource!.Value)
            .ToArray();
        return plan.Mutations.All(mutation => roots.Any(root =>
            root.Hive == mutation.Source.Hive &&
            root.View == mutation.Source.View &&
            root.KeyPath.Equals(mutation.Source.KeyPath, StringComparison.OrdinalIgnoreCase)));
    }

    private static bool IsAdministrator()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(
            WindowsBuiltInRole.Administrator);
    }

    private static ValueTask WriteResponseAsync(
        Stream pipe,
        ElevationRequest request,
        string nonce,
        ElevationOutcome outcome,
        RegistryMutationResult? mutationResult,
        string? errorType,
        string? errorMessage,
        CancellationToken cancellationToken) =>
        ElevationMessageSerializer.WriteResponseAsync(
            pipe,
            new ElevationResponse(
                ElevationProtocol.CurrentVersion,
                request.RequestId,
                nonce,
                outcome,
                Environment.ProcessId,
                mutationResult,
                errorType,
                errorMessage),
            cancellationToken);
}
