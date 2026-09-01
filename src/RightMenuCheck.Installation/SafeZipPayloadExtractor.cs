using System.IO.Compression;

namespace RightMenuCheck.Installation;

public static class SafeZipPayloadExtractor
{
    private const int BufferSize = 81920;
    private const int MaximumEntries = 20_000;
    private const long MaximumEntryBytes = 1024L * 1024 * 1024;
    private const long MaximumTotalBytes = 2L * 1024 * 1024 * 1024;
    private static readonly HashSet<string> ReservedDeviceNames = new(
        [
            "CON", "PRN", "AUX", "NUL", "CLOCK$",
            "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
            "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
        ],
        StringComparer.OrdinalIgnoreCase);

    public static async Task ExtractAsync(
        string packagePath,
        string destinationDirectory,
        IProgress<double>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packagePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationDirectory);
        var package = Path.GetFullPath(packagePath);
        var destination = Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(destinationDirectory));
        if (!File.Exists(package) || Directory.Exists(destination))
        {
            throw new InvalidDataException("Installer package or staging directory state is invalid.");
        }

        Directory.CreateDirectory(destination);
        await using var stream = new FileStream(
            package,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            BufferSize,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: false);
        if (archive.Entries.Count is <= 0 or > MaximumEntries)
        {
            throw new InvalidDataException("Installer package entry count is invalid.");
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long declaredTotal = 0;
        foreach (var entry in archive.Entries)
        {
            ValidateEntry(entry);
            if (entry.Length > MaximumEntryBytes ||
                declaredTotal > MaximumTotalBytes - entry.Length)
            {
                throw new InvalidDataException("Installer package expands beyond the allowed size.");
            }

            declaredTotal += entry.Length;
            var relative = entry.FullName.Replace('/', Path.DirectorySeparatorChar);
            var target = Path.GetFullPath(Path.Combine(destination, relative));
            if (!target.StartsWith(
                    destination + Path.DirectorySeparatorChar,
                    StringComparison.OrdinalIgnoreCase) ||
                !seen.Add(target))
            {
                throw new InvalidDataException(
                    "Installer package contains an unsafe or duplicate path.");
            }
        }

        long extractedTotal = 0;
        foreach (var entry in archive.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relative = entry.FullName.Replace('/', Path.DirectorySeparatorChar);
            var target = Path.GetFullPath(Path.Combine(destination, relative));
            if (entry.FullName.EndsWith('/'))
            {
                Directory.CreateDirectory(target);
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            await using var input = entry.Open();
            await using var output = new FileStream(
                target,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                BufferSize,
                FileOptions.Asynchronous | FileOptions.WriteThrough);
            var buffer = new byte[BufferSize];
            long entryBytes = 0;
            while (true)
            {
                var read = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                entryBytes += read;
                extractedTotal += read;
                if (entryBytes > entry.Length || extractedTotal > MaximumTotalBytes)
                {
                    throw new InvalidDataException(
                        "Installer package produced more data than declared.");
                }

                await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken)
                    .ConfigureAwait(false);
                if (declaredTotal > 0)
                {
                    progress?.Report(Math.Clamp(extractedTotal / (double)declaredTotal, 0, 1));
                }
            }

            if (entryBytes != entry.Length)
            {
                throw new InvalidDataException("Installer package entry length did not match metadata.");
            }
        }

        progress?.Report(1);
    }

    private static void ValidateEntry(ZipArchiveEntry entry)
    {
        var name = entry.FullName;
        if (string.IsNullOrWhiteSpace(name) ||
            name.Contains('\0') ||
            name.StartsWith('/') ||
            name.StartsWith('\\') ||
            Path.IsPathFullyQualified(name.Replace('/', Path.DirectorySeparatorChar)))
        {
            throw new InvalidDataException("Installer package contains an invalid path.");
        }

        var segments = name.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0 || segments.Any(IsUnsafeSegment))
        {
            throw new InvalidDataException("Installer package contains a traversal path.");
        }

        var unixFileType = (entry.ExternalAttributes >> 16) & 0xF000;
        if (unixFileType is not 0 and not 0x8000 and not 0x4000 ||
            ((FileAttributes)entry.ExternalAttributes).HasFlag(FileAttributes.ReparsePoint))
        {
            throw new InvalidDataException("Installer package contains a filesystem link.");
        }
    }

    private static bool IsUnsafeSegment(string segment)
    {
        if (segment is "." or ".." ||
            segment.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
            segment[^1] is ' ' or '.')
        {
            return true;
        }

        var deviceName = segment.Split('.', count: 2)[0];
        return ReservedDeviceNames.Contains(deviceName);
    }
}
