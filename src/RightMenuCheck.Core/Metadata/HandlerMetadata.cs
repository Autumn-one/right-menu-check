using RightMenuCheck.Core.Inventory;

namespace RightMenuCheck.Core.Metadata;

public enum HandlerComponentRole
{
    ContextMenuHandler,
    ExplorerCommand,
    CommandStateHandler,
    DelegateExecute,
}

public enum ComServerKind
{
    Unknown,
    InProcess,
    LocalServer,
    PackagedInProcess,
    PackagedLocalServer,
    PackagedSurrogate,
}

public sealed record MetadataIssue(
    string Component,
    string Operation,
    string ErrorType,
    string Message);

public sealed record ComServerRegistration(
    string Clsid,
    ComServerKind Kind,
    string? DisplayName,
    string? RawServerPath,
    string? ResolvedServerPath,
    string? ThreadingModel,
    string? TreatAsClsid,
    RegistrySource? RegistrySource,
    string? PackageFullName,
    int? PackageServerId);

public sealed record HandlerComponentMetadata(
    HandlerComponentRole Role,
    string Clsid,
    ComServerRegistration? ComServer,
    IReadOnlyList<MetadataIssue> Issues);

public sealed record ContextMenuRegistrationMetadata(
    ContextMenuRegistration Registration,
    IReadOnlyList<HandlerComponentMetadata> Components);
