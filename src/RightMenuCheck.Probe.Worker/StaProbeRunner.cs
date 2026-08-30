using System.Runtime.InteropServices;
using RightMenuCheck.Probe.Protocol;

namespace RightMenuCheck.Probe.Worker;

internal static class StaProbeRunner
{
    public static Task<ProbeResponse> RunAsync(ProbeRequest request)
    {
        var completion = new TaskCompletionSource<ProbeResponse>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() => RunOnStaThread(request, completion))
        {
            IsBackground = false,
            Name = "RightMenuCheck Shell Probe STA",
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task;
    }

    private static void RunOnStaThread(
        ProbeRequest request,
        TaskCompletionSource<ProbeResponse> completion)
    {
        var initializeResult = ShellInterop.CoInitializeEx(
            IntPtr.Zero,
            ShellInterop.CoInitApartmentThreaded);
        if (initializeResult < 0)
        {
            completion.SetResult(new ProbeResponse(
                ProbeProtocol.CurrentVersion,
                request.RequestId,
                request.Nonce,
                ProbeOutcome.InitializationFailed,
                Environment.ProcessId,
                RuntimeInformation.ProcessArchitecture.ToString(),
                DateTimeOffset.UtcNow,
                TotalDurationMilliseconds: 0,
                Phases: [],
                new ProbeError(
                    "CoInitializeEx",
                    Marshal.GetExceptionForHR(initializeResult)?.Message ??
                    "COM apartment initialization failed.",
                    initializeResult)));
            return;
        }

        try
        {
            completion.SetResult(ShellProbeExecutor.Execute(request));
        }
        catch (ExternalException exception)
        {
            completion.SetException(exception);
        }
        finally
        {
            ShellInterop.CoUninitialize();
        }
    }
}
