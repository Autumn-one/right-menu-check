using System.Text.Json;
using RightMenuCheck.Core.Backup;
using RightMenuCheck.Core.Inventory;
using RightMenuCheck.Core.Metadata;
using RightMenuCheck.Windows.Backup;
using RightMenuCheck.Windows.Management;
using RightMenuCheck.Windows.Registry;

namespace RightMenuCheck.IntegrationTests.Management;

public sealed class SystemRegistryManagementTests
{
    [Fact]
    public async Task DisableAndEnableSyntheticCurrentUserVerbWithMandatoryBackups()
    {
        using var fixture = new ManagementFixture();
        var metadata = fixture.CreateMetadata();
        var service = fixture.CreateStateService();
        var disableBackup = fixture.CreateBackupPath("disable");
        var enableBackup = fixture.CreateBackupPath("enable");

        var disable = await service.PrepareAsync(
            metadata,
            ContextMenuStateAction.Disable,
            disableBackup,
            allowSystemProtected: false,
            overwriteBackup: false,
            CancellationToken.None);
        var disableResult = await service.ExecuteLocalAsync(disable, CancellationToken.None);

        Assert.True(disable.Backup?.IsComplete);
        Assert.True(disableResult.MutationResult?.Succeeded);
        Assert.NotNull(fixture.ReadValue("LegacyDisable"));
        Assert.Equal("Synthetic", fixture.ReadValue("MUIVerb")?.Value);

        var enable = await service.PrepareAsync(
            metadata,
            ContextMenuStateAction.Enable,
            enableBackup,
            allowSystemProtected: false,
            overwriteBackup: false,
            CancellationToken.None);
        var enableResult = await service.ExecuteLocalAsync(enable, CancellationToken.None);

        Assert.True(enable.Backup?.IsComplete);
        Assert.True(enableResult.MutationResult?.Succeeded);
        Assert.Null(fixture.ReadValue("LegacyDisable"));
        Assert.Equal("Synthetic", fixture.ReadValue("MUIVerb")?.Value);
        Assert.All(fixture.GetJournalPaths(), AssertCompletedJournal);
    }

    [Fact]
    public async Task ExactRestoreRemovesValueAddedAfterBackupAndKeepsOriginalTree()
    {
        using var fixture = new ManagementFixture();
        var metadata = fixture.CreateMetadata();
        var backupPath = fixture.CreateBackupPath("restore");
        await fixture.BackupService.CreateAsync(
            backupPath,
            [metadata],
            BackupPurpose.Manual,
            requireComplete: true,
            overwrite: false,
            CancellationToken.None);
        fixture.Writer.SetValue(
            fixture.RegistrationSource,
            new RegistryValueSnapshot(
                "LegacyDisable",
                BackupRegistryValueKind.Text,
                string.Empty,
                TextItems: null,
                Base64Data: null,
                NumericValue: null));
        Assert.NotNull(fixture.ReadValue("LegacyDisable"));
        var restore = fixture.CreateRestoreService();

        var plan = await restore.CreatePlanAsync(
            backupPath,
            RegistryRestoreMode.Exact,
            CancellationToken.None);

        Assert.True(plan.CanExecute, plan.BlockReason);
        Assert.Contains(plan.Preflight.Conflicts, conflict =>
            conflict.Kind == RestoreConflictKind.ExtraCurrentValue &&
            conflict.ValueName == "LegacyDisable");
        var result = await restore.ExecuteAsync(
            plan,
            acceptConflicts: true,
            CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Null(fixture.ReadValue("LegacyDisable"));
        Assert.Equal("Synthetic", fixture.ReadValue("MUIVerb")?.Value);
        Assert.Equal(
            "synthetic.exe \"%1\"",
            fixture.Registry.GetValue(
                fixture.RegistrationSource.Hive,
                fixture.RegistrationSource.View,
                $"{fixture.RegistrationSource.KeyPath}\\command",
                valueName: null)?.Value);
        AssertCompletedJournal(result.JournalPath);
    }

    private static void AssertCompletedJournal(string path)
    {
        using var document = JsonDocument.Parse(File.ReadAllBytes(path));
        Assert.Equal(
            "Completed",
            document.RootElement.GetProperty("state").GetString());
    }

    private sealed class ManagementFixture : IDisposable
    {
        private readonly string _testRootName = $"RightMenuCheck.Tests.{Guid.NewGuid():N}";
        private readonly string _temporaryDirectory = Path.Combine(
            Path.GetTempPath(),
            $"RightMenuCheck-Management-{Guid.NewGuid():N}");
        private readonly RegistrySource _testRoot;

        public ManagementFixture()
        {
            Registry = new SystemRegistryReader();
            SecurityReader = new SystemRegistrySecurityDescriptorReader();
            Writer = new SystemRegistryWriter();
            _testRoot = new RegistrySource(
                RegistryHiveKind.CurrentUser,
                RegistryViewKind.Registry64,
                $"Software\\Classes\\{_testRootName}");
            RegistrationSource = new RegistrySource(
                _testRoot.Hive,
                _testRoot.View,
                $"{_testRoot.KeyPath}\\shell\\sample");
            Directory.CreateDirectory(_temporaryDirectory);
            Writer.SetValue(
                RegistrationSource,
                new RegistryValueSnapshot(
                    "MUIVerb",
                    BackupRegistryValueKind.Text,
                    "Synthetic",
                    TextItems: null,
                    Base64Data: null,
                    NumericValue: null));
            Writer.SetValue(
                new RegistrySource(
                    RegistrationSource.Hive,
                    RegistrationSource.View,
                    $"{RegistrationSource.KeyPath}\\command"),
                new RegistryValueSnapshot(
                    string.Empty,
                    BackupRegistryValueKind.Text,
                    "synthetic.exe \"%1\"",
                    TextItems: null,
                    Base64Data: null,
                    NumericValue: null));
            SnapshotReader = new RegistrySnapshotReader(Registry, SecurityReader);
            BackupService = new RightMenuBackupService(SnapshotReader);
            TransactionExecutor = new RegistryTransactionExecutor(
                Registry,
                SnapshotReader,
                Writer,
                new FileRegistryActionJournalStore(JournalDirectory));
        }

        public SystemRegistryReader Registry { get; }

        public SystemRegistrySecurityDescriptorReader SecurityReader { get; }

        public SystemRegistryWriter Writer { get; }

        public RegistrySnapshotReader SnapshotReader { get; }

        public RightMenuBackupService BackupService { get; }

        public RegistryTransactionExecutor TransactionExecutor { get; }

        public RegistrySource RegistrationSource { get; }

        private string JournalDirectory => Path.Combine(_temporaryDirectory, "journals");

        public ContextMenuRegistrationMetadata CreateMetadata()
        {
            var registration = new ContextMenuRegistration
            {
                Id = _testRootName,
                Source = new RegistryContextMenuSource(RegistrationSource),
                ClassPath = _testRootName,
                RegistrationPath = RegistrationSource.KeyPath,
                CanonicalName = "sample",
                DisplayName = "Synthetic",
                TargetKind = ContextMenuTargetKind.FileType,
                Kind = ContextMenuRegistrationKind.StaticVerb,
                Command = "synthetic.exe \"%1\"",
            };
            return new ContextMenuRegistrationMetadata(
                registration,
                Components: [],
                Owner: null,
                Issues: []);
        }

        public ContextMenuStateActionService CreateStateService() => new(
            new ContextMenuStateActionPlanner(Registry),
            BackupService,
            TransactionExecutor);

        public RegistryRestoreService CreateRestoreService()
        {
            var reader = new RightMenuBackupReader();
            return new RegistryRestoreService(
                reader,
                new RestorePreflightService(reader, Registry, SecurityReader),
                TransactionExecutor);
        }

        public string CreateBackupPath(string name) =>
            Path.Combine(_temporaryDirectory, $"{name}.rmcbak");

        public RegistryValueData? ReadValue(string name) => Registry.GetValue(
            RegistrationSource.Hive,
            RegistrationSource.View,
            RegistrationSource.KeyPath,
            name);

        public string[] GetJournalPaths() => Directory.Exists(JournalDirectory)
            ? Directory.GetFiles(JournalDirectory, "*.json")
            : [];

        public void Dispose()
        {
            Writer.DeleteKeyTree(_testRoot);
            if (Directory.Exists(_temporaryDirectory))
            {
                Directory.Delete(_temporaryDirectory, recursive: true);
            }
        }
    }
}
