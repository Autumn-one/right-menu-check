using System.Runtime.InteropServices;
using RightMenuCheck.Core.Inventory;
using RightMenuCheck.Core.Metadata;
using RightMenuCheck.Core.Statistics;
using RightMenuCheck.Probe.Protocol;
using RightMenuCheck.Windows.Probe;

namespace RightMenuCheck.Windows.Benchmark;

public sealed class ContextMenuBenchmarkRunner
{
    private const string IsolatedMeasurementScope =
        "Fresh worker process per trial; OS file cache is retained; timings cover handler phases, not Explorer UI latency.";
    private readonly IProbeWorkerClient _probeClient;
    private readonly ProbeWorkerSelector _workerSelector;

    public ContextMenuBenchmarkRunner(
        IProbeWorkerClient probeClient,
        ProbeWorkerSelector workerSelector)
    {
        _probeClient = probeClient ?? throw new ArgumentNullException(nameof(probeClient));
        _workerSelector = workerSelector ?? throw new ArgumentNullException(nameof(workerSelector));
    }

    public async Task<ContextMenuBenchmarkResult> RunAsync(
        ContextMenuRegistrationMetadata metadata,
        BenchmarkTarget target,
        BenchmarkOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        ArgumentNullException.ThrowIfNull(target);
        ValidateOptions(options);

        var operation = GetProbeOperation(metadata.Registration);
        if (operation is null)
        {
            return CreateNotApplicable(metadata.Registration);
        }

        if (metadata.Registration.HandlerClsid is not { } handlerClsid)
        {
            return CreateNotApplicable(
                metadata.Registration,
                "No menu-construction handler CLSID is registered for this command.");
        }

        var registryView = GetRegistryView(metadata.Registration.Source);
        var architecture = GetHandlerArchitecture(metadata, handlerClsid);
        var worker = _workerSelector.Select(architecture, registryView);
        if (!worker.IsSupported || worker.WorkerPath is null)
        {
            return CreateUnsupported(metadata.Registration, worker.Reason);
        }

        var invocation = new ProbeInvocation(
            operation.Value,
            target.Kind,
            handlerClsid,
            target.Path,
            metadata.Registration.ClassPath);
        return await RunTrialsAsync(
                metadata.Registration.Id,
                isAggregate: false,
                invocation,
                new ProbeWorkerOptions(worker.WorkerPath, options.Timeout),
                options,
                cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<ContextMenuBenchmarkResult> RunAggregateAsync(
        BenchmarkTarget target,
        BenchmarkOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(target);
        ValidateOptions(options);

        var nativeArchitecture = RuntimeInformation.OSArchitecture switch
        {
            Architecture.X86 => BinaryArchitectureKind.X86,
            Architecture.Arm64 => BinaryArchitectureKind.Arm64,
            _ => BinaryArchitectureKind.X64,
        };
        var worker = _workerSelector.Select(
            nativeArchitecture,
            Environment.Is64BitOperatingSystem
                ? RegistryViewKind.Registry64
                : RegistryViewKind.Registry32);
        if (!worker.IsSupported || worker.WorkerPath is null)
        {
            return new ContextMenuBenchmarkResult(
                $"aggregate:{target.Kind}:{target.Path}",
                IsAggregate: true,
                BenchmarkStatus.Unsupported,
                AttemptedTrials: 0,
                SuccessfulTrials: 0,
                TimeoutCount: 0,
                CrashCount: 0,
                FailureCount: 0,
                HandlerDuration: null,
                PhaseStatistics: [],
                Trials: [],
                IsolatedMeasurementScope,
                worker.Reason);
        }

        var invocation = new ProbeInvocation(
            ProbeOperation.AggregatedContextMenu,
            target.Kind,
            HandlerClsid: string.Empty,
            target.Path,
            RegistryClassPath: null);
        return await RunTrialsAsync(
                $"aggregate:{target.Kind}:{target.Path}",
                isAggregate: true,
                invocation,
                new ProbeWorkerOptions(worker.WorkerPath, options.Timeout),
                options,
                cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<ContextMenuBenchmarkResult> RunTrialsAsync(
        string id,
        bool isAggregate,
        ProbeInvocation invocation,
        ProbeWorkerOptions workerOptions,
        BenchmarkOptions benchmarkOptions,
        CancellationToken cancellationToken)
    {
        var trials = new List<BenchmarkTrialResult>(benchmarkOptions.TrialCount);
        for (var trialNumber = 1; trialNumber <= benchmarkOptions.TrialCount; trialNumber++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var response = await _probeClient
                .RunAsync(invocation, workerOptions, cancellationToken)
                .ConfigureAwait(false);
            double? handlerDuration = response.Outcome == ProbeOutcome.Success
                ? response.Phases.Sum(static phase => phase.DurationMilliseconds)
                : null;
            trials.Add(new BenchmarkTrialResult(
                trialNumber,
                response.Outcome,
                handlerDuration,
                response.TotalDurationMilliseconds,
                response.WorkerProcessId,
                response.WorkerArchitecture,
                response.Phases,
                response.Menu,
                response.Error));
        }

        return BuildResult(id, isAggregate, trials);
    }

    private static ContextMenuBenchmarkResult BuildResult(
        string id,
        bool isAggregate,
        List<BenchmarkTrialResult> trials)
    {
        var successfulTrials = trials
            .Where(static trial => trial.Outcome == ProbeOutcome.Success)
            .ToArray();
        var timeoutCount = trials.Count(static trial => trial.Outcome == ProbeOutcome.TimedOut);
        var crashCount = trials.Count(static trial => trial.Outcome == ProbeOutcome.Crashed);
        var failureCount = trials.Count - successfulTrials.Length - timeoutCount - crashCount;
        var status = successfulTrials.Length switch
        {
            0 => BenchmarkStatus.Failed,
            _ when successfulTrials.Length == trials.Count => BenchmarkStatus.Completed,
            _ => BenchmarkStatus.Partial,
        };
        var phaseStatistics = trials
            .SelectMany(static trial => trial.Phases)
            .GroupBy(static phase => phase.Phase)
            .Select(group => new PhaseBenchmarkStatistics(
                group.Key,
                SampleStatistics.Calculate(group.Select(static phase => phase.DurationMilliseconds))!,
                group.Count(static phase => phase.Succeeded),
                group.Count(static phase => !phase.Succeeded)))
            .OrderBy(static phase => phase.Phase)
            .ToArray();

        return new ContextMenuBenchmarkResult(
            id,
            isAggregate,
            status,
            trials.Count,
            successfulTrials.Length,
            timeoutCount,
            crashCount,
            failureCount,
            SampleStatistics.Calculate(successfulTrials.Select(static trial =>
                trial.HandlerDurationMilliseconds!.Value)),
            phaseStatistics,
            trials,
            IsolatedMeasurementScope,
            Limitation: null);
    }

    private static ProbeOperation? GetProbeOperation(ContextMenuRegistration registration) =>
        registration.Kind switch
        {
            ContextMenuRegistrationKind.ClassicContextMenuHandler =>
                ProbeOperation.ClassicContextMenu,
            ContextMenuRegistrationKind.ExplorerCommand or
                ContextMenuRegistrationKind.PackagedExplorerCommand =>
                ProbeOperation.ExplorerCommand,
            _ => null,
        };

    private static BinaryArchitectureKind GetHandlerArchitecture(
        ContextMenuRegistrationMetadata metadata,
        string handlerClsid) =>
        metadata.Components.FirstOrDefault(component => component.Clsid.Equals(
                handlerClsid,
                StringComparison.OrdinalIgnoreCase))?
            .Binary?
            .Architecture ?? BinaryArchitectureKind.Unknown;

    private static RegistryViewKind GetRegistryView(ContextMenuSource source) => source switch
    {
        RegistryContextMenuSource registrySource => registrySource.Location.View,
        PackageContextMenuSource packageSource when packageSource.Architecture is
            PackageArchitectureKind.X86 or PackageArchitectureKind.X86OnArm64 =>
            RegistryViewKind.Registry32,
        _ => RegistryViewKind.Registry64,
    };

    private static ContextMenuBenchmarkResult CreateNotApplicable(
        ContextMenuRegistration registration,
        string? limitation = null) =>
        new(
            registration.Id,
            IsAggregate: false,
            BenchmarkStatus.NotApplicable,
            AttemptedTrials: 0,
            SuccessfulTrials: 0,
            TimeoutCount: 0,
            CrashCount: 0,
            FailureCount: 0,
            HandlerDuration: null,
            PhaseStatistics: [],
            Trials: [],
            IsolatedMeasurementScope,
            limitation ?? GetNotApplicableReason(registration.Kind));

    private static ContextMenuBenchmarkResult CreateUnsupported(
        ContextMenuRegistration registration,
        string? limitation) =>
        new(
            registration.Id,
            IsAggregate: false,
            BenchmarkStatus.Unsupported,
            AttemptedTrials: 0,
            SuccessfulTrials: 0,
            TimeoutCount: 0,
            CrashCount: 0,
            FailureCount: 0,
            HandlerDuration: null,
            PhaseStatistics: [],
            Trials: [],
            IsolatedMeasurementScope,
            limitation ?? "No compatible isolated worker is available.");

    private static string GetNotApplicableReason(ContextMenuRegistrationKind kind) => kind switch
    {
        ContextMenuRegistrationKind.DelegateExecuteVerb =>
            "DelegateExecute runs only after command selection; invocation is intentionally prohibited.",
        ContextMenuRegistrationKind.StaticVerb =>
            "Static verbs have no per-handler menu-construction callback to time.",
        ContextMenuRegistrationKind.CascadingVerb =>
            "This cascade is registry-defined; use the aggregate menu benchmark for its construction cost.",
        _ => "This registration cannot be attributed with a supported per-handler probe.",
    };

    private static void ValidateOptions(BenchmarkOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.TrialCount is < 3 or > 20)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                options.TrialCount,
                "Trial count must be between 3 and 20.");
        }
    }
}
