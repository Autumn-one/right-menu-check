using RightMenuCheck.Core.Backup;
using RightMenuCheck.Windows.Management;

namespace RightMenuCheck.Windows.Elevation;

public static class ElevationProtocol
{
    public const int CurrentVersion = 1;
    public const int MaximumMessageBytes = 64 * 1024 * 1024;
}

public enum ElevationOperation
{
    StateChange,
    Restore,
    RemoveRegistration,
}

public enum ElevationOutcome
{
    Succeeded,
    Failed,
    InvalidRequest,
    NotElevated,
    Cancelled,
    TimedOut,
}

public sealed record ElevationRequest(
    int ProtocolVersion,
    Guid RequestId,
    string Nonce,
    ElevationOperation Operation,
    Guid ExpectedBackupId,
    string BackupPath,
    RegistryMutationPlan? StateMutationPlan,
    RegistryRestoreMode? RestoreMode,
    bool AcceptRestoreConflicts,
    string? RemovalRegistrationId = null);

public sealed record ElevationResponse(
    int ProtocolVersion,
    Guid RequestId,
    string Nonce,
    ElevationOutcome Outcome,
    int HelperProcessId,
    RegistryMutationResult? MutationResult,
    string? ErrorType,
    string? ErrorMessage);

public sealed record ElevationValidationResult(bool IsValid, string? Error);
