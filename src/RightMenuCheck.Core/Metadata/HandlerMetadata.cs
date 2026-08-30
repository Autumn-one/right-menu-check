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

public enum BinaryArchitectureKind
{
    Unknown,
    X86,
    X64,
    Arm,
    Arm64,
    AnyCpu,
    AnyCpuPrefer32Bit,
}

public enum SignatureVerificationStatus
{
    Unknown,
    NoSignature,
    Valid,
    Invalid,
    Error,
}

public enum ApplicationOwnerKind
{
    Unknown,
    InstalledApplication,
    Package,
    WindowsSystem,
}

public enum OwnershipConfidence
{
    None,
    Low,
    Medium,
    High,
    Exact,
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

public sealed record AuthenticodeSignatureMetadata(
    SignatureVerificationStatus Status,
    string? PublisherName,
    string? Subject,
    string? Issuer,
    string? Thumbprint,
    DateTimeOffset? ValidFrom,
    DateTimeOffset? ValidTo,
    string? TrustErrorCode);

public sealed record BinaryFileMetadata(
    string Path,
    bool Exists,
    long? Size,
    DateTimeOffset? LastWriteTime,
    string? Sha256,
    BinaryArchitectureKind Architecture,
    bool IsManaged,
    string? FileVersion,
    string? ProductVersion,
    string? ProductName,
    string? Description,
    string? CompanyName,
    AuthenticodeSignatureMetadata Signature,
    IReadOnlyList<MetadataIssue> Issues);

public sealed record ApplicationOwnerMetadata(
    ApplicationOwnerKind Kind,
    OwnershipConfidence Confidence,
    string DisplayName,
    string? Publisher,
    string? Version,
    string? InstallLocation,
    string? ProductCode,
    string? PackageFullName,
    RegistrySource? UninstallRegistrySource,
    string? UninstallKeyName,
    string? UninstallString,
    string? QuietUninstallString,
    bool IsWindowsInstaller,
    bool IsSystemProtected,
    string MatchReason);

public sealed record HandlerComponentMetadata(
    HandlerComponentRole Role,
    string Clsid,
    ComServerRegistration? ComServer,
    BinaryFileMetadata? Binary,
    IReadOnlyList<MetadataIssue> Issues);

public sealed record ContextMenuRegistrationMetadata(
    ContextMenuRegistration Registration,
    IReadOnlyList<HandlerComponentMetadata> Components,
    ApplicationOwnerMetadata? Owner,
    IReadOnlyList<MetadataIssue> Issues);
