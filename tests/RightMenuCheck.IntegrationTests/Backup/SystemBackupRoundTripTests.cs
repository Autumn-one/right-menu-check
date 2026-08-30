using RightMenuCheck.Core.Backup;
using RightMenuCheck.Core.Inventory;
using RightMenuCheck.Core.Metadata;
using RightMenuCheck.Windows.Backup;
using RightMenuCheck.Windows.Registry;

namespace RightMenuCheck.IntegrationTests.Backup;

public sealed class SystemBackupRoundTripTests
{
    private const string RegistrationPath =
        "Software\\Classes\\*\\shellex\\ContextMenuHandlers\\Open With";

    [Fact]
    public async Task BackupAndPreflightRealMicrosoftRegistrationWithoutMutation()
    {
        var registryReader = new SystemRegistryReader();
        var source = new RegistrySource(
            RegistryHiveKind.LocalMachine,
            RegistryViewKind.Registry64,
            RegistrationPath);
        Assert.True(registryReader.KeyExists(source.Hive, source.View, source.KeyPath));
        var securityReader = new SystemRegistrySecurityDescriptorReader();
        var service = new RightMenuBackupService(new RegistrySnapshotReader(
            registryReader,
            securityReader));
        var backupPath = Path.Combine(
            Path.GetTempPath(),
            $"RightMenuCheck-System-{Guid.NewGuid():N}.rmcbak");

        try
        {
            var artifact = await service.CreateAsync(
                backupPath,
                [CreateMetadata(source)],
                BackupPurpose.Manual,
                requireComplete: true,
                overwrite: false,
                CancellationToken.None);
            var reader = new RightMenuBackupReader();
            var manifest = await reader.ReadAsync(backupPath, CancellationToken.None);
            var preflight = await new RestorePreflightService(
                    reader,
                    registryReader,
                    securityReader)
                .AnalyzeAsync(backupPath, CancellationToken.None);

            Assert.True(artifact.IsComplete);
            Assert.True(artifact.Size > 0);
            Assert.NotEmpty(manifest.RegistryKeys);
            Assert.Empty(manifest.Issues);
            Assert.True(preflight.IntegrityVerified);
            Assert.Equal(0, preflight.KeysToCreate);
            Assert.Equal(0, preflight.KeysToUpdate);
            Assert.Equal(0, preflight.ValuesToWrite);
            Assert.Empty(preflight.Conflicts);
            Assert.Empty(preflight.Issues);
            Assert.True(registryReader.KeyExists(source.Hive, source.View, source.KeyPath));
        }
        finally
        {
            File.Delete(backupPath);
        }
    }

    private static ContextMenuRegistrationMetadata CreateMetadata(RegistrySource source)
    {
        var registration = new ContextMenuRegistration
        {
            Id = "system-open-with",
            Source = new RegistryContextMenuSource(source),
            ClassPath = "*",
            RegistrationPath = source.KeyPath,
            CanonicalName = "Open With",
            DisplayName = "Open With",
            TargetKind = ContextMenuTargetKind.File,
            Kind = ContextMenuRegistrationKind.ClassicContextMenuHandler,
            HandlerClsid = "{09799AFB-AD67-11D1-ABCD-00C04FC30936}",
        };
        return new ContextMenuRegistrationMetadata(
            registration,
            Components: [],
            Owner: null,
            Issues: []);
    }
}
