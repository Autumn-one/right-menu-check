using RightMenuCheck.Core.Backup;
using RightMenuCheck.Core.Inventory;

namespace RightMenuCheck.Windows.Management;

public enum RegistryMutationKind
{
    SetValue,
    DeleteValue,
    DeleteKeyTree,
    RestoreKeyTree,
}

public sealed record RegistryMutation(
    RegistryMutationKind Kind,
    RegistrySource Source,
    string? ValueName,
    RegistryValueSnapshot? Value,
    IReadOnlyList<RegistryKeySnapshot>? KeyTree);

public sealed record RegistryMutationPlan(
    Guid OperationId,
    string OperationName,
    string BackupPath,
    IReadOnlyList<RegistryMutation> Mutations);

public enum RegistryActionJournalState
{
    Prepared,
    Applying,
    Completed,
    RolledBack,
    RollbackFailed,
}

public sealed record RegistryRollbackState(
    RegistrySource Root,
    bool Existed,
    IReadOnlyList<RegistryKeySnapshot> Keys);

public sealed record RegistryActionJournal(
    int FormatVersion,
    Guid OperationId,
    string OperationName,
    string BackupPath,
    DateTimeOffset StartedAt,
    DateTimeOffset UpdatedAt,
    RegistryActionJournalState State,
    IReadOnlyList<RegistryMutation> Mutations,
    IReadOnlyList<RegistryRollbackState> RollbackStates,
    string? ErrorType,
    string? ErrorMessage);

public sealed record RegistryMutationResult(
    Guid OperationId,
    bool Succeeded,
    bool RolledBack,
    int AppliedMutationCount,
    string JournalPath,
    string? ErrorType,
    string? ErrorMessage);
