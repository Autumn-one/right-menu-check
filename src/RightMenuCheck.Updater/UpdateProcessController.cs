using System.ComponentModel;
using System.Diagnostics;

namespace RightMenuCheck.Updater;

public sealed record UpdateProcessHandle(int ProcessId, string ExecutablePath);

public interface IUpdateProcessController
{
    Task WaitForExitAsync(
        int processId,
        string expectedExecutablePath,
        TimeSpan timeout,
        CancellationToken cancellationToken);

    UpdateProcessHandle Start(
        string executablePath,
        string workingDirectory,
        IReadOnlyList<string> arguments);

    bool HasExited(UpdateProcessHandle process);

    Task StopAsync(UpdateProcessHandle process, CancellationToken cancellationToken);
}

public sealed class SystemUpdateProcessController : IUpdateProcessController
{
    public async Task WaitForExitAsync(
        int processId,
        string expectedExecutablePath,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(processId);

        Process process;
        try
        {
            process = Process.GetProcessById(processId);
        }
        catch (ArgumentException)
        {
            return;
        }

        using (process)
        {
            var actualPath = GetProcessPath(process);
            if (!actualPath.Equals(
                    Path.GetFullPath(expectedExecutablePath),
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("Update parent process identity does not match.");
            }

            using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutSource.CancelAfter(timeout);
            try
            {
                await process.WaitForExitAsync(timeoutSource.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new TimeoutException("The application did not exit before the update timeout.");
            }
        }
    }

    public UpdateProcessHandle Start(
        string executablePath,
        string workingDirectory,
        IReadOnlyList<string> arguments)
    {
        var executable = Path.GetFullPath(executablePath);
        var startInfo = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            WorkingDirectory = Path.GetFullPath(workingDirectory),
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        var process = Process.Start(startInfo) ??
                      throw new InvalidOperationException("Updated application did not start.");
        return new UpdateProcessHandle(process.Id, executable);
    }

    public bool HasExited(UpdateProcessHandle process)
    {
        try
        {
            using var running = Process.GetProcessById(process.ProcessId);
            return running.HasExited;
        }
        catch (ArgumentException)
        {
            return true;
        }
    }

    public async Task StopAsync(
        UpdateProcessHandle process,
        CancellationToken cancellationToken)
    {
        Process running;
        try
        {
            running = Process.GetProcessById(process.ProcessId);
        }
        catch (ArgumentException)
        {
            return;
        }

        using (running)
        {
            if (!GetProcessPath(running).Equals(
                    Path.GetFullPath(process.ExecutablePath),
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("Refusing to stop an unexpected process.");
            }

            _ = running.CloseMainWindow();
            using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutSource.CancelAfter(TimeSpan.FromSeconds(5));
            try
            {
                await running.WaitForExitAsync(timeoutSource.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                running.Kill(entireProcessTree: true);
                await running.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private static string GetProcessPath(Process process)
    {
        try
        {
            return Path.GetFullPath(process.MainModule?.FileName ?? string.Empty);
        }
        catch (Win32Exception exception)
        {
            throw new InvalidOperationException("Unable to verify update process identity.", exception);
        }
    }
}
