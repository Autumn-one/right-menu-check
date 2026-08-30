using RightMenuCheck.Core.Inventory;
using RightMenuCheck.Core.Metadata;
using RightMenuCheck.Probe.Protocol;
using RightMenuCheck.Windows.Benchmark;
using RightMenuCheck.Windows.Probe;

namespace RightMenuCheck.IntegrationTests.Benchmark;

public sealed class SystemContextMenuBenchmarkTests
{
    [Fact]
    public async Task RunUsesFreshWorkerForEveryClassicHandlerTrial()
    {
        var runner = CreateRunner();
        var target = new BenchmarkTarget(
            ProbeTargetKind.File,
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "win.ini"));

        var result = await runner.RunAsync(
            CreateOpenWithMetadata(),
            target,
            new BenchmarkOptions(3, TimeSpan.FromSeconds(10)),
            CancellationToken.None);

        Assert.Equal(BenchmarkStatus.Completed, result.Status);
        Assert.Equal(3, result.SuccessfulTrials);
        Assert.NotNull(result.HandlerDuration);
        Assert.Equal(3, result.HandlerDuration.Count);
        Assert.True(result.HandlerDuration.Median >= 0);
        Assert.True(result.HandlerDuration.Percentile95 >= result.HandlerDuration.Median);
        Assert.Equal(
            3,
            result.Trials.Select(static trial => trial.WorkerProcessId).Distinct().Count());
        Assert.Contains(
            result.PhaseStatistics,
            phase => phase.Phase == ProbePhase.MenuConstruction);
    }

    [Fact]
    public async Task RunAggregateCalculatesDistributionAcrossFreshWorkers()
    {
        var runner = CreateRunner();
        var target = new BenchmarkTarget(
            ProbeTargetKind.File,
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "win.ini"));

        var result = await runner.RunAggregateAsync(
            target,
            new BenchmarkOptions(3, TimeSpan.FromSeconds(15)),
            CancellationToken.None);

        Assert.Equal(BenchmarkStatus.Completed, result.Status);
        Assert.True(result.IsAggregate);
        Assert.NotNull(result.HandlerDuration);
        Assert.Equal(3, result.HandlerDuration.Count);
        Assert.Contains(
            result.PhaseStatistics,
            phase => phase.Phase == ProbePhase.AggregateMenuCreation);
    }

    private static ContextMenuBenchmarkRunner CreateRunner()
    {
        var workerPath = GetBuiltWorkerPath();
        return new ContextMenuBenchmarkRunner(
            new ProbeWorkerClient(),
            new ProbeWorkerSelector(new ProbeWorkerPaths(
                workerPath,
                workerPath,
                Arm64WorkerPath: null)));
    }

    private static ContextMenuRegistrationMetadata CreateOpenWithMetadata()
    {
        var registration = new ContextMenuRegistration
        {
            Id = "open-with",
            Source = new RegistryContextMenuSource(new RegistrySource(
                RegistryHiveKind.LocalMachine,
                RegistryViewKind.Registry64,
                "Software\\Classes\\*\\shellex\\ContextMenuHandlers\\Open With")),
            ClassPath = "*",
            RegistrationPath =
                "Software\\Classes\\*\\shellex\\ContextMenuHandlers\\Open With",
            CanonicalName = "Open With",
            DisplayName = "Open With",
            TargetKind = ContextMenuTargetKind.File,
            Kind = ContextMenuRegistrationKind.ClassicContextMenuHandler,
            HandlerClsid = "{09799AFB-AD67-11D1-ABCD-00C04FC30936}",
        };
        return new ContextMenuRegistrationMetadata(
            registration,
            Components: [],
            Owner: null,
            Issues: []);
    }

    private static string GetBuiltWorkerPath()
    {
        var testOutput = new DirectoryInfo(AppContext.BaseDirectory);
        var binRoot = testOutput.Parent?.Parent ??
                      throw new DirectoryNotFoundException("The shared artifacts/bin directory was not found.");
        return Path.Combine(
            binRoot.FullName,
            "RightMenuCheck.Probe.Worker",
            "debug",
            "RightMenuCheck.Probe.Worker.exe");
    }
}
