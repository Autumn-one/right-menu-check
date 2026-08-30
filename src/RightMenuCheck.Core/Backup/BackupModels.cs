using RightMenuCheck.Core.Inventory;
using RightMenuCheck.Core.Metadata;

namespace RightMenuCheck.Core.Backup;

public static class RightMenuBackupFormat
{
    public const int CurrentVersion = 1;
    public const string ManifestEntryName = "manifest.json";
    public const string IntegrityEntryName = "integrity.json";
}

public enum BackupPurpose
{
    Manual,
    BeforeDisable,
    BeforeRemove,
    BeforeRestore,
}

public enum BackupRegistryValueKind
{
    None,
    Text,
    ExpandableText,
    MultiText,
    Binary,
    DWord,
    QWord,
}

public sealed record RegistryValueSnapshot(
    string Name,
    BackupRegistryValueKind Kind,
    string? Text,
    IReadOnlyList<string>? TextItems,
    string? Base64Data,
    long? NumericValue);

public sealed record RegistryKeySnapshot(
    RegistrySource Source,
    string? SecurityDescriptorSddl,
    IReadOnlyList<RegistryValueSnapshot> Values);

public sealed record BackupFileReference(
    string Path,
    bool Exists,
    long? Size,
    string? Sha256,
    string? FileVersion,
    BinaryArchitectureKind Architecture,
    SignatureVerificationStatus SignatureStatus,
    string? Publisher);

public sealed record BackupPackageReference(
    string Name,
    string FullName,
    string FamilyName,
    string ApplicationId,
    PackageArchitectureKind Architecture,
    string ManifestPath);

public sealed record BackupOwnerReference(
    ApplicationOwnerKind Kind,
    OwnershipConfidence Confidence,
    string DisplayName,
    string? Publisher,
    string? Version,
    string? InstallLocation,
    string? ProductCode,
    string? PackageFullName,
    bool IsSystemProtected);

public sealed record BackedUpRegistration(
    string Id,
    string DisplayName,
    ContextMenuRegistrationKind Kind,
    ContextMenuTargetKind TargetKind,
    string ClassPath,
    string RegistrationPath,
    string? HandlerClsid,
    RegistrySource? RegistrySource,
    BackupPackageReference? Package,
    BackupOwnerReference? Owner,
    IReadOnlyList<BackupFileReference> Files);

public sealed record BackupCaptureIssue(
    string Component,
    string Operation,
    string ErrorType,
    string Message);

public sealed record RightMenuBackupManifest(
    int FormatVersion,
    Guid BackupId,
    DateTimeOffset CreatedAt,
    string ToolVersion,
    string OperatingSystemVersion,
    string ProcessArchitecture,
    BackupPurpose Purpose,
    bool IsComplete,
    IReadOnlyList<BackedUpRegistration> Registrations,
    IReadOnlyList<RegistryKeySnapshot> RegistryKeys,
    IReadOnlyList<BackupCaptureIssue> Issues);

public sealed record BackupIntegrityManifest(
    int FormatVersion,
    string HashAlgorithm,
    IReadOnlyDictionary<string, string> Entries);

public sealed record BackupArtifactInfo(
    string Path,
    Guid BackupId,
    DateTimeOffset CreatedAt,
    long Size,
    int RegistrationCount,
    int RegistryKeyCount,
    bool IsComplete);

public enum RestoreConflictKind
{
    MissingKey,
    MissingValue,
    DifferentValue,
    ExtraCurrentValue,
    DifferentSecurityDescriptor,
}

public sealed record RestoreConflict(
    RegistrySource Source,
    string? ValueName,
    RestoreConflictKind Kind,
    string Message);

public sealed record RestorePreflightResult(
    Guid BackupId,
    bool IntegrityVerified,
    int KeysToCreate,
    int KeysToUpdate,
    int ValuesToWrite,
    IReadOnlyList<RestoreConflict> Conflicts,
    IReadOnlyList<BackupCaptureIssue> Issues);
