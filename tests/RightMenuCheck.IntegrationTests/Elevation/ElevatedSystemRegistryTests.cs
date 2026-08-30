using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using RightMenuCheck.Core.Backup;
using RightMenuCheck.Core.Inventory;
using RightMenuCheck.Windows.Elevation;
using RightMenuCheck.Windows.Management;
using RightMenuCheck.Windows.Registry;

namespace RightMenuCheck.IntegrationTests.Elevation;

public sealed class ElevatedSystemRegistryTests
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        MaxDepth = 64,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    [Fact]
    [Trait("Category", "RequiresUac")]
    public async Task RestoreAndRemoveSyntheticMachineRegistrationThroughElevatedHelper()
    {
        if (!string.Equals(
                Environment.GetEnvironmentVariable("RIGHTMENUCHECK_RUN_ELEVATED_TESTS"),
                "1",
                StringComparison.Ordinal))
        {
            return;
        }

        var testName = $"RightMenuCheck.Elevated.Tests.{Guid.NewGuid():N}";
        var testRoot = new RegistrySource(
            RegistryHiveKind.LocalMachine,
            RegistryViewKind.Registry64,
            $"Software\\Classes\\{testName}");
        var registrationRoot = new RegistrySource(
            testRoot.Hive,
            testRoot.View,
            $"{testRoot.KeyPath}\\shell\\sample");
        var registrationId = $"elevated-{testName}";
        var cleanupRegistrationId = $"cleanup-{testName}";
        var manifest = CreateManifest(
            registrationId,
            cleanupRegistrationId,
            testRoot,
            registrationRoot);
        var backupPath = Path.Combine(
            Path.GetTempPath(),
            $"RightMenuCheck-Elevated-{Guid.NewGuid():N}.rmcbak");
        WriteBackup(backupPath, manifest);
        var client = new ElevatedHelperClient();
        var helperOptions = new ElevatedHelperOptions(
            GetBuiltHelperPath(),
            TimeSpan.FromMinutes(2));
        var registry = new SystemRegistryReader();

        try
        {
            var restorePlan = new RegistryRestorePlan(
                backupPath,
                RegistryRestoreMode.Exact,
                manifest,
                new RestorePreflightResult(
                    manifest.BackupId,
                    IntegrityVerified: true,
                    KeysToCreate: 2,
                    KeysToUpdate: 0,
                    ValuesToWrite: 2,
                    Conflicts: [],
                    Issues: []),
                new RegistryMutationPlan(
                    Guid.NewGuid(),
                    "Elevated synthetic restore",
                    backupPath,
                    [new RegistryMutation(
                        RegistryMutationKind.RestoreKeyTree,
                        testRoot,
                        ValueName: null,
                        Value: null,
                        manifest.RegistryKeys)]),
                CanExecute: true,
                BlockReason: null);

            var restoreResponse = await client.RunRestoreAsync(
                restorePlan,
                acceptConflicts: true,
                helperOptions,
                CancellationToken.None);

            Assert.True(
                restoreResponse.Outcome == ElevationOutcome.Succeeded,
                $"Restore failed: {restoreResponse.ErrorType} {restoreResponse.ErrorMessage} " +
                $"Mutation={restoreResponse.MutationResult}");
            Assert.True(registry.KeyExists(
                registrationRoot.Hive,
                registrationRoot.View,
                registrationRoot.KeyPath));
            Assert.Equal(
                "Synthetic Elevated",
                registry.GetValue(
                    registrationRoot.Hive,
                    registrationRoot.View,
                    registrationRoot.KeyPath,
                    "MUIVerb")?.Value);

            var artifact = new BackupArtifactInfo(
                backupPath,
                manifest.BackupId,
                manifest.CreatedAt,
                new FileInfo(backupPath).Length,
                RegistrationCount: 2,
                RegistryKeyCount: 2,
                IsComplete: true);
            var removal = new PreparedContextMenuRemoval(
                new ContextMenuRemovalPlan(
                    IsSupported: true,
                    IsNoChange: false,
                    RequiresElevation: true,
                    "Synthetic test removal",
                    BlockReason: null,
                    new RegistryMutation(
                        RegistryMutationKind.DeleteKeyTree,
                        testRoot,
                        ValueName: null,
                        Value: null,
                        KeyTree: null)),
                cleanupRegistrationId,
                artifact,
                new RegistryMutationPlan(
                    Guid.NewGuid(),
                    "Synthetic test removal",
                    backupPath,
                    [new RegistryMutation(
                        RegistryMutationKind.DeleteKeyTree,
                        testRoot,
                        ValueName: null,
                        Value: null,
                        KeyTree: null)]));

            var removalResponse = await client.RunRemovalAsync(
                removal,
                helperOptions,
                CancellationToken.None);

            Assert.True(
                removalResponse.Outcome == ElevationOutcome.Succeeded,
                $"Removal failed: {removalResponse.ErrorType} {removalResponse.ErrorMessage} " +
                $"Mutation={removalResponse.MutationResult}");
            Assert.False(registry.KeyExists(
                testRoot.Hive,
                testRoot.View,
                testRoot.KeyPath));
        }
        finally
        {
            File.Delete(backupPath);
        }
    }

    private static RightMenuBackupManifest CreateManifest(
        string registrationId,
        string cleanupRegistrationId,
        RegistrySource testRoot,
        RegistrySource registrationRoot)
    {
        var commandSource = new RegistrySource(
            registrationRoot.Hive,
            registrationRoot.View,
            $"{registrationRoot.KeyPath}\\command");
        return new RightMenuBackupManifest(
            RightMenuBackupFormat.CurrentVersion,
            Guid.NewGuid(),
            DateTimeOffset.UtcNow,
            "1.0.0.0",
            Environment.OSVersion.VersionString,
            "X64",
            BackupPurpose.Manual,
            IsComplete: true,
            Registrations:
            [
                new BackedUpRegistration(
                    registrationId,
                    "Synthetic Elevated",
                    ContextMenuRegistrationKind.StaticVerb,
                    ContextMenuTargetKind.FileType,
                    testRoot.KeyPath,
                    registrationRoot.KeyPath,
                    HandlerClsid: null,
                    registrationRoot,
                    Package: null,
                    Owner: null,
                    Files: []),
                new BackedUpRegistration(
                    cleanupRegistrationId,
                    "Synthetic Elevated Test Container",
                    ContextMenuRegistrationKind.StaticVerb,
                    ContextMenuTargetKind.FileType,
                    testRoot.KeyPath,
                    testRoot.KeyPath,
                    HandlerClsid: null,
                    testRoot,
                    Package: null,
                    Owner: null,
                    Files: []),
            ],
            RegistryKeys:
            [
                new RegistryKeySnapshot(
                    registrationRoot,
                    SecurityDescriptorSddl: null,
                    [new RegistryValueSnapshot(
                        "MUIVerb",
                        BackupRegistryValueKind.Text,
                        "Synthetic Elevated",
                        TextItems: null,
                        Base64Data: null,
                        NumericValue: null)]),
                new RegistryKeySnapshot(
                    commandSource,
                    SecurityDescriptorSddl: null,
                    [new RegistryValueSnapshot(
                        string.Empty,
                        BackupRegistryValueKind.Text,
                        "synthetic-elevated.exe \"%1\"",
                        TextItems: null,
                        Base64Data: null,
                        NumericValue: null)]),
            ],
            Issues: []);
    }

    private static void WriteBackup(string path, RightMenuBackupManifest manifest)
    {
        var manifestBytes = JsonSerializer.SerializeToUtf8Bytes(manifest, SerializerOptions);
        var integrity = new BackupIntegrityManifest(
            RightMenuBackupFormat.CurrentVersion,
            "SHA-256",
            new Dictionary<string, string>
            {
                [RightMenuBackupFormat.ManifestEntryName] =
                    Convert.ToHexString(SHA256.HashData(manifestBytes)),
            });
        var integrityBytes = JsonSerializer.SerializeToUtf8Bytes(integrity, SerializerOptions);
        using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        using var archive = new ZipArchive(file, ZipArchiveMode.Create);
        WriteEntry(archive, RightMenuBackupFormat.ManifestEntryName, manifestBytes);
        WriteEntry(archive, RightMenuBackupFormat.IntegrityEntryName, integrityBytes);
    }

    private static void WriteEntry(ZipArchive archive, string name, byte[] content)
    {
        var entry = archive.CreateEntry(name, CompressionLevel.Optimal);
        using var stream = entry.Open();
        stream.Write(content);
    }

    private static string GetBuiltHelperPath()
    {
        var output = new DirectoryInfo(AppContext.BaseDirectory);
        var binRoot = output.Parent?.Parent ??
                      throw new DirectoryNotFoundException("The shared artifacts/bin directory was not found.");
        return Path.Combine(
            binRoot.FullName,
            "RightMenuCheck.Elevated",
            "debug",
            "RightMenuCheck.Elevated.exe");
    }
}
