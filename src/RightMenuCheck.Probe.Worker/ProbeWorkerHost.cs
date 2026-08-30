using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using RightMenuCheck.Probe.Protocol;

namespace RightMenuCheck.Probe.Worker;

internal static class ProbeWorkerHost
{
    private static readonly TimeSpan ConnectionTimeout = TimeSpan.FromSeconds(15);

    public static async Task<int> RunAsync(string[] args, CancellationToken cancellationToken)
    {
        if (!WorkerArguments.TryParse(args, out var arguments) || arguments is null)
        {
            return 2;
        }

        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(ConnectionTimeout);
        await using var pipe = new NamedPipeClientStream(
            ".",
            arguments.PipeName,
            PipeDirection.InOut,
            PipeOptions.Asynchronous);

        try
        {
            await pipe.ConnectAsync(timeoutSource.Token).ConfigureAwait(false);
            var request = await ProbeMessageSerializer
                .ReadRequestAsync(pipe, timeoutSource.Token)
                .ConfigureAwait(false);
            var validation = ProbeRequestValidator.Validate(request, arguments.Nonce);
            if (!validation.IsValid)
            {
                if (validation.Error?.Equals(
                        "Nonce validation failed.",
                        StringComparison.Ordinal) == true)
                {
                    return 3;
                }

                await WriteInvalidRequestAsync(pipe, request, arguments.Nonce, validation.Error)
                    .ConfigureAwait(false);
                return 4;
            }

            timeoutSource.CancelAfter(Timeout.InfiniteTimeSpan);
            var response = await StaProbeRunner.RunAsync(request).ConfigureAwait(false);
            await ProbeMessageSerializer
                .WriteResponseAsync(pipe, response, timeoutSource.Token)
                .ConfigureAwait(false);
            return 0;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return 5;
        }
        catch (EndOfStreamException)
        {
            return 6;
        }
        catch (InvalidDataException)
        {
            return 7;
        }
        catch (IOException)
        {
            return 8;
        }
    }

    private static ValueTask WriteInvalidRequestAsync(
        Stream stream,
        ProbeRequest request,
        string nonce,
        string? validationError)
    {
        var response = new ProbeResponse(
            ProbeProtocol.CurrentVersion,
            request.RequestId,
            nonce,
            ProbeOutcome.InvalidRequest,
            Environment.ProcessId,
            RuntimeInformation.ProcessArchitecture.ToString(),
            DateTimeOffset.UtcNow,
            TotalDurationMilliseconds: 0,
            Phases: [],
            new ProbeError(
                "RequestValidation",
                validationError ?? "The request is invalid.",
                HResult: null));
        return ProbeMessageSerializer.WriteResponseAsync(stream, response);
    }
}
