using System.Diagnostics;
using RightMenuCheck.Core.Inventory;

namespace RightMenuCheck.Windows.Inventory;

public sealed class ContextMenuInventoryScanner
{
    private readonly ContextMenuRegistryScanner _registryScanner;
    private readonly PackagedContextMenuScanner _packagedScanner;

    public ContextMenuInventoryScanner(
        ContextMenuRegistryScanner registryScanner,
        PackagedContextMenuScanner packagedScanner)
    {
        _registryScanner = registryScanner ?? throw new ArgumentNullException(nameof(registryScanner));
        _packagedScanner = packagedScanner ?? throw new ArgumentNullException(nameof(packagedScanner));
    }

    public ContextMenuInventoryResult Scan(CancellationToken cancellationToken = default)
    {
        var startedAt = DateTimeOffset.UtcNow;
        var stopwatch = Stopwatch.StartNew();
        var registryResult = _registryScanner.Scan(cancellationToken);
        var packagedResult = _packagedScanner.Scan(cancellationToken);
        stopwatch.Stop();

        var registrations = registryResult.Registrations
            .Concat(packagedResult.Registrations)
            .ToArray();

        return new ContextMenuInventoryResult(
            startedAt,
            stopwatch.Elapsed,
            registrations,
            registryResult.Issues,
            packagedResult.Issues);
    }
}
