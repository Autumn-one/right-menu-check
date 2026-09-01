namespace RightMenuCheck.Probe.Protocol;

public static class ProbeProtocol
{
    public const int CurrentVersion = 2;
    public const int NonceSizeBytes = 32;
    public const int MaximumMessageBytes = 1024 * 1024;
}

public enum ProbeOperation
{
    ClassicContextMenu,
    ExplorerCommand,
    AggregatedContextMenu,
}

public enum ProbeTargetKind
{
    File,
    Folder,
    FolderBackground,
    Drive,
    DesktopBackground,
}

public enum ProbeOutcome
{
    Success,
    NotApplicable,
    InvalidRequest,
    ActivationFailed,
    InitializationFailed,
    QueryFailed,
    TimedOut,
    Crashed,
    ProtocolError,
}

public enum ProbePhase
{
    ComActivation,
    ShellInitialization,
    MenuConstruction,
    GetTitle,
    GetIcon,
    GetState,
    EnumerateSubCommands,
    AggregateMenuCreation,
}

public sealed record ProbeRequest(
    int ProtocolVersion,
    Guid RequestId,
    string Nonce,
    ProbeOperation Operation,
    ProbeTargetKind TargetKind,
    string HandlerClsid,
    string TargetPath,
    string? RegistryClassPath);

public sealed record ProbePhaseTiming(
    ProbePhase Phase,
    double DurationMilliseconds,
    int HResult,
    bool Succeeded);

public sealed record ProbeError(
    string Type,
    string Message,
    int? HResult);

public enum ProbeMenuItemKind
{
    Command,
    Submenu,
    Separator,
    OwnerDrawn,
}

public sealed record ProbeMenuItem(
    ProbeMenuItemKind Kind,
    string? Title,
    int Depth,
    uint? CommandId,
    string? CanonicalVerb,
    string? HelpText,
    bool IsDisabled,
    bool IsHidden);

public sealed record ProbeMenuSnapshot(
    int CommandIdCount,
    IReadOnlyList<ProbeMenuItem> Items,
    bool Truncated,
    string? Limitation = null);

public sealed record ProbeResponse(
    int ProtocolVersion,
    Guid RequestId,
    string Nonce,
    ProbeOutcome Outcome,
    int WorkerProcessId,
    string WorkerArchitecture,
    DateTimeOffset StartedAt,
    double TotalDurationMilliseconds,
    IReadOnlyList<ProbePhaseTiming> Phases,
    ProbeMenuSnapshot? Menu,
    ProbeError? Error);
