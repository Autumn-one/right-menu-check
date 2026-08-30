namespace RightMenuCheck.Core.Inventory;

public enum ContextMenuTargetKind
{
    Unknown,
    File,
    FileType,
    AllFileSystemObjects,
    Folder,
    FolderBackground,
    Drive,
    DesktopBackground,
    LibraryFolder,
    LibraryBackground,
}

public enum ContextMenuRegistrationKind
{
    ClassicContextMenuHandler,
    StaticVerb,
    DelegateExecuteVerb,
    ExplorerCommand,
    CascadingVerb,
    PackagedExplorerCommand,
}

public enum PackageArchitectureKind
{
    Unknown,
    Neutral,
    X86,
    X64,
    Arm,
    Arm64,
    X86OnArm64,
}

public abstract record ContextMenuSource;

public sealed record RegistryContextMenuSource(RegistrySource Location) : ContextMenuSource;

public sealed record PackageContextMenuSource(
    string PackageName,
    string PackageFullName,
    string PackageFamilyName,
    string ApplicationId,
    PackageArchitectureKind Architecture,
    string ManifestPath) : ContextMenuSource;

[Flags]
public enum ContextMenuRegistrationStatus
{
    None = 0,
    Blocked = 1 << 0,
    LegacyDisabled = 1 << 1,
    ProgrammaticOnly = 1 << 2,
    ExtendedOnly = 1 << 3,
    CurrentUserOverridePresent = 1 << 4,
}

public sealed record ContextMenuRegistration
{
    public required string Id { get; init; }

    public required ContextMenuSource Source { get; init; }

    public required string ClassPath { get; init; }

    public required string RegistrationPath { get; init; }

    public required string CanonicalName { get; init; }

    public required string DisplayName { get; init; }

    public required ContextMenuTargetKind TargetKind { get; init; }

    public required ContextMenuRegistrationKind Kind { get; init; }

    public ContextMenuRegistrationStatus Status { get; init; }

    public string? ParentId { get; init; }

    public string? HandlerClsid { get; init; }

    public string? DelegateExecuteClsid { get; init; }

    public string? CommandStateHandlerClsid { get; init; }

    public string? Command { get; init; }

    public string? Icon { get; init; }

    public string? AppliesTo { get; init; }

    public string? ExtendedSubCommandsKey { get; init; }

    public IReadOnlyList<string> SubCommands { get; init; } = [];

    public bool IsVisibleByDefault =>
        (Status & (ContextMenuRegistrationStatus.Blocked |
                   ContextMenuRegistrationStatus.LegacyDisabled |
                   ContextMenuRegistrationStatus.ProgrammaticOnly |
                   ContextMenuRegistrationStatus.ExtendedOnly)) == 0;
}
