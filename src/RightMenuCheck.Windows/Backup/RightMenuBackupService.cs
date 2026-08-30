using System.IO.Compression;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using RightMenuCheck.Core.Backup;
using RightMenuCheck.Core.Inventory;
using RightMenuCheck.Core.Metadata;

namespace RightMenuCheck.Windows.Backup;

public sealed class RightMenuBackupService
{
    private readonly RegistrySnapshotReader _snapshotReader;

    public RightMenuBackupService(RegistrySnapshotReader snapshotReader)
    {
        _snapshotReader = snapshotReader ?? throw new ArgumentNullException(nameof(snapshotReader));
    }

    public async Task<BackupArtifactInfo> CreateAsync(
        string destinationPath,
        IReadOnlyList<ContextMenuRegistrationMetadata> registrations,
        BackupPurpose purpose,
        bool requireComplete,
        bool overwrite,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);
        ArgumentNullException.ThrowIfNull(registrations);
        if (registrations.Count == 0)
        {
            throw new ArgumentException("At least one registration must be selected.", nameof(registrations));
        }

        var fullPath = Path.GetFullPath(destinationPath);
        if (!Path.GetExtension(fullPath).Equals(".rmcbak", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Backup file must use the .rmcbak extension.", nameof(destinationPath));
        }

        var directory = Path.GetDirectoryName(fullPath) ??
                        throw new DirectoryNotFoundException("Backup destination directory is unavailable.");
        if (!Directory.Exists(directory))
        {
            throw new DirectoryNotFoundException(directory);
        }

        var snapshots = new List<RegistryKeySnapshot>();
        var issues = new List<BackupCaptureIssue>();
        var capturedRoots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var registration in registrations)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (registration.Registration.Source is not RegistryContextMenuSource registrySource)
            {
                continue;
            }

            var root = registrySource.Location;
            var identity = $"{root.Hive}|{root.View}|{root.KeyPath}";
            if (!capturedRoots.Add(identity))
            {
                continue;
            }

            var capture = _snapshotReader.Capture(root, cancellationToken);
            snapshots.AddRange(capture.Keys);
            issues.AddRange(capture.Issues);
        }

        var isComplete = issues.Count == 0;
        if (requireComplete && !isComplete)
        {
            throw new BackupIncompleteException(issues);
        }

        var createdAt = DateTimeOffset.UtcNow;
        var manifest = new RightMenuBackupManifest(
            RightMenuBackupFormat.CurrentVersion,
            Guid.NewGuid(),
            createdAt,
            GetToolVersion(),
            Environment.OSVersion.VersionString,
            RuntimeInformation.ProcessArchitecture.ToString(),
            purpose,
            isComplete,
            registrations.Select(CreateRegistrationReference).ToArray(),
            snapshots
                .GroupBy(static key =>
                    $"{key.Source.Hive}|{key.Source.View}|{key.Source.KeyPath}",
                    StringComparer.OrdinalIgnoreCase)
                .Select(static group => group.First())
                .OrderBy(static key => key.Source.KeyPath, StringComparer.OrdinalIgnoreCase)
                .ToArray(),
            issues);

        var manifestBytes = JsonSerializer.SerializeToUtf8Bytes(manifest, BackupJson.Options);
        var manifestHash = Convert.ToHexString(SHA256.HashData(manifestBytes));
        var integrity = new BackupIntegrityManifest(
            RightMenuBackupFormat.CurrentVersion,
            "SHA-256",
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [RightMenuBackupFormat.ManifestEntryName] = manifestHash,
            });
        var integrityBytes = JsonSerializer.SerializeToUtf8Bytes(integrity, BackupJson.Options);

        var temporaryPath = Path.Combine(
            directory,
            $".{Path.GetFileName(fullPath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            await WriteArchiveAsync(
                    temporaryPath,
                    manifestBytes,
                    integrityBytes,
                    createdAt,
                    cancellationToken)
                .ConfigureAwait(false);

            if (File.Exists(fullPath))
            {
                if (!overwrite)
                {
                    throw new IOException("Backup destination already exists.");
                }

                File.Replace(temporaryPath, fullPath, destinationBackupFileName: null);
            }
            else
            {
                File.Move(temporaryPath, fullPath);
            }

            var fileInfo = new FileInfo(fullPath);
            return new BackupArtifactInfo(
                fullPath,
                manifest.BackupId,
                manifest.CreatedAt,
                fileInfo.Length,
                manifest.Registrations.Count,
                manifest.RegistryKeys.Count,
                manifest.IsComplete);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private static async Task WriteArchiveAsync(
        string path,
        byte[] manifestBytes,
        byte[] integrityBytes,
        DateTimeOffset createdAt,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.CreateNew,
            FileAccess.ReadWrite,
            FileShare.None,
            bufferSize: 81920,
            FileOptions.Asynchronous | FileOptions.WriteThrough);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true);
        await WriteEntryAsync(
                archive,
                RightMenuBackupFormat.ManifestEntryName,
                manifestBytes,
                createdAt,
                cancellationToken)
            .ConfigureAwait(false);
        await WriteEntryAsync(
                archive,
                RightMenuBackupFormat.IntegrityEntryName,
                integrityBytes,
                createdAt,
                cancellationToken)
            .ConfigureAwait(false);
    }

    private static async Task WriteEntryAsync(
        ZipArchive archive,
        string entryName,
        byte[] content,
        DateTimeOffset createdAt,
        CancellationToken cancellationToken)
    {
        var entry = archive.CreateEntry(entryName, CompressionLevel.Optimal);
        entry.LastWriteTime = createdAt;
        await using var entryStream = entry.Open();
        await entryStream.WriteAsync(content, cancellationToken).ConfigureAwait(false);
    }

    private static BackedUpRegistration CreateRegistrationReference(
        ContextMenuRegistrationMetadata metadata)
    {
        var registration = metadata.Registration;
        RegistrySource? registrySource = registration.Source is RegistryContextMenuSource registry
            ? registry.Location
            : null;
        var package = registration.Source is PackageContextMenuSource packageSource
            ? new BackupPackageReference(
                packageSource.PackageName,
                packageSource.PackageFullName,
                packageSource.PackageFamilyName,
                packageSource.ApplicationId,
                packageSource.Architecture,
                packageSource.ManifestPath)
            : null;
        var owner = metadata.Owner is { } applicationOwner
            ? new BackupOwnerReference(
                applicationOwner.Kind,
                applicationOwner.Confidence,
                applicationOwner.DisplayName,
                applicationOwner.Publisher,
                applicationOwner.Version,
                applicationOwner.InstallLocation,
                applicationOwner.ProductCode,
                applicationOwner.PackageFullName,
                applicationOwner.IsSystemProtected)
            : null;
        var files = metadata.Components
            .Select(static component => component.Binary)
            .Where(static binary => binary is not null)
            .Cast<BinaryFileMetadata>()
            .GroupBy(static binary => binary.Path, StringComparer.OrdinalIgnoreCase)
            .Select(static group => group.First())
            .Select(static binary => new BackupFileReference(
                binary.Path,
                binary.Exists,
                binary.Size,
                binary.Sha256,
                binary.FileVersion,
                binary.Architecture,
                binary.Signature.Status,
                binary.Signature.PublisherName ?? binary.CompanyName))
            .ToArray();

        return new BackedUpRegistration(
            registration.Id,
            registration.DisplayName,
            registration.Kind,
            registration.TargetKind,
            registration.ClassPath,
            registration.RegistrationPath,
            registration.HandlerClsid,
            registrySource,
            package,
            owner,
            files);
    }

    private static string GetToolVersion() =>
        Assembly.GetEntryAssembly()?.GetName().Version?.ToString() ?? "0.0.0.0";
}
