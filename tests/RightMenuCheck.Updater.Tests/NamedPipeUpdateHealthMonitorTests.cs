using System.IO.Pipes;
using RightMenuCheck.Distribution;
using RightMenuCheck.Updater;

namespace RightMenuCheck.Updater.Tests;

public sealed class NamedPipeUpdateHealthMonitorTests
{
    [Fact]
    public async Task AcceptsReportFromExpectedProcessWithMatchingTokenAndVersion()
    {
        var monitor = new NamedPipeUpdateHealthMonitor();
        var token = Guid.NewGuid().ToString("N");
        using var endpoint = monitor.Create(token);
        var process = new UpdateProcessHandle(Environment.ProcessId, Environment.ProcessPath!);
        var controller = new RunningProcessController();
        var wait = monitor.WaitForHealthyAsync(
            endpoint,
            process,
            "1.2.3",
            controller,
            TimeSpan.FromSeconds(3),
            CancellationToken.None);

        await SendAsync(endpoint.PipeName, new UpdateHealthReport(
            token,
            Environment.ProcessId,
            "1.2.3"));

        Assert.True(await wait);
    }

    [Fact]
    public async Task RejectsReportWithWrongPayloadIdentity()
    {
        var monitor = new NamedPipeUpdateHealthMonitor();
        var token = Guid.NewGuid().ToString("N");
        using var endpoint = monitor.Create(token);
        var process = new UpdateProcessHandle(Environment.ProcessId, Environment.ProcessPath!);
        var wait = monitor.WaitForHealthyAsync(
            endpoint,
            process,
            "1.2.3",
            new RunningProcessController(),
            TimeSpan.FromSeconds(3),
            CancellationToken.None);

        await SendAsync(endpoint.PipeName, new UpdateHealthReport(
            "wrong-token",
            Environment.ProcessId,
            "9.9.9"));

        Assert.False(await wait);
    }

    [Fact]
    public async Task AcceptsEquivalentSemanticVersionWithBuildMetadata()
    {
        var monitor = new NamedPipeUpdateHealthMonitor();
        var token = Guid.NewGuid().ToString("N");
        using var endpoint = monitor.Create(token);
        var process = new UpdateProcessHandle(Environment.ProcessId, Environment.ProcessPath!);
        var wait = monitor.WaitForHealthyAsync(
            endpoint,
            process,
            "1.2.3",
            new RunningProcessController(),
            TimeSpan.FromSeconds(3),
            CancellationToken.None);

        await SendAsync(endpoint.PipeName, new UpdateHealthReport(
            token,
            Environment.ProcessId,
            "1.2.3+commit.abcdef"));

        Assert.True(await wait);
    }

    private static async Task SendAsync(string pipeName, UpdateHealthReport report)
    {
        await using var client = new NamedPipeClientStream(
            ".",
            pipeName,
            PipeDirection.Out,
            PipeOptions.Asynchronous);
        await client.ConnectAsync(CancellationToken.None);
        await using var writer = new StreamWriter(client)
        {
            AutoFlush = true,
        };
        await writer.WriteLineAsync(DistributionJson.Serialize(report));
    }

    private sealed class RunningProcessController : IUpdateProcessController
    {
        public IVerifiedUpdateParent OpenVerifiedParent(
            int processId,
            string expectedExecutablePath) => throw new NotSupportedException();

        public UpdateProcessHandle Start(
            string executablePath,
            string workingDirectory,
            IReadOnlyList<string> arguments) => throw new NotSupportedException();

        public bool HasExited(UpdateProcessHandle process) => false;

        public Task StopAsync(
            UpdateProcessHandle process,
            CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
