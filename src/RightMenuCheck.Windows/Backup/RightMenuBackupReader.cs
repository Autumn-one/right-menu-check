using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using RightMenuCheck.Core.Backup;

namespace RightMenuCheck.Windows.Backup;

public interface IRightMenuBackupReader
{
    Task<RightMenuBackupManifest> ReadAsync(
        string backupPath,
        CancellationToken cancellationToken = default);
}

public sealed class RightMenuBackupReader : IRightMenuBackupReader
{
    private const long MaximumArchiveBytes = 256L * 1024 * 1024;
    private const long MaximumEntryBytes = 64L * 1024 * 1024;

    public async Task<RightMenuBackupManifest> ReadAsync(
        string backupPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(backupPath);
        var fullPath = Path.GetFullPath(backupPath);
        var fileInfo = new FileInfo(fullPath);
        if (!fileInfo.Exists)
        {
            throw new FileNotFoundException("Backup file was not found.", fullPath);
        }

        if (fileInfo.Length <= 0 || fileInfo.Length > MaximumArchiveBytes)
        {
            throw new InvalidDataException("Backup archive size is outside the allowed range.");
        }

        await using var stream = new FileStream(
            fullPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 81920,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true);
        var manifestEntry = GetSingleEntry(archive, RightMenuBackupFormat.ManifestEntryName);
        var integrityEntry = GetSingleEntry(archive, RightMenuBackupFormat.IntegrityEntryName);
        var manifestBytes = await ReadEntryAsync(manifestEntry, cancellationToken).ConfigureAwait(false);
        var integrityBytes = await ReadEntryAsync(integrityEntry, cancellationToken).ConfigureAwait(false);
        var integrity = JsonSerializer.Deserialize<BackupIntegrityManifest>(
                            integrityBytes,
                            BackupJson.Options) ??
                        throw new InvalidDataException("Backup integrity manifest is JSON null.");
        ValidateIntegrityManifest(integrity, manifestBytes);

        var manifest = JsonSerializer.Deserialize<RightMenuBackupManifest>(
                           manifestBytes,
                           BackupJson.Options) ??
                       throw new InvalidDataException("Backup manifest is JSON null.");
        ValidateManifest(manifest);
        return manifest;
    }

    private static ZipArchiveEntry GetSingleEntry(ZipArchive archive, string entryName)
    {
        var entries = archive.Entries
            .Where(entry => entry.FullName.Equals(entryName, StringComparison.Ordinal))
            .Take(2)
            .ToArray();
        return entries.Length switch
        {
            1 => entries[0],
            0 => throw new InvalidDataException($"Backup entry '{entryName}' is missing."),
            _ => throw new InvalidDataException($"Backup entry '{entryName}' is duplicated."),
        };
    }

    private static async Task<byte[]> ReadEntryAsync(
        ZipArchiveEntry entry,
        CancellationToken cancellationToken)
    {
        if (entry.Length <= 0 || entry.Length > MaximumEntryBytes)
        {
            throw new InvalidDataException($"Backup entry '{entry.FullName}' has an invalid size.");
        }

        await using var entryStream = entry.Open();
        using var destination = new MemoryStream(capacity: checked((int)entry.Length));
        await entryStream.CopyToAsync(destination, cancellationToken).ConfigureAwait(false);
        if (destination.Length != entry.Length)
        {
            throw new InvalidDataException($"Backup entry '{entry.FullName}' was truncated.");
        }

        return destination.ToArray();
    }

    private static void ValidateIntegrityManifest(
        BackupIntegrityManifest integrity,
        byte[] manifestBytes)
    {
        if (integrity.FormatVersion != RightMenuBackupFormat.CurrentVersion ||
            !integrity.HashAlgorithm.Equals("SHA-256", StringComparison.OrdinalIgnoreCase) ||
            !integrity.Entries.TryGetValue(
                RightMenuBackupFormat.ManifestEntryName,
                out var expectedHash))
        {
            throw new InvalidDataException("Backup integrity manifest is unsupported or incomplete.");
        }

        byte[] expectedBytes;
        try
        {
            expectedBytes = Convert.FromHexString(expectedHash);
        }
        catch (FormatException exception)
        {
            throw new InvalidDataException("Backup manifest checksum is not valid hexadecimal.", exception);
        }

        var actualBytes = SHA256.HashData(manifestBytes);
        if (expectedBytes.Length != actualBytes.Length ||
            !CryptographicOperations.FixedTimeEquals(expectedBytes, actualBytes))
        {
            throw new InvalidDataException("Backup manifest checksum verification failed.");
        }
    }

    private static void ValidateManifest(RightMenuBackupManifest manifest)
    {
        if (manifest.FormatVersion != RightMenuBackupFormat.CurrentVersion)
        {
            throw new InvalidDataException("Backup format version is unsupported.");
        }

        if (manifest.BackupId == Guid.Empty || manifest.Registrations.Count == 0)
        {
            throw new InvalidDataException("Backup manifest identity or registration list is invalid.");
        }

        if (manifest.RegistryKeys.Any(static key => string.IsNullOrWhiteSpace(key.Source.KeyPath)))
        {
            throw new InvalidDataException("Backup manifest contains an invalid registry path.");
        }
    }
}
