using System.Diagnostics;
using System.IO.Pipes;
using RightMenuCheck.Probe.Protocol;
using RightMenuCheck.Windows.Elevation;
using RightMenuCheck.Windows.Management;

namespace RightMenuCheck.IntegrationTests.Elevation;

public sealed class ElevatedHelperHostTests
{
    [Fact]
    public async Task HelperRejectsValidRequestBeforeBackupReadWhenTokenIsNotElevated()
    {
        var pipeName = $"RightMenuCheck.Elevated.Test.{Guid.NewGuid():N}";
        var nonce = ProbeRequestValidator.CreateNonce();
        await using var pipe = new NamedPipeServerStream(
            pipeName,
            PipeDirection.InOut,
            maxNumberOfServerInstances: 1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        using var process = StartHelperWithoutElevation(pipeName, nonce);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await pipe.WaitForConnectionAsync(timeout.Token);
        var request = new ElevationRequest(
            ElevationProtocol.CurrentVersion,
            Guid.NewGuid(),
            nonce,
            ElevationOperation.Restore,
            Guid.NewGuid(),
            "C:\\This-Backup-Does-Not-Exist.rmcbak",
            StateMutationPlan: null,
            RegistryRestoreMode.Exact,
            AcceptRestoreConflicts: true);

        await ElevationMessageSerializer.WriteRequestAsync(pipe, request, timeout.Token);
        var response = await ElevationMessageSerializer.ReadResponseAsync(pipe, timeout.Token);

        Assert.Equal(ElevationOutcome.NotElevated, response.Outcome);
        Assert.Equal(process.Id, response.HelperProcessId);
        Assert.Equal("AdministratorTokenRequired", response.ErrorType);
        await process.WaitForExitAsync(timeout.Token);
        Assert.Equal(5, process.ExitCode);
    }

    private static Process StartHelperWithoutElevation(string pipeName, string nonce)
    {
        var helperPath = GetBuiltHelperPath();
        var startInfo = new ProcessStartInfo
        {
            FileName = helperPath,
            WorkingDirectory = Path.GetDirectoryName(helperPath),
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add("--pipe");
        startInfo.ArgumentList.Add(pipeName);
        startInfo.ArgumentList.Add("--nonce");
        startInfo.ArgumentList.Add(nonce);
        return Process.Start(startInfo) ??
               throw new InvalidOperationException("Elevated helper test process did not start.");
    }

    private static string GetBuiltHelperPath()
    {
        var testOutput = new DirectoryInfo(AppContext.BaseDirectory);
        var binRoot = testOutput.Parent?.Parent ??
                      throw new DirectoryNotFoundException("The shared artifacts/bin directory was not found.");
        return Path.Combine(
            binRoot.FullName,
            "RightMenuCheck.Elevated",
            "debug",
            "RightMenuCheck.Elevated.exe");
    }
}
