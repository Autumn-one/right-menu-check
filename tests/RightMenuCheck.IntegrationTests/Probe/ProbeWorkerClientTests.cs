using RightMenuCheck.Probe.Protocol;
using RightMenuCheck.Windows.Probe;
using System.Diagnostics;

namespace RightMenuCheck.IntegrationTests.Probe;

public sealed class ProbeWorkerClientTests
{
    [Fact]
    public async Task RunCompletesAuthenticatedCurrentUserPipeHandshake()
    {
        var client = new ProbeWorkerClient();
        var invocation = new ProbeInvocation(
            ProbeOperation.ClassicContextMenu,
            ProbeTargetKind.File,
            "{11111111-2222-3333-4444-555555555555}",
            Path.Combine(Environment.SystemDirectory, "kernel32.dll"),
            "*");
        var options = new ProbeWorkerOptions(
            GetBuiltExecutable("RightMenuCheck.Probe.Worker"),
            TimeSpan.FromSeconds(5));

        var response = await client.RunAsync(invocation, options, CancellationToken.None);

        Assert.Equal(ProbeOutcome.NotApplicable, response.Outcome);
        Assert.Equal(ProbeProtocol.CurrentVersion, response.ProtocolVersion);
        Assert.NotEqual(Guid.Empty, response.RequestId);
        Assert.True(response.WorkerProcessId > 0);
        Assert.False(string.IsNullOrWhiteSpace(response.WorkerArchitecture));
        Assert.NotNull(response.Error);
        Assert.Equal("ProbeEnginePending", response.Error.Type);
    }

    [Fact]
    public async Task RunKillsWorkerJobWhenHandshakeTimesOut()
    {
        var client = new ProbeWorkerClient();
        var invocation = new ProbeInvocation(
            ProbeOperation.ClassicContextMenu,
            ProbeTargetKind.File,
            "{11111111-2222-3333-4444-555555555555}",
            Path.Combine(Environment.SystemDirectory, "kernel32.dll"),
            "*");
        var options = new ProbeWorkerOptions(
            GetBuiltExecutable("RightMenuCheck.Probe.FaultWorker"),
            TimeSpan.FromMilliseconds(300));

        var response = await client.RunAsync(invocation, options, CancellationToken.None);

        Assert.Equal(ProbeOutcome.TimedOut, response.Outcome);
        Assert.True(response.WorkerProcessId > 0);
        await AssertProcessExitedAsync(response.WorkerProcessId);
    }

    private static async Task AssertProcessExitedAsync(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await process.WaitForExitAsync(timeout.Token);
            Assert.True(process.HasExited);
        }
        catch (ArgumentException)
        {
            // The process already disappeared from the process table.
        }
    }

    private static string GetBuiltExecutable(string projectName)
    {
        var testOutput = new DirectoryInfo(AppContext.BaseDirectory);
        var binRoot = testOutput.Parent?.Parent ??
                      throw new DirectoryNotFoundException("The shared artifacts/bin directory was not found.");
        return Path.Combine(
            binRoot.FullName,
            projectName,
            "debug",
            $"{projectName}.exe");
    }
}
