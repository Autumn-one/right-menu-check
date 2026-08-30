using RightMenuCheck.Core.Inventory;
using RightMenuCheck.Core.Metadata;
using RightMenuCheck.Core.Statistics;
using RightMenuCheck.Probe.Protocol;
using RightMenuCheck.Windows.Benchmark;
using RightMenuCheck.Windows.Probe;

namespace RightMenuCheck.Windows.Tests.Benchmark;

public sealed class ContextMenuBenchmarkRunnerTests
{
    private const string HandlerClsid = "{11111111-2222-3333-4444-555555555555}";

    [Fact]
    public void DefaultOptionsUseThreeTrials()
    {
        Assert.Equal(3, BenchmarkOptions.Default.TrialCount);
    }

    [Fact]
    public async Task RunCalculatesMedianNearestRankP95AndPhaseStatistics()
    {
        var client = new FakeProbeClient(
            CreateSuccess(1, 2),
            CreateSuccess(2, 3),
            CreateSuccess(4, 5));
        var runner = CreateRunner(client);

        var result = await runner.RunAsync(
            CreateMetadata(ContextMenuRegistrationKind.ClassicContextMenuHandler),
            new BenchmarkTarget(ProbeTargetKind.File, "C:\\Samples\\file.txt"),
            new BenchmarkOptions(3, TimeSpan.FromSeconds(1)),
            CancellationToken.None);

        Assert.Equal(BenchmarkStatus.Completed, result.Status);
        Assert.Equal(3, result.SuccessfulTrials);
        Assert.NotNull(result.HandlerDuration);
        Assert.Equal(5, result.HandlerDuration.Median);
        Assert.Equal(9, result.HandlerDuration.Percentile95);
        Assert.Equal(2, result.PhaseStatistics.Count);
        var activation = Assert.Single(
            result.PhaseStatistics,
            phase => phase.Phase == ProbePhase.ComActivation);
        Assert.Equal(2, activation.Distribution.Median);
        Assert.Equal(4, activation.Distribution.Percentile95);
        Assert.Equal(0, result.FailureRate);
        Assert.Equal(3, client.Invocations.Count);
    }

    [Fact]
    public async Task RunKeepsTimeoutAndCrashCountsSeparateFromSuccessfulStatistics()
    {
        var client = new FakeProbeClient(
            CreateSuccess(1, 1),
            CreateFailure(ProbeOutcome.TimedOut),
            CreateFailure(ProbeOutcome.Crashed));
        var runner = CreateRunner(client);

        var result = await runner.RunAsync(
            CreateMetadata(ContextMenuRegistrationKind.ExplorerCommand),
            new BenchmarkTarget(ProbeTargetKind.Folder, "C:\\Samples"),
            new BenchmarkOptions(3, TimeSpan.FromSeconds(1)),
            CancellationToken.None);

        Assert.Equal(BenchmarkStatus.Partial, result.Status);
        Assert.Equal(1, result.SuccessfulTrials);
        Assert.Equal(1, result.TimeoutCount);
        Assert.Equal(1, result.CrashCount);
        Assert.Equal(0, result.FailureCount);
        Assert.Equal(2d / 3d, result.FailureRate);
        Assert.NotNull(result.HandlerDuration);
        Assert.Equal(2, result.HandlerDuration.Median);
    }

    [Fact]
    public async Task FailureAnalysisGroupsOutcomePhaseErrorAndHResult()
    {
        var client = new FakeProbeClient(
            CreateQueryFailure(),
            CreateQueryFailure(),
            CreateFailure(ProbeOutcome.TimedOut));
        var runner = CreateRunner(client);

        var result = await runner.RunAsync(
            CreateMetadata(ContextMenuRegistrationKind.ClassicContextMenuHandler),
            new BenchmarkTarget(ProbeTargetKind.File, "C:\\Samples\\file.txt"),
            new BenchmarkOptions(3, TimeSpan.FromSeconds(1)),
            CancellationToken.None);

        var reasons = BenchmarkFailureAnalyzer.Group(result);

        Assert.Equal(2, reasons.Count);
        var queryFailure = Assert.Single(
            reasons,
            reason => reason.Outcome == ProbeOutcome.QueryFailed);
        Assert.Equal(2, queryFailure.Count);
        Assert.Equal(ProbePhase.MenuConstruction, queryFailure.FailedPhase);
        Assert.Equal("QueryContextMenu", queryFailure.ErrorType);
        Assert.Equal(unchecked((int)0x80004005), queryFailure.HResult);
    }

    [Theory]
    [InlineData(ContextMenuRegistrationKind.StaticVerb)]
    [InlineData(ContextMenuRegistrationKind.DelegateExecuteVerb)]
    [InlineData(ContextMenuRegistrationKind.CascadingVerb)]
    public async Task RunMarksUnattributableRegistrationsNotApplicableWithoutFakeZero(
        ContextMenuRegistrationKind kind)
    {
        var client = new FakeProbeClient();
        var runner = CreateRunner(client);

        var result = await runner.RunAsync(
            CreateMetadata(kind),
            new BenchmarkTarget(ProbeTargetKind.File, "C:\\Samples\\file.txt"),
            new BenchmarkOptions(3, TimeSpan.FromSeconds(1)),
            CancellationToken.None);

        Assert.Equal(BenchmarkStatus.NotApplicable, result.Status);
        Assert.Equal(0, result.AttemptedTrials);
        Assert.Null(result.HandlerDuration);
        Assert.Null(result.FailureRate);
        Assert.NotNull(result.Limitation);
        Assert.Empty(client.Invocations);
    }

    [Fact]
    public async Task RunAggregateUsesAggregateOperationAndProducesStatistics()
    {
        var client = new FakeProbeClient(
            CreateSuccess(5, 10),
            CreateSuccess(6, 11),
            CreateSuccess(7, 12));
        var runner = CreateRunner(client);

        var result = await runner.RunAggregateAsync(
            new BenchmarkTarget(ProbeTargetKind.File, "C:\\Samples\\file.txt"),
            new BenchmarkOptions(3, TimeSpan.FromSeconds(1)),
            CancellationToken.None);

        Assert.True(result.IsAggregate);
        Assert.Equal(BenchmarkStatus.Completed, result.Status);
        Assert.All(
            client.Invocations,
            invocation => Assert.Equal(ProbeOperation.AggregatedContextMenu, invocation.Operation));
        Assert.All(client.Invocations, invocation => Assert.Equal(string.Empty, invocation.HandlerClsid));
    }

    [Fact]
    public void ComparerOrdersTimeoutCrashFailureP95AndNotApplicable()
    {
        var results = new[]
        {
            CreateResult("not-applicable", BenchmarkStatus.NotApplicable, timeout: 0, crash: 0, p95: null),
            CreateResult("fast", BenchmarkStatus.Completed, timeout: 0, crash: 0, p95: 5),
            CreateResult("slow", BenchmarkStatus.Completed, timeout: 0, crash: 0, p95: 50),
            CreateResult(
                "partial-failure",
                BenchmarkStatus.Partial,
                timeout: 0,
                crash: 0,
                p95: 1,
                failureCount: 1),
            CreateResult("failure", BenchmarkStatus.Failed, timeout: 0, crash: 0, p95: null),
            CreateResult("crash", BenchmarkStatus.Partial, timeout: 0, crash: 1, p95: 1),
            CreateResult("timeout", BenchmarkStatus.Partial, timeout: 1, crash: 0, p95: 1),
        };

        Array.Sort(results, ContextMenuBenchmarkComparer.Instance);

        Assert.Equal(
            ["timeout", "crash", "partial-failure", "failure", "slow", "fast", "not-applicable"],
            results.Select(static result => result.Id));
    }

    private static ContextMenuBenchmarkRunner CreateRunner(IProbeWorkerClient client) => new(
        client,
        new ProbeWorkerSelector(new ProbeWorkerPaths(
            "C:\\Workers\\x64\\worker.exe",
            "C:\\Workers\\x86\\worker.exe",
            Arm64WorkerPath: null)));

    private static ContextMenuRegistrationMetadata CreateMetadata(
        ContextMenuRegistrationKind kind)
    {
        var registration = new ContextMenuRegistration
        {
            Id = $"registration-{kind}",
            Source = new RegistryContextMenuSource(new RegistrySource(
                RegistryHiveKind.LocalMachine,
                RegistryViewKind.Registry64,
                "Software\\Classes\\*\\shell\\test")),
            ClassPath = "*",
            RegistrationPath = "Software\\Classes\\*\\shell\\test",
            CanonicalName = "test",
            DisplayName = "Test",
            TargetKind = ContextMenuTargetKind.File,
            Kind = kind,
            HandlerClsid = HandlerClsid,
        };
        return new ContextMenuRegistrationMetadata(
            registration,
            Components: [],
            Owner: null,
            Issues: []);
    }

    private static ProbeResponse CreateSuccess(double activation, double query) => new(
        ProbeProtocol.CurrentVersion,
        Guid.NewGuid(),
        "nonce",
        ProbeOutcome.Success,
        WorkerProcessId: Random.Shared.Next(1000, 5000),
        WorkerArchitecture: "X64",
        DateTimeOffset.UtcNow,
        activation + query + 1,
        [
            new ProbePhaseTiming(ProbePhase.ComActivation, activation, HResult: 0, Succeeded: true),
            new ProbePhaseTiming(ProbePhase.MenuConstruction, query, HResult: 0, Succeeded: true),
        ],
        Error: null);

    private static ProbeResponse CreateFailure(ProbeOutcome outcome) => new(
        ProbeProtocol.CurrentVersion,
        Guid.NewGuid(),
        "nonce",
        outcome,
        WorkerProcessId: 1,
        WorkerArchitecture: "X64",
        DateTimeOffset.UtcNow,
        TotalDurationMilliseconds: 100,
        Phases: [],
        new ProbeError(outcome.ToString(), "Failure fixture", HResult: null));

    private static ProbeResponse CreateQueryFailure() => new(
        ProbeProtocol.CurrentVersion,
        Guid.NewGuid(),
        "nonce",
        ProbeOutcome.QueryFailed,
        WorkerProcessId: 1,
        WorkerArchitecture: "X64",
        DateTimeOffset.UtcNow,
        TotalDurationMilliseconds: 10,
        Phases:
        [
            new ProbePhaseTiming(
                ProbePhase.MenuConstruction,
                DurationMilliseconds: 5,
                HResult: unchecked((int)0x80004005),
                Succeeded: false),
        ],
        new ProbeError(
            "QueryContextMenu",
            "The handler rejected the sample.",
            unchecked((int)0x80004005)));

    private static ContextMenuBenchmarkResult CreateResult(
        string id,
        BenchmarkStatus status,
        int timeout,
        int crash,
        double? p95,
        int failureCount = 0) => new(
            id,
            IsAggregate: false,
            status,
            AttemptedTrials: 3,
            SuccessfulTrials: status == BenchmarkStatus.Completed ? 3 : 1,
            timeout,
            crash,
            FailureCount: status == BenchmarkStatus.Failed ? 3 : failureCount,
            HandlerDuration: p95 is null
                ? null
                : new SampleDistribution(3, p95.Value, p95.Value, p95.Value, p95.Value, p95.Value),
            PhaseStatistics: [],
            Trials: [],
            MeasurementScope: "test",
            Limitation: null);

    private sealed class FakeProbeClient(params ProbeResponse[] responses) : IProbeWorkerClient
    {
        private readonly Queue<ProbeResponse> _responses = new(responses);

        public List<ProbeInvocation> Invocations { get; } = [];

        public Task<ProbeResponse> RunAsync(
            ProbeInvocation invocation,
            ProbeWorkerOptions options,
            CancellationToken cancellationToken = default)
        {
            Invocations.Add(invocation);
            var response = _responses.Dequeue();
            return Task.FromResult(response with
            {
                RequestId = Guid.NewGuid(),
                Nonce = "fixture",
            });
        }
    }
}
