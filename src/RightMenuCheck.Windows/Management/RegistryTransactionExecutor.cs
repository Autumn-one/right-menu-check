using RightMenuCheck.Core.Backup;
using RightMenuCheck.Core.Inventory;
using RightMenuCheck.Windows.Backup;
using RightMenuCheck.Windows.Registry;

namespace RightMenuCheck.Windows.Management;

public sealed class RegistryTransactionExecutor
{
    private const int JournalFormatVersion = 1;
    private static readonly SemaphoreSlim MutationLock = new(1, 1);
    private readonly IRegistryReader _registryReader;
    private readonly RegistrySnapshotReader _snapshotReader;
    private readonly IRegistryWriter _writer;
    private readonly IRegistryActionJournalStore _journalStore;

    public RegistryTransactionExecutor(
        IRegistryReader registryReader,
        RegistrySnapshotReader snapshotReader,
        IRegistryWriter writer,
        IRegistryActionJournalStore journalStore)
    {
        _registryReader = registryReader ?? throw new ArgumentNullException(nameof(registryReader));
        _snapshotReader = snapshotReader ?? throw new ArgumentNullException(nameof(snapshotReader));
        _writer = writer ?? throw new ArgumentNullException(nameof(writer));
        _journalStore = journalStore ?? throw new ArgumentNullException(nameof(journalStore));
    }

    public async Task<RegistryMutationResult> ExecuteAsync(
        RegistryMutationPlan plan,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (plan.OperationId == Guid.Empty || plan.Mutations.Count == 0)
        {
            throw new ArgumentException("Registry mutation plan is empty or has no identity.", nameof(plan));
        }

        foreach (var mutation in plan.Mutations)
        {
            RegistryMutationPolicy.Validate(mutation.Source);
        }

        await MutationLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var rollbackStates = CaptureRollbackStates(plan.Mutations, cancellationToken);
            var now = DateTimeOffset.UtcNow;
            var journal = new RegistryActionJournal(
                JournalFormatVersion,
                plan.OperationId,
                plan.OperationName,
                plan.BackupPath,
                now,
                now,
                RegistryActionJournalState.Prepared,
                plan.Mutations,
                rollbackStates,
                ErrorType: null,
                ErrorMessage: null);
            var journalPath = await _journalStore
                .WriteAsync(journal, cancellationToken)
                .ConfigureAwait(false);

            cancellationToken.ThrowIfCancellationRequested();
            journal = journal with
            {
                State = RegistryActionJournalState.Applying,
                UpdatedAt = DateTimeOffset.UtcNow,
            };
            journalPath = await _journalStore
                .WriteAsync(journal, CancellationToken.None)
                .ConfigureAwait(false);

            var applied = 0;
            try
            {
                foreach (var mutation in plan.Mutations)
                {
                    ApplyMutation(mutation);
                    applied++;
                }

                journal = journal with
                {
                    State = RegistryActionJournalState.Completed,
                    UpdatedAt = DateTimeOffset.UtcNow,
                };
                journalPath = await _journalStore
                    .WriteAsync(journal, CancellationToken.None)
                    .ConfigureAwait(false);
                return new RegistryMutationResult(
                    plan.OperationId,
                    Succeeded: true,
                    RolledBack: false,
                    applied,
                    journalPath,
                    ErrorType: null,
                    ErrorMessage: null);
            }
#pragma warning disable CA1031
            catch (Exception exception) when (exception is not OperationCanceledException)
#pragma warning restore CA1031
            {
                try
                {
                    RollBack(rollbackStates);
                    journal = journal with
                    {
                        State = RegistryActionJournalState.RolledBack,
                        UpdatedAt = DateTimeOffset.UtcNow,
                        ErrorType = exception.GetType().Name,
                        ErrorMessage = exception.Message,
                    };
                    journalPath = await _journalStore
                        .WriteAsync(journal, CancellationToken.None)
                        .ConfigureAwait(false);
                    return new RegistryMutationResult(
                        plan.OperationId,
                        Succeeded: false,
                        RolledBack: true,
                        applied,
                        journalPath,
                        exception.GetType().Name,
                        exception.Message);
                }
#pragma warning disable CA1031
                catch (Exception rollbackException)
#pragma warning restore CA1031
                {
                    journal = journal with
                    {
                        State = RegistryActionJournalState.RollbackFailed,
                        UpdatedAt = DateTimeOffset.UtcNow,
                        ErrorType = rollbackException.GetType().Name,
                        ErrorMessage =
                            $"Original: {exception.Message} Rollback: {rollbackException.Message}",
                    };
                    journalPath = await _journalStore
                        .WriteAsync(journal, CancellationToken.None)
                        .ConfigureAwait(false);
                    throw new InvalidOperationException(
                        $"Registry mutation and rollback both failed. Journal: {journalPath}",
                        new AggregateException(exception, rollbackException));
                }
            }
        }
        finally
        {
            MutationLock.Release();
        }
    }

    private RegistryRollbackState[] CaptureRollbackStates(
        IReadOnlyList<RegistryMutation> mutations,
        CancellationToken cancellationToken)
    {
        var roots = mutations
            .Select(static mutation => mutation.Source)
            .GroupBy(static source =>
                $"{source.Hive}|{source.View}|{source.KeyPath}",
                StringComparer.OrdinalIgnoreCase)
            .Select(static group => group.First())
            .ToArray();
        var states = new List<RegistryRollbackState>(roots.Length);

        foreach (var root in roots)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var exists = _registryReader.KeyExists(root.Hive, root.View, root.KeyPath);
            if (!exists)
            {
                states.Add(new RegistryRollbackState(root, Existed: false, Keys: []));
                continue;
            }

            var capture = _snapshotReader.Capture(root, cancellationToken);
            if (!capture.IsComplete)
            {
                throw new BackupIncompleteException(capture.Issues);
            }

            states.Add(new RegistryRollbackState(root, Existed: true, capture.Keys));
        }

        return states.ToArray();
    }

    private void ApplyMutation(RegistryMutation mutation)
    {
        switch (mutation.Kind)
        {
            case RegistryMutationKind.SetValue:
                _writer.SetValue(
                    mutation.Source,
                    mutation.Value ?? throw new InvalidOperationException("SetValue has no value."));
                break;
            case RegistryMutationKind.DeleteValue:
                _writer.DeleteValue(
                    mutation.Source,
                    mutation.ValueName ?? throw new InvalidOperationException("DeleteValue has no name."));
                break;
            case RegistryMutationKind.DeleteKeyTree:
                _writer.DeleteKeyTree(mutation.Source);
                break;
            case RegistryMutationKind.RestoreKeyTree:
                _writer.RestoreKeyTree(
                    mutation.Source,
                    mutation.KeyTree ?? throw new InvalidOperationException("RestoreKeyTree has no snapshot."));
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(mutation), mutation.Kind, null);
        }
    }

    private void RollBack(RegistryRollbackState[] rollbackStates)
    {
        for (var index = rollbackStates.Length - 1; index >= 0; index--)
        {
            var state = rollbackStates[index];
            _writer.DeleteKeyTree(state.Root);
            if (state.Existed)
            {
                _writer.RestoreKeyTree(state.Root, state.Keys);
            }
        }
    }
}
