namespace RightMenuCheck.Core.Inventory;

public sealed record RegistryScanIssue(
    RegistrySource Source,
    string Operation,
    string ErrorType,
    string Message);

public sealed record ContextMenuScanResult(
    DateTimeOffset StartedAt,
    TimeSpan Duration,
    IReadOnlyList<ContextMenuRegistration> Registrations,
    IReadOnlyList<RegistryScanIssue> Issues);

public sealed record PackageScanIssue(
    string PackageFullName,
    string SourcePath,
    string Operation,
    string ErrorType,
    string Message);

public sealed record PackagedContextMenuScanResult(
    DateTimeOffset StartedAt,
    TimeSpan Duration,
    IReadOnlyList<ContextMenuRegistration> Registrations,
    IReadOnlyList<PackageScanIssue> Issues);

public sealed record ContextMenuInventoryResult(
    DateTimeOffset StartedAt,
    TimeSpan Duration,
    IReadOnlyList<ContextMenuRegistration> Registrations,
    IReadOnlyList<RegistryScanIssue> RegistryIssues,
    IReadOnlyList<PackageScanIssue> PackageIssues);
