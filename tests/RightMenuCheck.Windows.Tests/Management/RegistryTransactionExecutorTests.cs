using RightMenuCheck.Core.Backup;
using RightMenuCheck.Core.Inventory;
using RightMenuCheck.Windows.Backup;
using RightMenuCheck.Windows.Management;
using RightMenuCheck.Windows.Registry;
using RightMenuCheck.Windows.Tests.Backup;
using RightMenuCheck.Windows.Tests.Registry;

namespace RightMenuCheck.Windows.Tests.Management;

public sealed class RegistryTransactionExecutorTests
{
    private const string KeyPath = "Software\\Classes\\RightMenuCheck.Test\\shell\\sample";
    private static readonly RegistrySource Source = new(
        RegistryHiveKind.CurrentUser,
        RegistryViewKind.Registry64,
        KeyPath);

    [Fact]
    public async Task ExecuteWritesValuesAndCompletesJournal()
    {
        var registry = CreateRegistry();
        var journal = new InMemoryJournalStore();
        var executor = CreateExecutor(registry, new InMemoryRegistryWriter(registry), journal);
        var plan = CreatePlan(
            SetText("Existing", "new"),
            SetText("Added", "added"));

        var result = await executor.ExecuteAsync(plan, CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.False(result.RolledBack);
        Assert.Equal(2, result.AppliedMutationCount);
        Assert.Equal("new", ReadText(registry, "Existing"));
        Assert.Equal("added", ReadText(registry, "Added"));
        Assert.Equal(RegistryActionJournalState.Completed, journal.LastJournal?.State);
    }

    [Fact]
    public async Task ExecuteRestoresExactOriginalTreeWhenLaterMutationFails()
    {
        var registry = CreateRegistry();
        var journal = new InMemoryJournalStore();
        var writer = new ThrowingRegistryWriter(
            new InMemoryRegistryWriter(registry),
            throwOnCall: 2);
        var executor = CreateExecutor(registry, writer, journal);
        var plan = CreatePlan(
            SetText("Existing", "changed"),
            SetText("Added", "added"));

        var result = await executor.ExecuteAsync(plan, CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.True(result.RolledBack);
        Assert.Equal(1, result.AppliedMutationCount);
        Assert.Equal("original", ReadText(registry, "Existing"));
        Assert.Null(registry.GetValue(Source.Hive, Source.View, Source.KeyPath, "Added"));
        Assert.Equal("preserved", ReadText(registry, "Preserved"));
        Assert.Equal(RegistryActionJournalState.RolledBack, journal.LastJournal?.State);
    }

    [Fact]
    public async Task ExecuteRejectsMutationOutsideContextMenuAllowlist()
    {
        var registry = CreateRegistry();
        var executor = CreateExecutor(
            registry,
            new InMemoryRegistryWriter(registry),
            new InMemoryJournalStore());
        var unsafeSource = new RegistrySource(
            RegistryHiveKind.CurrentUser,
            RegistryViewKind.Registry64,
            "Software\\RightMenuCheck\\NotClasses");
        var plan = new RegistryMutationPlan(
            Guid.NewGuid(),
            "unsafe",
            "backup.rmcbak",
            [new RegistryMutation(
                RegistryMutationKind.SetValue,
                unsafeSource,
                "Value",
                CreateTextValue("Value", "data"),
                KeyTree: null)]);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            executor.ExecuteAsync(plan, CancellationToken.None));
        Assert.False(registry.KeyExists(
            unsafeSource.Hive,
            unsafeSource.View,
            unsafeSource.KeyPath));
    }

    private static RegistryTransactionExecutor CreateExecutor(
        InMemoryRegistryReader registry,
        IRegistryWriter writer,
        IRegistryActionJournalStore journal) =>
        new(
            registry,
            new RegistrySnapshotReader(registry, new FakeSecurityDescriptorReader()),
            writer,
            journal);

    private static InMemoryRegistryReader CreateRegistry()
    {
        var registry = new InMemoryRegistryReader(Source.View);
        registry.SetValue(Source.Hive, Source.View, Source.KeyPath, "Existing", "original");
        registry.SetValue(Source.Hive, Source.View, Source.KeyPath, "Preserved", "preserved");
        return registry;
    }

    private static RegistryMutationPlan CreatePlan(params RegistryMutation[] mutations) => new(
        Guid.NewGuid(),
        "test mutation",
        "backup.rmcbak",
        mutations);

    private static RegistryMutation SetText(string name, string text) => new(
        RegistryMutationKind.SetValue,
        Source,
        name,
        CreateTextValue(name, text),
        KeyTree: null);

    private static RegistryValueSnapshot CreateTextValue(string name, string text) => new(
        name,
        BackupRegistryValueKind.Text,
        text,
        TextItems: null,
        Base64Data: null,
        NumericValue: null);

    private static string? ReadText(InMemoryRegistryReader registry, string name) =>
        registry.GetValue(Source.Hive, Source.View, Source.KeyPath, name)?.Value as string;

    private sealed class InMemoryRegistryWriter(InMemoryRegistryReader registry) : IRegistryWriter
    {
        public void SetValue(RegistrySource source, RegistryValueSnapshot value)
        {
            var (nativeValue, kind) = ConvertValue(value);
            registry.SetValue(
                source.Hive,
                source.View,
                source.KeyPath,
                value.Name,
                nativeValue,
                kind);
        }

        public void DeleteValue(RegistrySource source, string valueName) =>
            registry.DeleteValue(source.Hive, source.View, source.KeyPath, valueName);

        public void DeleteKeyTree(RegistrySource source) =>
            registry.DeleteKeyTree(source.Hive, source.View, source.KeyPath);

        public void RestoreKeyTree(
            RegistrySource root,
            IReadOnlyList<RegistryKeySnapshot> keys)
        {
            foreach (var key in keys.OrderBy(static key => key.Source.KeyPath.Count(
                         static character => character == '\\')))
            {
                registry.AddKey(key.Source.Hive, key.Source.View, key.Source.KeyPath);
                foreach (var value in key.Values)
                {
                    SetValue(key.Source, value);
                }
            }
        }

        private static (object? Value, RegistryValueDataKind Kind) ConvertValue(
            RegistryValueSnapshot value) => value.Kind switch
        {
            BackupRegistryValueKind.Text => (value.Text, RegistryValueDataKind.Text),
            BackupRegistryValueKind.ExpandableText =>
                (value.Text, RegistryValueDataKind.ExpandableText),
            BackupRegistryValueKind.MultiText =>
                (value.TextItems?.ToArray(), RegistryValueDataKind.MultiText),
            BackupRegistryValueKind.Binary =>
                (Convert.FromBase64String(value.Base64Data!), RegistryValueDataKind.Binary),
            BackupRegistryValueKind.None =>
                (Convert.FromBase64String(value.Base64Data!), RegistryValueDataKind.None),
            BackupRegistryValueKind.DWord =>
                (checked((int)value.NumericValue!.Value), RegistryValueDataKind.DWord),
            BackupRegistryValueKind.QWord =>
                (value.NumericValue, RegistryValueDataKind.QWord),
            _ => throw new ArgumentOutOfRangeException(nameof(value), value.Kind, null),
        };
    }

    private sealed class ThrowingRegistryWriter(IRegistryWriter inner, int throwOnCall)
        : IRegistryWriter
    {
        private int _callCount;

        public void SetValue(RegistrySource source, RegistryValueSnapshot value)
        {
            ThrowIfRequired();
            inner.SetValue(source, value);
        }

        public void DeleteValue(RegistrySource source, string valueName)
        {
            ThrowIfRequired();
            inner.DeleteValue(source, valueName);
        }

        public void DeleteKeyTree(RegistrySource source) => inner.DeleteKeyTree(source);

        public void RestoreKeyTree(
            RegistrySource root,
            IReadOnlyList<RegistryKeySnapshot> keys) =>
            inner.RestoreKeyTree(root, keys);

        private void ThrowIfRequired()
        {
            _callCount++;
            if (_callCount == throwOnCall)
            {
                throw new IOException("Injected mutation failure.");
            }
        }
    }

    private sealed class InMemoryJournalStore : IRegistryActionJournalStore
    {
        public RegistryActionJournal? LastJournal { get; private set; }

        public Task<string> WriteAsync(
            RegistryActionJournal journal,
            CancellationToken cancellationToken = default)
        {
            LastJournal = journal;
            return Task.FromResult($"{journal.OperationId:D}.json");
        }
    }
}
