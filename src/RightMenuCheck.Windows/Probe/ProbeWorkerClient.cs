using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text.Json;
using RightMenuCheck.Probe.Protocol;

namespace RightMenuCheck.Windows.Probe;

public sealed record ProbeInvocation(
    ProbeOperation Operation,
    ProbeTargetKind TargetKind,
    string HandlerClsid,
    string TargetPath,
    string? RegistryClassPath);

public sealed record ProbeWorkerOptions(string WorkerExecutablePath, TimeSpan Timeout);

public interface IProbeWorkerClient
{
    Task<ProbeResponse> RunAsync(
        ProbeInvocation invocation,
        ProbeWorkerOptions options,
        CancellationToken cancellationToken = default);
}

public sealed class ProbeWorkerClient : IProbeWorkerClient
{
    private static readonly TimeSpan WorkerExitGracePeriod = TimeSpan.FromSeconds(2);

    public async Task<ProbeResponse> RunAsync(
        ProbeInvocation invocation,
        ProbeWorkerOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(invocation);
        ArgumentNullException.ThrowIfNull(options);

        var workerPath = ValidateAndNormalizeOptions(invocation, options);
        var nonce = ProbeRequestValidator.CreateNonce();
        var request = new ProbeRequest(
            ProbeProtocol.CurrentVersion,
            Guid.NewGuid(),
            nonce,
            invocation.Operation,
            invocation.TargetKind,
            invocation.HandlerClsid,
            Path.GetFullPath(invocation.TargetPath),
            invocation.RegistryClassPath);
        var validation = ProbeRequestValidator.Validate(request, nonce);
        if (!validation.IsValid)
        {
            throw new ArgumentException(validation.Error, nameof(invocation));
        }

        var pipeName = $"RightMenuCheck.Probe.{Guid.NewGuid():N}";
        await using var pipe = new NamedPipeServerStream(
            pipeName,
            PipeDirection.InOut,
            maxNumberOfServerInstances: 1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly,
            inBufferSize: 4096,
            outBufferSize: 4096);
        using var job = JobObject.CreateKillOnClose();
        using var process = CreateWorkerProcess(workerPath, pipeName, nonce);
        var startedAt = DateTimeOffset.UtcNow;
        var stopwatch = Stopwatch.StartNew();

        try
        {
            if (!process.Start())
            {
                return CreateFailure(
                    request,
                    ProbeOutcome.Crashed,
                    startedAt,
                    stopwatch.Elapsed,
                    processId: 0,
                    "ProcessStartFailed",
                    "The probe worker process could not be started.");
            }

            var standardErrorTask = process.StandardError.ReadToEndAsync(CancellationToken.None);
            _ = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
            try
            {
                job.Assign(process);
            }
            catch (System.ComponentModel.Win32Exception exception)
            {
                TryKillProcess(process);
                return CreateFailure(
                    request,
                    ProbeOutcome.Crashed,
                    startedAt,
                    stopwatch.Elapsed,
                    process.Id,
                    "JobAssignmentFailed",
                    exception.Message,
                    exception.NativeErrorCode);
            }

            using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutSource.CancelAfter(options.Timeout);

            try
            {
                var connectionTask = pipe.WaitForConnectionAsync(timeoutSource.Token);
                var exitTask = process.WaitForExitAsync(timeoutSource.Token);
                var firstTask = await Task.WhenAny(connectionTask, exitTask).ConfigureAwait(false);
                if (firstTask == exitTask && !pipe.IsConnected)
                {
                    await exitTask.ConfigureAwait(false);
                    var errorOutput = await ReadBoundedOutputAsync(standardErrorTask).ConfigureAwait(false);
                    return CreateFailure(
                        request,
                        ProbeOutcome.Crashed,
                        startedAt,
                        stopwatch.Elapsed,
                        process.Id,
                        "WorkerExitedBeforeHandshake",
                        FormatExitMessage(process.ExitCode, errorOutput));
                }

                await connectionTask.ConfigureAwait(false);
                await ProbeMessageSerializer
                    .WriteRequestAsync(pipe, request, timeoutSource.Token)
                    .ConfigureAwait(false);
                var response = await ProbeMessageSerializer
                    .ReadResponseAsync(pipe, timeoutSource.Token)
                    .ConfigureAwait(false);

                if (!IsExpectedResponse(response, request, process.Id))
                {
                    return CreateFailure(
                        request,
                        ProbeOutcome.ProtocolError,
                        startedAt,
                        stopwatch.Elapsed,
                        process.Id,
                        "ResponseIdentityMismatch",
                        "The worker response version, request identity, process identity, or nonce did not match.");
                }

                await WaitForWorkerExitAsync(process).ConfigureAwait(false);
                return response;
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return CreateFailure(
                    request,
                    ProbeOutcome.TimedOut,
                    startedAt,
                    stopwatch.Elapsed,
                    process.Id,
                    "ProbeTimeout",
                    $"The isolated probe exceeded the {options.Timeout.TotalMilliseconds:F0} ms timeout.");
            }
            catch (EndOfStreamException exception)
            {
                return CreateProtocolFailure(exception);
            }
            catch (InvalidDataException exception)
            {
                return CreateProtocolFailure(exception);
            }
            catch (JsonException exception)
            {
                return CreateProtocolFailure(exception);
            }
            catch (IOException exception)
            {
                return CreateProtocolFailure(exception);
            }

            ProbeResponse CreateProtocolFailure(Exception exception) => CreateFailure(
                request,
                ProbeOutcome.ProtocolError,
                startedAt,
                stopwatch.Elapsed,
                process.Id,
                exception.GetType().Name,
                exception.Message,
                Marshal.GetHRForException(exception));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (System.ComponentModel.Win32Exception exception)
        {
            return CreateFailure(
                request,
                ProbeOutcome.Crashed,
                startedAt,
                stopwatch.Elapsed,
                processId: 0,
                exception.GetType().Name,
                exception.Message,
                exception.NativeErrorCode);
        }
    }

    private static string ValidateAndNormalizeOptions(
        ProbeInvocation invocation,
        ProbeWorkerOptions options)
    {
        if (options.Timeout < TimeSpan.FromMilliseconds(100) ||
            options.Timeout > TimeSpan.FromMinutes(2))
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                options.Timeout,
                "Probe timeout must be between 100 ms and 2 minutes.");
        }

        if (string.IsNullOrWhiteSpace(options.WorkerExecutablePath))
        {
            throw new ArgumentException("Worker executable path is required.", nameof(options));
        }

        var workerPath = Path.GetFullPath(options.WorkerExecutablePath);
        if (!File.Exists(workerPath))
        {
            throw new FileNotFoundException("Probe worker executable was not found.", workerPath);
        }

        if (string.IsNullOrWhiteSpace(invocation.TargetPath) ||
            (!File.Exists(invocation.TargetPath) && !Directory.Exists(invocation.TargetPath)))
        {
            throw new FileNotFoundException("Probe target does not exist.", invocation.TargetPath);
        }

        return workerPath;
    }

    private static Process CreateWorkerProcess(string workerPath, string pipeName, string nonce)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = workerPath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            WorkingDirectory = Path.GetDirectoryName(workerPath) ?? AppContext.BaseDirectory,
        };
        startInfo.ArgumentList.Add("--pipe");
        startInfo.ArgumentList.Add(pipeName);
        startInfo.ArgumentList.Add("--nonce");
        startInfo.ArgumentList.Add(nonce);
        return new Process { StartInfo = startInfo, EnableRaisingEvents = true };
    }

    private static bool IsExpectedResponse(
        ProbeResponse response,
        ProbeRequest request,
        int processId) =>
        response.ProtocolVersion == ProbeProtocol.CurrentVersion &&
        response.RequestId == request.RequestId &&
        response.Nonce.Equals(request.Nonce, StringComparison.Ordinal) &&
        response.WorkerProcessId == processId;

    private static async Task WaitForWorkerExitAsync(Process process)
    {
        using var exitSource = new CancellationTokenSource(WorkerExitGracePeriod);
        try
        {
            await process.WaitForExitAsync(exitSource.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Closing the job object after the response reclaims a worker that failed to exit.
        }
    }

    private static async Task<string> ReadBoundedOutputAsync(Task<string> outputTask)
    {
        var output = await outputTask.ConfigureAwait(false);
        const int maximumCharacters = 2048;
        return output.Length <= maximumCharacters ? output : output[..maximumCharacters];
    }

    private static string FormatExitMessage(int exitCode, string standardError) =>
        string.IsNullOrWhiteSpace(standardError)
            ? $"The worker exited with code {exitCode}."
            : $"The worker exited with code {exitCode}: {standardError.Trim()}";

    private static void TryKillProcess(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(milliseconds: 2000);
            }
        }
        catch (InvalidOperationException)
        {
        }
        catch (System.ComponentModel.Win32Exception)
        {
        }
    }

    private static ProbeResponse CreateFailure(
        ProbeRequest request,
        ProbeOutcome outcome,
        DateTimeOffset startedAt,
        TimeSpan duration,
        int processId,
        string errorType,
        string errorMessage,
        int? hResult = null) =>
        new(
            ProbeProtocol.CurrentVersion,
            request.RequestId,
            request.Nonce,
            outcome,
            processId,
            WorkerArchitecture: string.Empty,
            startedAt,
            duration.TotalMilliseconds,
            Phases: [],
            new ProbeError(errorType, errorMessage, hResult));
}
