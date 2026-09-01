using System.Diagnostics;
using RightMenuCheck.Core.Inventory;
using RightMenuCheck.Core.Metadata;
using RightMenuCheck.Probe.Protocol;
using RightMenuCheck.Windows.Benchmark;
using RightMenuCheck.Windows.Diagnostics;
using RightMenuCheck.Windows.Inventory;
using RightMenuCheck.Windows.Metadata;
using RightMenuCheck.Windows.Packages;
using RightMenuCheck.Windows.Probe;
using RightMenuCheck.Windows.Registry;

namespace RightMenuCheck.App.Services;

public enum ScanStage
{
    Discovering,
    Enriching,
    Completed,
}

public sealed record ScanProgress(ScanStage Stage, int Completed, int Total);

public sealed record ContextMenuScanSnapshot(
    DateTimeOffset StartedAt,
    TimeSpan Duration,
    IReadOnlyList<ContextMenuRegistrationMetadata> Items,
    IReadOnlyList<RegistryScanIssue> RegistryIssues,
    IReadOnlyList<PackageScanIssue> PackageIssues,
    IReadOnlyList<MetadataIssue> MetadataIssues);

public interface IContextMenuDataService
{
    Task<ContextMenuScanSnapshot> ScanAsync(
        IProgress<ScanProgress>? progress,
        CancellationToken cancellationToken);

    Task<ContextMenuBenchmarkResult> BenchmarkAsync(
        ContextMenuRegistrationMetadata metadata,
        BenchmarkTarget target,
        BenchmarkOptions options,
        CancellationToken cancellationToken);

    Task<ContextMenuBenchmarkResult> BenchmarkAggregateAsync(
        BenchmarkTarget target,
        BenchmarkOptions options,
        CancellationToken cancellationToken);
}

public sealed class ContextMenuDataService : IContextMenuDataService
{
    private readonly ContextMenuBenchmarkRunner _benchmarkRunner;
    private readonly IAppLogger _logger;

    public ContextMenuDataService(IAppLogger? logger = null)
    {
        _logger = logger ?? NullAppLogger.Instance;
        _benchmarkRunner = new ContextMenuBenchmarkRunner(
            new ProbeWorkerClient(),
            new ProbeWorkerSelector(ProbeWorkerLocator.Locate()));
    }

    public async Task<ContextMenuScanSnapshot> ScanAsync(
        IProgress<ScanProgress>? progress,
        CancellationToken cancellationToken)
    {
        _logger.Log(
            AppLogLevel.Information,
            "scan.started",
            "Context-menu inventory scan started.");
        try
        {
            var snapshot = await Task.Run(
                    async () => await ScanCoreAsync(progress, cancellationToken).ConfigureAwait(false),
                    cancellationToken)
                .ConfigureAwait(false);
            _logger.Log(
                AppLogLevel.Information,
                "scan.completed",
                "Context-menu inventory scan completed.",
                new Dictionary<string, object?>
                {
                    ["registrationCount"] = snapshot.Items.Count,
                    ["registryIssueCount"] = snapshot.RegistryIssues.Count,
                    ["packageIssueCount"] = snapshot.PackageIssues.Count,
                    ["metadataIssueCount"] = snapshot.MetadataIssues.Count,
                    ["durationMilliseconds"] = snapshot.Duration.TotalMilliseconds,
                });
            return snapshot;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _logger.Log(
                AppLogLevel.Information,
                "scan.canceled",
                "Context-menu inventory scan was canceled.");
            throw;
        }
#pragma warning disable CA1031
        catch (Exception exception)
#pragma warning restore CA1031
        {
            _logger.Log(
                AppLogLevel.Error,
                "scan.failed",
                "Context-menu inventory scan failed.",
                exception: exception);
            throw;
        }
    }

    public async Task<ContextMenuBenchmarkResult> BenchmarkAsync(
        ContextMenuRegistrationMetadata metadata,
        BenchmarkTarget target,
        BenchmarkOptions options,
        CancellationToken cancellationToken)
    {
        _logger.Log(
            AppLogLevel.Information,
            "benchmark.started",
            "Isolated handler benchmark started.",
            new Dictionary<string, object?>
            {
                ["registrationId"] = metadata.Registration.Id,
                ["trialCount"] = options.TrialCount,
                ["targetKind"] = target.Kind.ToString(),
            });
        var result = await _benchmarkRunner
            .RunAsync(metadata, target, options, cancellationToken)
            .ConfigureAwait(false);
        LogBenchmarkResult("benchmark.completed", result);
        return result;
    }

    public async Task<ContextMenuBenchmarkResult> BenchmarkAggregateAsync(
        BenchmarkTarget target,
        BenchmarkOptions options,
        CancellationToken cancellationToken)
    {
        _logger.Log(
            AppLogLevel.Information,
            "benchmark.aggregate_started",
            "Aggregate Shell-menu benchmark started.",
            new Dictionary<string, object?>
            {
                ["trialCount"] = options.TrialCount,
                ["targetKind"] = target.Kind.ToString(),
            });
        var result = await _benchmarkRunner
            .RunAggregateAsync(target, options, cancellationToken)
            .ConfigureAwait(false);
        LogBenchmarkResult("benchmark.aggregate_completed", result);
        return result;
    }

    private void LogBenchmarkResult(string eventName, ContextMenuBenchmarkResult result)
    {
        var level = result.TimeoutCount > 0 || result.CrashCount > 0
            ? AppLogLevel.Warning
            : result.Status == BenchmarkStatus.Failed
                ? AppLogLevel.Error
                : AppLogLevel.Information;
        var failureReasons = BenchmarkFailureAnalyzer.Group(result);
        var observedMenus = result.Trials
            .Where(static trial => trial.Outcome == ProbeOutcome.Success && trial.Menu is not null)
            .Select(static trial => trial.Menu!)
            .ToArray();
        _logger.Log(
            level,
            eventName,
            "Benchmark completed.",
            new Dictionary<string, object?>
            {
                ["registrationId"] = result.Id,
                ["status"] = result.Status.ToString(),
                ["attemptedTrials"] = result.AttemptedTrials,
                ["successfulTrials"] = result.SuccessfulTrials,
                ["timeoutCount"] = result.TimeoutCount,
                ["crashCount"] = result.CrashCount,
                ["failureCount"] = result.FailureCount,
                ["medianMilliseconds"] = result.HandlerDuration?.Median,
                ["percentile95Milliseconds"] = result.HandlerDuration?.Percentile95,
                ["observedMenuTrials"] = observedMenus.Length,
                ["maximumObservedMenuItems"] = observedMenus.Length == 0
                    ? null
                    : observedMenus.Max(static menu => menu.Items.Count),
                ["menuSnapshotTruncated"] = observedMenus.Any(static menu => menu.Truncated),
                ["failureReasons"] = failureReasons.Count == 0
                    ? null
                    : string.Join(" || ", failureReasons.Select(FormatFailureReasonForLog)),
            });
    }

    private static string FormatFailureReasonForLog(BenchmarkFailureReason reason) =>
        $"outcome={reason.Outcome};phase={reason.FailedPhase?.ToString() ?? "none"};" +
        $"type={reason.ErrorType ?? "none"};" +
        $"hresult={(reason.HResult is { } hResult ? $"0x{unchecked((uint)hResult):X8}" : "none")};" +
        $"count={reason.Count};message={reason.ErrorMessage ?? "none"}";

    private static async Task<ContextMenuScanSnapshot> ScanCoreAsync(
        IProgress<ScanProgress>? progress,
        CancellationToken cancellationToken)
    {
        var startedAt = DateTimeOffset.UtcNow;
        var stopwatch = Stopwatch.StartNew();
        progress?.Report(new ScanProgress(ScanStage.Discovering, Completed: 0, Total: 0));

        var registryReader = new SystemRegistryReader();
        var packageCatalog = new SystemInstalledPackageCatalog().GetPackages();
        var packageSnapshot = new SnapshotPackageCatalog(packageCatalog);
        var inventoryScanner = new ContextMenuInventoryScanner(
            new ContextMenuRegistryScanner(registryReader),
            new PackagedContextMenuScanner(
                packageSnapshot,
                new PhysicalManifestStreamProvider()));
        var inventory = inventoryScanner.Scan(cancellationToken);

        var applicationCatalog = new RegistryInstalledApplicationCatalog(registryReader)
            .GetApplications();
        var ownershipResolver = new ApplicationOwnershipResolver(
            new SnapshotApplicationCatalog(applicationCatalog),
            packageSnapshot);
        var binaryReader = new CachingBinaryMetadataReader(new BinaryMetadataReader());
        var metadataEnricher = new ContextMenuMetadataEnricher(
            new ComServerResolver(registryReader),
            binaryReader);
        var items = new ContextMenuRegistrationMetadata[inventory.Registrations.Count];
        var completed = 0;
        progress?.Report(new ScanProgress(
            ScanStage.Enriching,
            Completed: 0,
            inventory.Registrations.Count));

        await Parallel.ForEachAsync(
            Enumerable.Range(0, inventory.Registrations.Count),
            new ParallelOptions
            {
                CancellationToken = cancellationToken,
                MaxDegreeOfParallelism = 2,
            },
            (index, _) =>
            {
                var enriched = metadataEnricher.Enrich(inventory.Registrations[index]);
                items[index] = ownershipResolver.Resolve(enriched);
                var current = Interlocked.Increment(ref completed);
                progress?.Report(new ScanProgress(
                    ScanStage.Enriching,
                    current,
                    inventory.Registrations.Count));
                return ValueTask.CompletedTask;
            }).ConfigureAwait(false);

        stopwatch.Stop();
        progress?.Report(new ScanProgress(
            ScanStage.Completed,
            items.Length,
            items.Length));
        return new ContextMenuScanSnapshot(
            startedAt,
            stopwatch.Elapsed,
            items,
            inventory.RegistryIssues,
            inventory.PackageIssues,
            ownershipResolver.CatalogIssues);
    }

    private sealed class SnapshotPackageCatalog(InstalledPackageCatalogResult snapshot)
        : IInstalledPackageCatalog
    {
        public InstalledPackageCatalogResult GetPackages() => snapshot;
    }

    private sealed class SnapshotApplicationCatalog(InstalledApplicationCatalogResult snapshot)
        : IInstalledApplicationCatalog
    {
        public InstalledApplicationCatalogResult GetApplications() => snapshot;
    }
}
