using System.ComponentModel;
using System.Diagnostics;
using System.IO.Pipes;
using RightMenuCheck.Probe.Protocol;
using RightMenuCheck.Windows.Management;

namespace RightMenuCheck.Windows.Elevation;

public sealed record ElevatedHelperOptions(
    string HelperExecutablePath,
    TimeSpan Timeout);

public interface IElevatedHelperClient
{
    Task<ElevationResponse> RunStateChangeAsync(
        PreparedContextMenuStateAction prepared,
        ElevatedHelperOptions options,
        CancellationToken cancellationToken = default);

    Task<ElevationResponse> RunRestoreAsync(
        RegistryRestorePlan restorePlan,
        bool acceptConflicts,
        ElevatedHelperOptions options,
        CancellationToken cancellationToken = default);

    Task<ElevationResponse> RunRemovalAsync(
        PreparedContextMenuRemoval prepared,
        ElevatedHelperOptions options,
        CancellationToken cancellationToken = default);
}

public sealed class ElevatedHelperClient : IElevatedHelperClient
{
    public Task<ElevationResponse> RunStateChangeAsync(
        PreparedContextMenuStateAction prepared,
        ElevatedHelperOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(prepared);
        if (!prepared.Plan.RequiresElevation || prepared.Backup is null ||
            prepared.MutationPlan is null)
        {
            throw new InvalidOperationException(
                "Prepared state action does not contain an elevated mutation and verified backup.");
        }

        return RunAsync(
            ElevationOperation.StateChange,
            prepared.Backup.BackupId,
            prepared.Backup.Path,
            prepared.MutationPlan,
            restoreMode: null,
            acceptRestoreConflicts: false,
            removalRegistrationId: null,
            options,
            cancellationToken);
    }

    public Task<ElevationResponse> RunRestoreAsync(
        RegistryRestorePlan restorePlan,
        bool acceptConflicts,
        ElevatedHelperOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(restorePlan);
        if (!restorePlan.CanExecute)
        {
            throw new InvalidOperationException(restorePlan.BlockReason);
        }

        return RunAsync(
            ElevationOperation.Restore,
            restorePlan.Manifest.BackupId,
            restorePlan.BackupPath,
            stateMutationPlan: null,
            restorePlan.Mode,
            acceptConflicts,
            removalRegistrationId: null,
            options,
            cancellationToken);
    }

    public Task<ElevationResponse> RunRemovalAsync(
        PreparedContextMenuRemoval prepared,
        ElevatedHelperOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(prepared);
        if (!prepared.Plan.RequiresElevation || prepared.Backup is null ||
            prepared.MutationPlan is null)
        {
            throw new InvalidOperationException(
                "Prepared removal does not contain an elevated mutation and verified backup.");
        }

        return RunAsync(
            ElevationOperation.RemoveRegistration,
            prepared.Backup.BackupId,
            prepared.Backup.Path,
            stateMutationPlan: null,
            restoreMode: null,
            acceptRestoreConflicts: false,
            prepared.RegistrationId,
            options,
            cancellationToken);
    }

    private static async Task<ElevationResponse> RunAsync(
        ElevationOperation operation,
        Guid backupId,
        string backupPath,
        RegistryMutationPlan? stateMutationPlan,
        RegistryRestoreMode? restoreMode,
        bool acceptRestoreConflicts,
        string? removalRegistrationId,
        ElevatedHelperOptions options,
        CancellationToken cancellationToken)
    {
        ValidateOptions(options);
        var nonce = ProbeRequestValidator.CreateNonce();
        var request = new ElevationRequest(
            ElevationProtocol.CurrentVersion,
            Guid.NewGuid(),
            nonce,
            operation,
            backupId,
            Path.GetFullPath(backupPath),
            stateMutationPlan,
            restoreMode,
            acceptRestoreConflicts,
            removalRegistrationId);
        var validation = ElevationRequestValidator.ValidateEnvelope(request, nonce);
        if (!validation.IsValid)
        {
            throw new InvalidOperationException(validation.Error);
        }

        var helperPath = Path.GetFullPath(options.HelperExecutablePath);
        var pipeName = $"RightMenuCheck.Elevated.{Guid.NewGuid():N}";
        await using var pipe = new NamedPipeServerStream(
            pipeName,
            PipeDirection.InOut,
            maxNumberOfServerInstances: 1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly,
            inBufferSize: 81920,
            outBufferSize: 81920);
        using var process = CreateProcess(helperPath, pipeName, nonce);

        try
        {
            if (!process.Start())
            {
                return CreateFailure(
                    request,
                    ElevationOutcome.Failed,
                    processId: 0,
                    "ProcessStartFailed",
                    "The elevated helper process could not be started.");
            }
        }
        catch (Win32Exception exception) when (exception.NativeErrorCode == 1223)
        {
            return CreateFailure(
                request,
                ElevationOutcome.Cancelled,
                processId: 0,
                "UacCancelled",
                "The administrator consent prompt was cancelled.");
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(options.Timeout);
        try
        {
            await pipe.WaitForConnectionAsync(timeout.Token).ConfigureAwait(false);
            await ElevationMessageSerializer
                .WriteRequestAsync(pipe, request, timeout.Token)
                .ConfigureAwait(false);
            var response = await ElevationMessageSerializer
                .ReadResponseAsync(pipe, timeout.Token)
                .ConfigureAwait(false);
            if (response.ProtocolVersion != ElevationProtocol.CurrentVersion ||
                response.RequestId != request.RequestId ||
                !response.Nonce.Equals(nonce, StringComparison.Ordinal) ||
                response.HelperProcessId != process.Id)
            {
                return CreateFailure(
                    request,
                    ElevationOutcome.InvalidRequest,
                    process.Id,
                    "ResponseIdentityMismatch",
                    "The elevated helper response identity did not match the request.");
            }

            return response;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return CreateFailure(
                request,
                ElevationOutcome.TimedOut,
                process.Id,
                "ElevationTimeout",
                "The elevated helper did not complete within the configured timeout.");
        }
        catch (EndOfStreamException exception)
        {
            return CreateFailure(
                request,
                ElevationOutcome.Failed,
                process.Id,
                exception.GetType().Name,
                exception.Message);
        }
        catch (InvalidDataException exception)
        {
            return CreateFailure(
                request,
                ElevationOutcome.Failed,
                process.Id,
                exception.GetType().Name,
                exception.Message);
        }
        catch (IOException exception)
        {
            return CreateFailure(
                request,
                ElevationOutcome.Failed,
                process.Id,
                exception.GetType().Name,
                exception.Message);
        }
    }

    private static Process CreateProcess(string helperPath, string pipeName, string nonce)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = helperPath,
            WorkingDirectory = Path.GetDirectoryName(helperPath) ?? AppContext.BaseDirectory,
            UseShellExecute = true,
            Verb = "runas",
            WindowStyle = ProcessWindowStyle.Hidden,
        };
        startInfo.ArgumentList.Add("--pipe");
        startInfo.ArgumentList.Add(pipeName);
        startInfo.ArgumentList.Add("--nonce");
        startInfo.ArgumentList.Add(nonce);
        return new Process { StartInfo = startInfo };
    }

    private static void ValidateOptions(ElevatedHelperOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.Timeout < TimeSpan.FromSeconds(5) ||
            options.Timeout > TimeSpan.FromMinutes(5))
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                options.Timeout,
                "Elevation timeout must be between 5 seconds and 5 minutes.");
        }

        if (string.IsNullOrWhiteSpace(options.HelperExecutablePath) ||
            !File.Exists(Path.GetFullPath(options.HelperExecutablePath)))
        {
            throw new FileNotFoundException(
                "Elevated helper executable was not found.",
                options.HelperExecutablePath);
        }
    }

    private static ElevationResponse CreateFailure(
        ElevationRequest request,
        ElevationOutcome outcome,
        int processId,
        string errorType,
        string errorMessage) =>
        new(
            ElevationProtocol.CurrentVersion,
            request.RequestId,
            request.Nonce,
            outcome,
            processId,
            MutationResult: null,
            errorType,
            errorMessage);
}
