using System.IO.Compression;

namespace RightMenuCheck.Updater;

public interface ISafeZipExtractor
{
    Task ExtractAsync(
        string packagePath,
        string destinationDirectory,
        CancellationToken cancellationToken);

    Task ExtractAsync(
        Stream packageStream,
        string destinationDirectory,
        CancellationToken cancellationToken);
}

public sealed class SafeZipExtractor : ISafeZipExtractor
{
    private const long MaximumEntryBytes = 512L * 1024 * 1024;
    private const long MaximumTotalBytes = 2L * 1024 * 1024 * 1024;
    private const int UnixFileTypeMask = 0xF000;
    private const int UnixSymbolicLink = 0xA000;

    public async Task ExtractAsync(
        string packagePath,
        string destinationDirectory,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packagePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationDirectory);
        var package = Path.GetFullPath(packagePath);
        var destination = Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(destinationDirectory));
        if (!File.Exists(package) || Directory.Exists(destination))
        {
            throw new InvalidDataException("Update package or staging directory state is invalid.");
        }

        await using var packageStream = new FileStream(
            package,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 81920,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        await ExtractAsync(packageStream, destination, cancellationToken).ConfigureAwait(false);
    }

    public async Task ExtractAsync(
        Stream packageStream,
        string destinationDirectory,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(packageStream);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationDirectory);
        if (!packageStream.CanRead || !packageStream.CanSeek)
        {
            throw new InvalidDataException("Update package stream must be readable and seekable.");
        }

        var destination = Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(destinationDirectory));
        if (Directory.Exists(destination))
        {
            throw new InvalidDataException("Update staging directory already exists.");
        }

        packageStream.Position = 0;
        Directory.CreateDirectory(destination);
        var destinationPrefix = $"{destination}{Path.DirectorySeparatorChar}";
        long totalBytes = 0;
        try
        {
            using var archive = new ZipArchive(packageStream, ZipArchiveMode.Read, leaveOpen: true);
            foreach (var entry in archive.Entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                ValidateEntry(entry);
                var relativePath = entry.FullName.Replace('/', Path.DirectorySeparatorChar);
                var targetPath = Path.GetFullPath(Path.Combine(destination, relativePath));
                if (!targetPath.StartsWith(destinationPrefix, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException("Update archive contains a path outside staging.");
                }

                if (entry.FullName.EndsWith('/'))
                {
                    Directory.CreateDirectory(targetPath);
                    continue;
                }

                totalBytes = checked(totalBytes + entry.Length);
                if (entry.Length > MaximumEntryBytes || totalBytes > MaximumTotalBytes)
                {
                    throw new InvalidDataException("Update archive exceeds extraction limits.");
                }

                Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
                await using var input = entry.Open();
                await using var output = new FileStream(
                    targetPath,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    bufferSize: 81920,
                    FileOptions.Asynchronous | FileOptions.SequentialScan);
                await input.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
            }
        }
        catch
        {
            Directory.Delete(destination, recursive: true);
            throw;
        }
    }

    private static void ValidateEntry(ZipArchiveEntry entry)
    {
        var normalized = entry.FullName.Replace('\\', '/');
        if (string.IsNullOrWhiteSpace(normalized) ||
            normalized.StartsWith('/') ||
            normalized.Split('/').Any(static part => part is ".." or ".") ||
            Path.IsPathRooted(normalized) ||
            ((entry.ExternalAttributes >> 16) & UnixFileTypeMask) == UnixSymbolicLink)
        {
            throw new InvalidDataException("Update archive contains an unsafe entry.");
        }
    }
}
