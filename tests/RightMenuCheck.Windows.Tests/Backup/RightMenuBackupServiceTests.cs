using System.IO.Compression;
using System.Text;
using RightMenuCheck.Core.Backup;
using RightMenuCheck.Core.Inventory;
using RightMenuCheck.Core.Metadata;
using RightMenuCheck.Windows.Backup;
using RightMenuCheck.Windows.Registry;
using RightMenuCheck.Windows.Tests.Registry;

namespace RightMenuCheck.Windows.Tests.Backup;

public sealed class RightMenuBackupServiceTests
{
    private const RegistryViewKind View = RegistryViewKind.Registry64;
    private const string RootPath = "Software\\Classes\\*\\shell\\sample";

    [Fact]
    public async Task CreateAndReadRoundTripManifestRegistryOwnerAndFileEvidence()
    {
        var registry = CreateRegistry("sample.exe \"%1\"");
        var service = CreateService(registry);
        var backupPath = CreateTemporaryBackupPath();

        try
        {
            var artifact = await service.CreateAsync(
                backupPath,
                [CreateMetadata()],
                BackupPurpose.Manual,
                requireComplete: true,
                overwrite: false,
                CancellationToken.None);
            var manifest = await new RightMenuBackupReader().ReadAsync(
                backupPath,
                CancellationToken.None);

            Assert.True(artifact.IsComplete);
            Assert.True(artifact.Size > 0);
            Assert.Equal(artifact.BackupId, manifest.BackupId);
            Assert.Equal(RightMenuBackupFormat.CurrentVersion, manifest.FormatVersion);
            Assert.Single(manifest.Registrations);
            Assert.Equal(2, manifest.RegistryKeys.Count);
            var registration = manifest.Registrations[0];
            Assert.Equal("Sample App", registration.Owner?.DisplayName);
            Assert.Equal("ABC123", Assert.Single(registration.Files).Sha256);
            Assert.Empty(manifest.Issues);
        }
        finally
        {
            File.Delete(backupPath);
        }
    }

    [Fact]
    public async Task ReadRejectsManifestModifiedWithoutIntegrityUpdate()
    {
        var backupPath = CreateTemporaryBackupPath();
        try
        {
            await CreateService(CreateRegistry("sample.exe")).CreateAsync(
                backupPath,
                [CreateMetadata()],
                BackupPurpose.Manual,
                requireComplete: true,
                overwrite: false,
                CancellationToken.None);
            using (var stream = new FileStream(
                       backupPath,
                       FileMode.Open,
                       FileAccess.ReadWrite,
                       FileShare.None))
            using (var archive = new ZipArchive(stream, ZipArchiveMode.Update))
            {
                archive.GetEntry(RightMenuBackupFormat.ManifestEntryName)!.Delete();
                var replacement = archive.CreateEntry(RightMenuBackupFormat.ManifestEntryName);
                using var writer = new StreamWriter(
                    replacement.Open(),
                    new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
                writer.Write("{}");
            }

            var exception = await Assert.ThrowsAsync<InvalidDataException>(() =>
                new RightMenuBackupReader()
                    .ReadAsync(backupPath, CancellationToken.None));
            Assert.Contains("checksum", exception.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            File.Delete(backupPath);
        }
    }

    [Fact]
    public async Task CreateRefusesIncompleteSnapshotWhenRequired()
    {
        var service = CreateService(new InMemoryRegistryReader(View));
        var backupPath = CreateTemporaryBackupPath();

        await Assert.ThrowsAsync<BackupIncompleteException>(() => service.CreateAsync(
            backupPath,
            [CreateMetadata()],
            BackupPurpose.BeforeRemove,
            requireComplete: true,
            overwrite: false,
            CancellationToken.None));
        Assert.False(File.Exists(backupPath));
    }

    private static RightMenuBackupService CreateService(InMemoryRegistryReader registry) =>
        new(new RegistrySnapshotReader(registry, new FakeSecurityDescriptorReader()));

    private static InMemoryRegistryReader CreateRegistry(string command)
    {
        var registry = new InMemoryRegistryReader(View);
        registry.SetValue(
            RegistryHiveKind.LocalMachine,
            View,
            RootPath,
            "MUIVerb",
            "Sample");
        registry.SetValue(
            RegistryHiveKind.LocalMachine,
            View,
            $"{RootPath}\\command",
            valueName: null,
            command);
        return registry;
    }

    private static ContextMenuRegistrationMetadata CreateMetadata()
    {
        var registration = new ContextMenuRegistration
        {
            Id = "sample-registration",
            Source = new RegistryContextMenuSource(new RegistrySource(
                RegistryHiveKind.LocalMachine,
                View,
                RootPath)),
            ClassPath = "*",
            RegistrationPath = RootPath,
            CanonicalName = "sample",
            DisplayName = "Sample",
            TargetKind = ContextMenuTargetKind.File,
            Kind = ContextMenuRegistrationKind.StaticVerb,
            Command = "sample.exe \"%1\"",
        };
        var binary = new BinaryFileMetadata(
            "C:\\Apps\\Sample\\sample.exe",
            Exists: true,
            Size: 100,
            DateTimeOffset.UnixEpoch,
            "ABC123",
            BinaryArchitectureKind.X64,
            IsManaged: false,
            FileVersion: "1.0.0.0",
            ProductVersion: "1.0.0.0",
            ProductName: "Sample",
            Description: "Sample",
            CompanyName: "Sample Publisher",
            new AuthenticodeSignatureMetadata(
                SignatureVerificationStatus.Valid,
                "Sample Publisher",
                Subject: "CN=Sample Publisher",
                Issuer: "CN=Test",
                Thumbprint: "0011",
                DateTimeOffset.UnixEpoch,
                DateTimeOffset.UnixEpoch.AddYears(1),
                TrustErrorCode: null),
            Issues: []);
        var component = new HandlerComponentMetadata(
            HandlerComponentRole.ContextMenuHandler,
            "{11111111-2222-3333-4444-555555555555}",
            ComServer: null,
            binary,
            Issues: []);
        var owner = new ApplicationOwnerMetadata(
            ApplicationOwnerKind.InstalledApplication,
            OwnershipConfidence.High,
            "Sample App",
            "Sample Publisher",
            "1.0.0",
            "C:\\Apps\\Sample",
            ProductCode: null,
            PackageFullName: null,
            UninstallRegistrySource: null,
            UninstallKeyName: "Sample",
            UninstallString: "uninstall.exe",
            QuietUninstallString: null,
            IsWindowsInstaller: false,
            IsSystemProtected: false,
            "test");
        return new ContextMenuRegistrationMetadata(
            registration,
            [component],
            owner,
            Issues: []);
    }

    private static string CreateTemporaryBackupPath() =>
        Path.Combine(Path.GetTempPath(), $"RightMenuCheck-{Guid.NewGuid():N}.rmcbak");
}
