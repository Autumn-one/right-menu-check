using System.Diagnostics;
using RightMenuCheck.Core.Inventory;
using RightMenuCheck.Core.Metadata;
using RightMenuCheck.Windows.Benchmark;
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

    public ContextMenuDataService()
    {
        _benchmarkRunner = new ContextMenuBenchmarkRunner(
            new ProbeWorkerClient(),
            new ProbeWorkerSelector(ProbeWorkerLocator.Locate()));
    }

    public Task<ContextMenuScanSnapshot> ScanAsync(
        IProgress<ScanProgress>? progress,
        CancellationToken cancellationToken) =>
        Task.Run(
            async () => await ScanCoreAsync(progress, cancellationToken).ConfigureAwait(false),
            cancellationToken);

    public Task<ContextMenuBenchmarkResult> BenchmarkAsync(
        ContextMenuRegistrationMetadata metadata,
        BenchmarkTarget target,
        BenchmarkOptions options,
        CancellationToken cancellationToken) =>
        _benchmarkRunner.RunAsync(metadata, target, options, cancellationToken);

    public Task<ContextMenuBenchmarkResult> BenchmarkAggregateAsync(
        BenchmarkTarget target,
        BenchmarkOptions options,
        CancellationToken cancellationToken) =>
        _benchmarkRunner.RunAggregateAsync(target, options, cancellationToken);

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
