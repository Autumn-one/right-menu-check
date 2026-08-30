using RightMenuCheck.Core.Backup;
using RightMenuCheck.Core.Inventory;
using RightMenuCheck.Windows.Backup;
using RightMenuCheck.Windows.Registry;
using RightMenuCheck.Windows.Tests.Registry;

namespace RightMenuCheck.Windows.Tests.Backup;

public sealed class RestorePreflightServiceTests
{
    private const string KeyPath = "Software\\Classes\\*\\shell\\sample";
    private const RegistryViewKind View = RegistryViewKind.Registry64;

    [Fact]
    public async Task AnalyzeReportsChangedMissingAndExtraValuesWithoutWriting()
    {
        var current = new InMemoryRegistryReader(View);
        current.SetValue(
            RegistryHiveKind.CurrentUser,
            View,
            KeyPath,
            "Changed",
            "current");
        current.SetValue(
            RegistryHiveKind.CurrentUser,
            View,
            KeyPath,
            "Extra",
            "extra");
        var source = new RegistrySource(RegistryHiveKind.CurrentUser, View, KeyPath);
        var manifest = CreateManifest(source);
        var service = new RestorePreflightService(
            new FakeBackupReader(manifest),
            current,
            new FakeSecurityDescriptorReader());

        var result = await service.AnalyzeAsync("fixture.rmcbak", CancellationToken.None);

        Assert.True(result.IntegrityVerified);
        Assert.Equal(1, result.KeysToUpdate);
        Assert.Equal(2, result.ValuesToWrite);
        Assert.Contains(result.Conflicts, conflict =>
            conflict.Kind == RestoreConflictKind.DifferentValue && conflict.ValueName == "Changed");
        Assert.Contains(result.Conflicts, conflict =>
            conflict.Kind == RestoreConflictKind.MissingValue && conflict.ValueName == "Missing");
        Assert.Contains(result.Conflicts, conflict =>
            conflict.Kind == RestoreConflictKind.ExtraCurrentValue && conflict.ValueName == "Extra");
        Assert.Equal("current", current.GetValue(
            source.Hive,
            source.View,
            source.KeyPath,
            "Changed")?.Value);
    }

    [Fact]
    public async Task AnalyzeReportsMissingKeyAndAllValuesToWrite()
    {
        var source = new RegistrySource(RegistryHiveKind.CurrentUser, View, KeyPath);
        var service = new RestorePreflightService(
            new FakeBackupReader(CreateManifest(source)),
            new InMemoryRegistryReader(View),
            new FakeSecurityDescriptorReader());

        var result = await service.AnalyzeAsync("fixture.rmcbak", CancellationToken.None);

        Assert.Equal(1, result.KeysToCreate);
        Assert.Equal(2, result.ValuesToWrite);
        Assert.Contains(result.Conflicts, conflict => conflict.Kind == RestoreConflictKind.MissingKey);
    }

    private static RightMenuBackupManifest CreateManifest(RegistrySource source) => new(
        RightMenuBackupFormat.CurrentVersion,
        Guid.Parse("11111111-2222-3333-4444-555555555555"),
        DateTimeOffset.UnixEpoch,
        "1.0.0.0",
        "Windows",
        "X64",
        BackupPurpose.Manual,
        IsComplete: true,
        Registrations:
        [
            new BackedUpRegistration(
                "sample",
                "Sample",
                ContextMenuRegistrationKind.StaticVerb,
                ContextMenuTargetKind.File,
                "*",
                KeyPath,
                HandlerClsid: null,
                source,
                Package: null,
                Owner: null,
                Files: []),
        ],
        RegistryKeys:
        [
            new RegistryKeySnapshot(
                source,
                "O:BAG:BAD:(A;;KA;;;BA)",
                [
                    new RegistryValueSnapshot(
                        "Changed",
                        BackupRegistryValueKind.Text,
                        "backup",
                        TextItems: null,
                        Base64Data: null,
                        NumericValue: null),
                    new RegistryValueSnapshot(
                        "Missing",
                        BackupRegistryValueKind.DWord,
                        Text: null,
                        TextItems: null,
                        Base64Data: null,
                        NumericValue: 7),
                ]),
        ],
        Issues: []);

    private sealed class FakeBackupReader(RightMenuBackupManifest manifest)
        : IRightMenuBackupReader
    {
        public Task<RightMenuBackupManifest> ReadAsync(
            string backupPath,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(manifest);
    }
}
