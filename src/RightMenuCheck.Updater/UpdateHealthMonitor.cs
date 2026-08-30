using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32.SafeHandles;
using RightMenuCheck.Distribution;

namespace RightMenuCheck.Updater;

public sealed record UpdateHealthEndpoint(
    string PipeName,
    string Token,
    NamedPipeServerStream Server) : IDisposable
{
    public void Dispose() => Server.Dispose();
}

public interface IUpdateHealthMonitor
{
    UpdateHealthEndpoint Create(string healthToken);

    Task<bool> WaitForHealthyAsync(
        UpdateHealthEndpoint endpoint,
        UpdateProcessHandle process,
        string expectedVersion,
        IUpdateProcessController processController,
        TimeSpan timeout,
        CancellationToken cancellationToken);
}

public sealed partial class NamedPipeUpdateHealthMonitor : IUpdateHealthMonitor
{
    public UpdateHealthEndpoint Create(string healthToken)
    {
        if (!Guid.TryParseExact(healthToken, "N", out _))
        {
            throw new ArgumentException("Update health token is invalid.", nameof(healthToken));
        }

        var pipeName = $"RightMenuCheck.Update.Health.{Guid.NewGuid():N}";
        var server = new NamedPipeServerStream(
            pipeName,
            PipeDirection.In,
            maxNumberOfServerInstances: 1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        return new UpdateHealthEndpoint(pipeName, healthToken, server);
    }

    public async Task<bool> WaitForHealthyAsync(
        UpdateHealthEndpoint endpoint,
        UpdateProcessHandle process,
        string expectedVersion,
        IUpdateProcessController processController,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedVersion);
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);
        var connection = endpoint.Server.WaitForConnectionAsync(timeoutSource.Token);
        var processExit = WaitForProcessExitAsync(
            process,
            processController,
            timeoutSource.Token);
        try
        {
            if (await Task.WhenAny(connection, processExit).ConfigureAwait(false) == processExit)
            {
                return false;
            }

            await connection.ConfigureAwait(false);
            if (!GetNamedPipeClientProcessId(endpoint.Server.SafePipeHandle, out var clientProcessId) ||
                clientProcessId != process.ProcessId)
            {
                return false;
            }

            using var reader = new StreamReader(
                endpoint.Server,
                Encoding.UTF8,
                detectEncodingFromByteOrderMarks: false,
                bufferSize: 1024,
                leaveOpen: true);
            var json = await reader.ReadLineAsync(timeoutSource.Token).ConfigureAwait(false);
            if (json is null || json.Length > 1024)
            {
                return false;
            }

            var report = DistributionJson.Deserialize<UpdateHealthReport>(json);
            return report.ProcessId == process.ProcessId &&
                   VersionsMatch(report.Version, expectedVersion) &&
                   CryptographicOperations.FixedTimeEquals(
                       Encoding.UTF8.GetBytes(report.Token),
                       Encoding.UTF8.GetBytes(endpoint.Token));
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return false;
        }
        catch (InvalidDataException)
        {
            return false;
        }
        finally
        {
            timeoutSource.Cancel();
            try
            {
                await processExit.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }
    }

    private static async Task WaitForProcessExitAsync(
        UpdateProcessHandle process,
        IUpdateProcessController processController,
        CancellationToken cancellationToken)
    {
        while (!processController.HasExited(process))
        {
            await Task.Delay(TimeSpan.FromMilliseconds(100), cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private static bool VersionsMatch(string reportedVersion, string expectedVersion) =>
        SemanticVersion.TryParse(reportedVersion, out var reported) &&
        SemanticVersion.TryParse(expectedVersion, out var expected) &&
        reported.CompareTo(expected) == 0;

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetNamedPipeClientProcessId(
        SafePipeHandle pipe,
        out int clientProcessId);
}
