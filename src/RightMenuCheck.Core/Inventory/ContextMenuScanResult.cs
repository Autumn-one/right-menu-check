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
