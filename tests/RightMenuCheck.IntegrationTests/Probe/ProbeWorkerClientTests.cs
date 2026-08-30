using RightMenuCheck.Probe.Protocol;
using RightMenuCheck.Windows.Probe;
using System.Diagnostics;

namespace RightMenuCheck.IntegrationTests.Probe;

public sealed class ProbeWorkerClientTests
{
    [Fact]
    public async Task RunReturnsStructuredActivationFailureForUnregisteredHandler()
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

        Assert.True(
            response.Outcome == ProbeOutcome.ActivationFailed,
            $"Expected ActivationFailed but received {response.Outcome}: {response.Error}");
        Assert.Equal(ProbeProtocol.CurrentVersion, response.ProtocolVersion);
        Assert.NotEqual(Guid.Empty, response.RequestId);
        Assert.True(response.WorkerProcessId > 0);
        Assert.False(string.IsNullOrWhiteSpace(response.WorkerArchitecture));
        Assert.NotNull(response.Error);
        Assert.Equal("CoCreateInstance", response.Error.Type);
    }

    [Fact]
    public async Task RunMeasuresClassicContextMenuWithoutInvokingCommand()
    {
        var client = new ProbeWorkerClient();
        var invocation = new ProbeInvocation(
            ProbeOperation.ClassicContextMenu,
            ProbeTargetKind.File,
            "{09799AFB-AD67-11D1-ABCD-00C04FC30936}",
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "win.ini"),
            "*");

        var response = await client.RunAsync(
            invocation,
            CreateWorkerOptions(TimeSpan.FromSeconds(10)),
            CancellationToken.None);

        Assert.True(
            response.Outcome == ProbeOutcome.Success,
            $"Expected Success but received {response.Outcome}: {response.Error}");
        Assert.Contains(response.Phases, phase => phase.Phase == ProbePhase.ComActivation);
        Assert.Contains(response.Phases, phase => phase.Phase == ProbePhase.ShellInitialization);
        Assert.Contains(response.Phases, phase => phase.Phase == ProbePhase.MenuConstruction);
        Assert.All(response.Phases, phase =>
        {
            Assert.True(phase.Succeeded);
            Assert.True(phase.DurationMilliseconds >= 0);
        });
    }

    [Fact]
    public async Task RunMeasuresPackagedExplorerCommandWithoutInvokingCommand()
    {
        var client = new ProbeWorkerClient();
        var invocation = new ProbeInvocation(
            ProbeOperation.ExplorerCommand,
            ProbeTargetKind.FolderBackground,
            "{9F156763-7844-4DC4-B2B1-901F640F5155}",
            Path.GetTempPath(),
            "Directory\\Background");

        var response = await client.RunAsync(
            invocation,
            CreateWorkerOptions(TimeSpan.FromSeconds(10)),
            CancellationToken.None);

        Assert.True(
            response.Outcome == ProbeOutcome.Success,
            $"Expected Success but received {response.Outcome}: {response.Error}");
        Assert.Contains(response.Phases, phase => phase.Phase == ProbePhase.ComActivation);
        Assert.Contains(response.Phases, phase => phase.Phase == ProbePhase.GetTitle);
        Assert.Contains(response.Phases, phase => phase.Phase == ProbePhase.GetIcon);
        Assert.Contains(response.Phases, phase => phase.Phase == ProbePhase.GetState);
        Assert.Contains(response.Phases, phase => phase.Phase == ProbePhase.EnumerateSubCommands);
        Assert.DoesNotContain(
            response.Phases,
            phase => phase.Phase == ProbePhase.MenuConstruction);
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

    private static ProbeWorkerOptions CreateWorkerOptions(TimeSpan timeout) =>
        new(GetBuiltExecutable("RightMenuCheck.Probe.Worker"), timeout);

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
